using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>
/// Shows a preview card after the cursor dwells on one media file in an Explorer file list. The cursor must
/// rest on the same item across the whole dwell; any input, foreground change or move hides the card. Every
/// late asynchronous result is retired by a generation counter, mirroring the double-click hook's strictness.
/// </summary>
internal sealed class ExplorerFileHoverPreviewHook : IHook
{
    internal const int DwellTicksToIdentify = 3;   // ~300 ms: cheap cursor polling only
    internal const int DwellTicksToShow = 6;       // ~600 ms: the user's chosen threshold

    private readonly object _gate = new();
    private readonly ExplorerWatcher _watcher;
    private readonly Func<Point> _cursor;
    private readonly Func<Point, ExplorerFileHoverItem?> _identify;
    private readonly Func<ExplorerFileHoverItem, ExplorerWatcher, string?> _resolvePath;
    private readonly Func<ExplorerFileHoverItem, string, CancellationToken, Task<ExplorerMediaPreview?>> _resolve;
    private readonly Func<IExplorerPreviewWindow> _createWindow;
    private readonly Func<ExplorerFileHoverItem, bool> _stillCurrent;
    private readonly Func<Action, Task> _marshalToUi;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;
    private IDisposable? _input;
    private IExplorerPreviewWindow? _window;
    private Point _point;
    private ExplorerFileHoverItem? _item;
    private int _stableTicks;
    private int _generation;
    private CancellationTokenSource? _pending;
    private volatile bool _active;
    private bool _disposed;

    public bool IsHookActive => _active;

    public ExplorerFileHoverPreviewHook(NavigationInputSource input, ExplorerWatcher watcher)
        : this(watcher, hook => input.Subscribe(hook.OnPointer, hook.OnCancel, hook.OnForeground))
    {
    }

    internal ExplorerFileHoverPreviewHook(ExplorerWatcher watcher,
        Func<ExplorerFileHoverPreviewHook, IDisposable> observeInput)
    {
        // The card is WPF: it must be created and shown on WinTab's UI (STA) thread. StartHook captures
        // that dispatcher; every late asynchronous result hops back through it before touching UI.
        _watcher = watcher;
        _observeInput = observeInput;
        _cursor = () => WinApi.GetCursorPos(out var point) ? point : default;
        _identify = point => ExplorerFileHoverIdentity.TryCapture(point, out var item) ? item : null;
        _resolvePath = (item, watcher_) => ResolvePath(item, watcher_);
        _resolve = (item, path, token) => ExplorerMediaPreviewContent.ResolveAsync(item, path, token);
        _createWindow = () => new UI.Views.Controls.MediaPreviewWindow();
        // The preview follows the cursor, not the foreground app: UIA hit-testing works while any
        // other program is active, so the card shows as long as the Explorer window is visible.
        _stillCurrent = item => WinApi.IsWindowVisible(item.Window.Handle);
        _marshalToUi = action => MarshalToUi(action);
    }

    private Task MarshalToUi(Action action)
    {
        var dispatcher = _dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.InvokeAsync(action).Task;
    }

    internal ExplorerFileHoverPreviewHook(
        ExplorerWatcher watcher,
        Func<ExplorerFileHoverPreviewHook, IDisposable> observeInput,
        Func<Point> cursor,
        Func<Point, ExplorerFileHoverItem?> identify,
        Func<ExplorerFileHoverItem, ExplorerWatcher, string?> resolvePath,
        Func<ExplorerFileHoverItem, string, CancellationToken, Task<ExplorerMediaPreview?>> resolve,
        Func<IExplorerPreviewWindow> createWindow,
        Func<ExplorerFileHoverItem, bool> stillCurrent,
        Func<Action, Task> marshalToUi)
    {
        _watcher = watcher;
        _observeInput = observeInput;
        _cursor = cursor;
        _identify = identify;
        _resolvePath = resolvePath;
        _resolve = resolve;
        _createWindow = createWindow;
        _stillCurrent = stillCurrent;
        _marshalToUi = marshalToUi;
    }

    private readonly Func<ExplorerFileHoverPreviewHook, IDisposable> _observeInput;

