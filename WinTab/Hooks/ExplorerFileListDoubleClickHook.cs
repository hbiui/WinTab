using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>
/// Observes native left clicks without consuming them, validates both file-list backgrounds asynchronously,
/// and permits one short-lived parent-navigation request per completed double click.
/// </summary>
internal sealed class ExplorerFileListDoubleClickHook : IHook
{
    private const int RequestLifetimeMs = 1_500;
    private readonly object _gate = new();
    private readonly Func<ExplorerFileListDoubleClickHook, IDisposable> _observeInput;
    private readonly Func<Point, long, ExplorerFileListClick?> _capture;
    private readonly Func<ExplorerFileListClick, CancellationToken, Task<bool>> _hitTest;
    private readonly Func<ExplorerFileListDoubleClickRequest, Func<bool>, CancellationToken, Task<bool>> _navigate;
    private readonly Func<ExplorerFileListDoubleClickRequest, bool> _isCurrent;
    private readonly ExplorerFileListDoubleClickState _state;
    private IDisposable? _input;
    private CancellationTokenSource? _requestCancellation;
    private HitCandidate? _firstHit;
    private HitCandidate? _secondHit;
    private nint _gestureWindow;
    private int _generation;
    private bool _leftDown;
    private volatile bool _active;
    private bool _disposed;

    private sealed record HitCandidate(ExplorerFileListClick Click, Task<bool> IsEmpty);

    internal ExplorerFileListDoubleClickHook(NavigationInputSource input, ExplorerWatcher watcher)
        : this(hook => input.Subscribe(hook.ProcessPointer, hook.CancelForKeyboard, hook.OnForeground),
            (point, tick) => Capture(watcher, point, tick),
            (click, _) => Task.Run(() => ExplorerFileListAutomation.IsEmptyArea(click)),
            watcher.NavigateFileListParentAsync,
            IsCurrent,
            new ExplorerFileListDoubleClickState(
                () => (int)WinApi.GetDoubleClickTime(),
                () => WinApi.GetSystemMetrics(36),
                () => WinApi.GetSystemMetrics(37)))
    {
    }

    internal ExplorerFileListDoubleClickHook(
        Func<ExplorerFileListDoubleClickHook, IDisposable> observeInput,
        Func<Point, long, ExplorerFileListClick?> capture,
        Func<ExplorerFileListClick, CancellationToken, Task<bool>> hitTest,
        Func<ExplorerFileListDoubleClickRequest, Func<bool>, CancellationToken, Task<bool>> navigate,
        Func<ExplorerFileListDoubleClickRequest, bool> isCurrent,
        ExplorerFileListDoubleClickState state)
    {
        _observeInput = observeInput;
        _capture = capture;
        _hitTest = hitTest;
        _navigate = navigate;
        _isCurrent = isCurrent;
        _state = state;
    }