    public void StartHook()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active) return;
        _active = true;
        try
        {
            _input = _observeInput(this);
        }
        catch
        {
            _active = false;
            throw;
        }
        Reset();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => ProcessTick();
        _timer.Start();
    }

    public void StopHook()
    {
        if (!_active) return;
        _active = false;
        lock (_gate)
        {
            _generation++;
            _item = null;
            _stableTicks = 0;
            _pending?.Cancel();
            _pending = null;
        }
        _timer?.Stop();
        _timer = null;
        HideWindow();
        _input?.Dispose();
        _input = null;
    }

    /// <summary>One poll of the cursor. The real timer calls this; tests drive it directly.</summary>
    internal void ProcessTick()
    {
        if (!_active) return;
        try
        {
            var point = _cursor();
            lock (_gate)
            {
                if (point == _point)
                {
                    _stableTicks++;
                    AdvanceDwellLocked();
                    return;
                }
            }

            // The cursor moved: anything from an earlier gesture is retired.
            CancelPendingLocked();
            lock (_gate)
            {
                _point = point;
                _item = null;
                _stableTicks = 1;
            }
            HideWindow();
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Hover tick failed: {exception.GetType().Name}:{exception.Message}");
        }
    }

    private void AdvanceDwellLocked()
    {
        if (_stableTicks == DwellTicksToIdentify)
        {
            var point = _point;
            var generation = _generation;
            Task.Run(() =>
            {
                var captured = _identify(point);
                lock (_gate)
                {
                    if (!_active || generation != _generation || point != _point) return;
                    _item = captured;
                }
            });
            return;
        }

        if (_stableTicks < DwellTicksToShow || _item == null)
            return;

        var item = _item;
        if (!ExplorerMediaPreviewContent.IsMediaFile(item.Name) || _pending != null || _window is { Visible: true })
            return;

        var path = _resolvePath(item, _watcher);
        if (path == null)
            return;

        var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        var requestGeneration = _generation;
        _ = ResolveAndShowAsync(item, path, requestGeneration, cancellation);
    }

    private async Task ResolveAndShowAsync(ExplorerFileHoverItem item, string path, int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            var preview = await _resolve(item, path, cancellation.Token).ConfigureAwait(false);
            // Resolution may finish on a thread-pool (MTA) thread; WPF windows can only be created and
            // shown on the STA UI thread, so the final hop always goes through the UI dispatcher.
            await _marshalToUi(() =>
            {
                var show = !cancellation.IsCancellationRequested && preview != null &&
                           generation == _generation && _active &&
                           _stillCurrent(item) &&
                           ExplorerFileHoverIdentity.IsSameItem(item, CurrentItem());
                if (!show || preview == null)
                    return;
                var window = _window ??= _createWindow();
                window.Show(preview, item.Point);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Hover preview failed: {exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            cancellation.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cancellation))
                    _pending = null;
            }
        }
    }

    private ExplorerFileHoverItem? CurrentItem()
    {
        lock (_gate)
            return _item;
    }

    internal void OnPointer(NavigationPointerInput input)
    {
        if (input.Kind is NavigationPointerKind.LeftDown or NavigationPointerKind.MiddleDown
            or NavigationPointerKind.OtherDown)
            ResetWithHide();
    }

    internal void OnCancel() => ResetWithHide();

    internal void OnForeground(nint window)
    {
        var root = WinApi.GetAncestor(window, WinApi.GA_ROOT);
        if (root != 0) window = root;
        var item = CurrentItem();
        if (item == null || item.Window.Handle == window)
            return;
        // Another program became foreground, but the cursor may still rest on the Explorer file;
        // retire the hover only when the cursor has actually left that window.
        if (WinApi.GetCursorPos(out var point) && WinApi.WindowFromPoint(point) != 0 &&
            WinApi.GetAncestor(WinApi.WindowFromPoint(point), WinApi.GA_ROOT) == item.Window.Handle)
            return;
        ResetWithHide();
    }

    internal void ResetWithHide()
    {
        CancelPendingLocked();
        lock (_gate)
        {
            _item = null;
            _stableTicks = 0;
        }
        HideWindow();
    }

    private void Reset()
    {
        lock (_gate)
        {
            _point = new Point(int.MinValue, int.MinValue);
            _item = null;
            _stableTicks = 0;
        }
    }

    private void CancelPendingLocked()
    {
        lock (_gate)
        {
            _generation++;
            _pending?.Cancel();
            _pending = null;
        }
    }

    private void HideWindow()
    {
        var window = _window;
        if (window == null) return;
        // Input and foreground notifications arrive on the raw-input observer thread; hiding is also a
        // WPF UI-thread operation, so it hops through the same dispatcher as showing.
        try { _marshalToUi(window.Hide); }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Preview hide failed: {exception.GetType().Name}:{exception.Message}");
        }
    }

    /// <summary>
    /// Combines the watcher's location snapshot with the hovered item's name. Only real filesystem paths
    /// (drive letters or UNC) produce a preview; virtual locations such as Home or the Recycle Bin fail closed.
    /// </summary>
    private static string? ResolvePath(ExplorerFileHoverItem item, ExplorerWatcher watcher)
    {
        var location = watcher.TryGetFileListLocationSnapshot(item.Window, item.Tab);
        if (string.IsNullOrWhiteSpace(location) || item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var combined = Path.Combine(location, item.Name);
        var root = Path.GetPathRoot(combined);
        // A filesystem root is a drive letter with a slash or a UNC share; shell namespaces ("::{...}") are not.
        if (string.IsNullOrEmpty(root) ||
            (!char.IsLetter(root[0]) || root.Length < 3 || root[1] != ':') && !root.StartsWith(@"\\"))
            return null;
        return combined;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopHook();
        _window?.Dispose();
        _window = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