    public bool IsHookActive => _active;

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
    }

    public void StopHook()
    {
        IDisposable? input;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _active = false;
            _generation++;
            _state.Reset();
            _leftDown = false;
            _firstHit = null;
            _secondHit = null;
            _gestureWindow = 0;
            input = _input;
            _input = null;
            cancellation = _requestCancellation;
            _requestCancellation = null;
        }
        cancellation?.Cancel();
        // Never dispose the shared observer while holding _gate: its input thread may be finishing a callback.
        input?.Dispose();
    }

    private static ExplorerFileListClick? Capture(ExplorerWatcher watcher, Point point, long tick)
    {
        if (!ExplorerFileListAutomation.TryCaptureView(point, out var window, out var tab, out var view))
            return null;
        var location = watcher.TryGetFileListLocationSnapshot(window, tab);
        return string.IsNullOrWhiteSpace(location)
            ? null
            : new ExplorerFileListClick(window, tab, view, point, location, tick);
    }

    internal void ProcessPointer(NavigationPointerInput input)
    {
        if (!_active) return;
        if (input.FromWinTab || ExplorerNavigationAccess.HasModifiers() ||
            input.Kind == NavigationPointerKind.OtherDown || input.Kind == NavigationPointerKind.MiddleDown)
        {
            CancelPending();
            return;
        }
        if (input.Kind != NavigationPointerKind.LeftDown && input.Kind != NavigationPointerKind.LeftUp)
            return;

        var tick = Environment.TickCount64 - Math.Max(0, input.DelayMs);
        if (input.Kind == NavigationPointerKind.LeftDown)
        {
            int generation;
            lock (_gate)
            {
                if (!_active) return;
                if (_leftDown)
                {
                    CancelPendingLocked(out var repeatedCancellation);
                    repeatedCancellation?.Cancel();
                    return;
                }
                _leftDown = true;
                _generation++;
                generation = _generation;
                _requestCancellation?.Cancel();
                _requestCancellation = null;
                _secondHit = null;
            }

            var click = _capture(input.Point, tick);
            var hit = click == null ? null : _hitTest(click, CancellationToken.None);
            lock (_gate)
            {
                if (!_active || generation != _generation) return;
                var candidate = click == null || hit == null ? null : new HitCandidate(click, hit);
                var first = _state.HandleLeftDown(click);
                if (first != null && candidate != null)
                {
                    _firstHit = candidate;
                    _secondHit = null;
                    _gestureWindow = first.Window.Handle;
                }
                else if (click != null && candidate != null && _firstHit != null)
                {
                    _secondHit = candidate;
                }
                else
                {
                    _firstHit = null;
                    _secondHit = null;
                    _gestureWindow = 0;
                }
            }
            return;
        }

        ExplorerFileListDoubleClickRequest? request;
        HitCandidate? firstHit;
        HitCandidate? secondHit;
        CancellationTokenSource? cancellation = null;
        int requestGeneration;
        lock (_gate)
        {
            if (!_active) return;
            _leftDown = false;
            request = _state.HandleLeftUp(input.Point, tick);
            firstHit = _firstHit;
            secondHit = _secondHit;
            requestGeneration = _generation;
            if (request != null)
            {
                _firstHit = null;
                _secondHit = null;
                if (firstHit != null && secondHit != null &&
                    ReferenceEquals(request.First, firstHit.Click) && ReferenceEquals(request.Second, secondHit.Click))
                {
                    cancellation = new CancellationTokenSource();
                    _requestCancellation = cancellation;
                }
                else
                {
                    request = null;
                    _gestureWindow = 0;
                }
            }
        }

        if (request != null && firstHit != null && secondHit != null && cancellation != null)
            _ = ProcessRequestAsync(request, firstHit.IsEmpty, secondHit.IsEmpty, requestGeneration, cancellation);
    }

    private static bool IsCurrent(ExplorerFileListDoubleClickRequest request) =>
        ExplorerNavigationAccess.ForegroundFrame() == request.Second.Window.Handle &&
        ExplorerFileListAutomation.IsCurrent(request.First) &&
        ExplorerFileListAutomation.IsCurrent(request.Second);

    private async Task ProcessRequestAsync(ExplorerFileListDoubleClickRequest request, Task<bool> firstHit,
        Task<bool> secondHit, int generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool Current()
        {
            var age = Environment.TickCount64 - request.Second.Tick;
            return _active && generation == _generation && !token.IsCancellationRequested &&
                   age >= 0 && age <= RequestLifetimeMs &&
                   _isCurrent(request);
        }

        try
        {
            var remaining = RequestLifetimeMs - (Environment.TickCount64 - request.Second.Tick);
            if (remaining <= 0 || !Current()) return;
            var hits = await Task.WhenAll(firstHit, secondHit)
                .WaitAsync(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
            if (!Current() || !hits[0] || !hits[1]) return;
            await _navigate(request, Current, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"File-list double-click request failed: {exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_requestCancellation, cancellation))
                {
                    _requestCancellation = null;
                    _gestureWindow = 0;
                }
            }
            cancellation.Dispose();
        }
    }

    private void OnForeground(nint window)
    {
        var root = WinApi.GetAncestor(window, WinApi.GA_ROOT);
        if (root != 0) window = root;
        lock (_gate)
        {
            if (_gestureWindow == 0 || window == _gestureWindow) return;
        }
        CancelPending();
    }

    private void CancelForKeyboard() => CancelPending();

    private void CancelPending()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
            CancelPendingLocked(out cancellation);
        cancellation?.Cancel();
    }

    private void CancelPendingLocked(out CancellationTokenSource? cancellation)
    {
        _generation++;
        _state.Reset();
        _leftDown = false;
        _firstHit = null;
        _secondHit = null;
        _gestureWindow = 0;
        cancellation = _requestCancellation;
        _requestCancellation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopHook();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
