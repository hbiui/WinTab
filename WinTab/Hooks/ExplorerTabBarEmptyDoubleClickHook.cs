using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>A left click captured on an Explorer window, kept with its event tick for pairing.</summary>
internal sealed record ExplorerTabBarClick(nint Window, Point Point, long Tick);

/// <summary>
/// Reopens the most recently closed tab when the user double-clicks an empty area of the tab bar
/// (QTTabBar-style, rebuilt for Win11). The gesture is observed without consuming input and must
/// land inside the tab row but on no tab title and not on the new-tab button; anything else fails
/// closed. Reopening itself reuses the watcher's closed-tab history, so the tab returns in the
/// window whose empty tab bar was double-clicked (that window is foreground by the second click).
/// </summary>
internal sealed class ExplorerTabBarEmptyDoubleClickHook : IHook
{
    private const int RequestLifetimeMs = 1_500;
    internal const int NewTabButtonGuardPx = 48;

    private readonly object _gate = new();
    private readonly Func<ExplorerTabBarEmptyDoubleClickHook, IDisposable> _observeInput;
    private readonly Func<Point, long, ExplorerTabBarClick?> _capture;
    private readonly Func<ExplorerTabBarClick, CancellationToken, Task<bool>> _hitTest;
    private readonly Func<ExplorerTabBarClick, CancellationToken, Task> _reopen;
    private readonly Func<ExplorerTabBarClick, bool> _isCurrent;
    private readonly Func<int> _doubleClickTimeMs;
    private readonly Func<int> _doubleClickWidthPx;
    private readonly Func<int> _doubleClickHeightPx;
    private readonly TabStripHitTester _tabStrip;
    private IDisposable? _input;
    private CancellationTokenSource? _requestCancellation;
    private ExplorerTabBarClick? _firstDown;
    private ExplorerTabBarClick? _secondDown;
    private nint _gestureWindow;
    private int _generation;
    private bool _leftDown;
    private volatile bool _active;
    private bool _disposed;

    public bool IsHookActive => _active;

    public ExplorerTabBarEmptyDoubleClickHook(NavigationInputSource input, ExplorerWatcher watcher)
    {
        // Defaults live in the body: the empty-area hit test needs this hook's own TabStripHitTester,
        // and constructor initializers cannot reference instance members.
        _observeInput = hook => input.Subscribe(hook.ProcessPointer, hook.CancelForKeyboard, hook.OnForeground);
        _capture = Capture;
        _hitTest = IsEmptyTabBarAreaAsync;
        _reopen = (_, _) => watcher.ReopenClosedTabAsync();
        _isCurrent = click => ExplorerNavigationAccess.ForegroundFrame() == click.Window;
        _doubleClickTimeMs = () => (int)WinApi.GetDoubleClickTime();
        _doubleClickWidthPx = () => WinApi.GetSystemMetrics(36);
        _doubleClickHeightPx = () => WinApi.GetSystemMetrics(37);
        _tabStrip = new TabStripHitTester();
    }

    internal ExplorerTabBarEmptyDoubleClickHook(
        Func<ExplorerTabBarEmptyDoubleClickHook, IDisposable> observeInput,
        Func<Point, long, ExplorerTabBarClick?> capture,
        Func<ExplorerTabBarClick, CancellationToken, Task<bool>> hitTest,
        Func<ExplorerTabBarClick, CancellationToken, Task> reopen,
        Func<ExplorerTabBarClick, bool> isCurrent,
        Func<int> doubleClickTimeMs,
        Func<int> doubleClickWidthPx,
        Func<int> doubleClickHeightPx,
        TabStripHitTester? tabStrip = null)
    {
        _observeInput = observeInput;
        _capture = capture;
        _hitTest = hitTest;
        _reopen = reopen;
        _isCurrent = isCurrent;
        _doubleClickTimeMs = doubleClickTimeMs;
        _doubleClickWidthPx = doubleClickWidthPx;
        _doubleClickHeightPx = doubleClickHeightPx;
        _tabStrip = tabStrip ?? new TabStripHitTester();
    }

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
        if (!_active) return;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _active = false;
            _generation++;
            _leftDown = false;
            _firstDown = null;
            _secondDown = null;
            _gestureWindow = 0;
            cancellation = _requestCancellation;
            _requestCancellation = null;
        }
        cancellation?.Cancel();
        _input?.Dispose();
        _input = null;
    }

    private static ExplorerTabBarClick? Capture(Point point, long tick)
    {
        var hit = WinApi.WindowFromPoint(point);
        if (hit == 0) return null;
        var root = WinApi.GetAncestor(hit, WinApi.GA_ROOT);
        return root == 0 || !ExplorerWindowDiscovery.IsTabbedAppWindow(root)
            ? null
            : new ExplorerTabBarClick(root, point, tick);
    }

    private async Task<bool> IsEmptyTabBarAreaAsync(ExplorerTabBarClick click, CancellationToken token)
    {
        var row = await _tabStrip.GetTabRowAsync(click.Window, token).ConfigureAwait(false);
        if (row is not { } band || !band.Contains(click.Point))
            return false;
        // != false: a cold cache (null) must fail closed just like a point on a tab title.
        if (_tabStrip.HitTestTab(click.Point, click.Window) != false)
            return false;
        // The new-tab button sits to the right of the last tab inside the same row band; keep a
        // guard band so a double-click there never resurrects a tab behind the user's back.
        var rightmost = _tabStrip.TryGetRightmostTabRight(click.Window);
        if (rightmost is { } edge && click.Point.X > edge && click.Point.X <= edge + NewTabButtonGuardPx)
            return false;
        return true;
    }

    internal void ProcessPointer(NavigationPointerInput input)
    {
        if (!_active) return;
        if (input.FromWinTab || ExplorerNavigationAccess.HasModifiers() ||
            input.Kind is NavigationPointerKind.OtherDown or NavigationPointerKind.MiddleDown)
        {
            CancelPending();
            return;
        }
        if (input.Kind is not (NavigationPointerKind.LeftDown or NavigationPointerKind.LeftUp))
            return;

        var tick = Environment.TickCount64 - Math.Max(0, input.DelayMs);
        if (input.Kind == NavigationPointerKind.LeftDown)
        {
            var click = _capture(input.Point, tick);
            lock (_gate)
            {
                if (!_active) return;
                if (_leftDown)
                {
                    // A down without a matching up (third button spam) restarts cleanly.
                    _leftDown = false;
                    _firstDown = null;
                    _secondDown = null;
                    _gestureWindow = 0;
                    return;
                }
                _leftDown = true;

                if (click == null)
                {
                    _firstDown = null;
                    _secondDown = null;
                    _gestureWindow = 0;
                    return;
                }

                if (_firstDown is { } first && IsDoubleClickPair(first, click))
                {
                    _secondDown = click;
                    _gestureWindow = click.Window;
                }
                else
                {
                    _firstDown = click;
                    _secondDown = null;
                    _gestureWindow = 0;
                }
            }
            return;
        }

        // Left up: the first up only lifts the button and keeps the candidate; the up after the
        // paired second down completes the double click and hands the pair to the reopen check.
        ExplorerTabBarClick? firstClick;
        ExplorerTabBarClick? secondClick;
        lock (_gate)
        {
            if (!_active) return;
            _leftDown = false;
            firstClick = _firstDown;
            secondClick = _secondDown;
            if (secondClick == null || firstClick == null)
                return;
            // The gesture is consumed regardless of what the async checks decide.
            _firstDown = null;
            _secondDown = null;
            _gestureWindow = 0;
        }
        _ = ProcessReopenAsync(firstClick, secondClick);
    }

    private bool IsDoubleClickPair(ExplorerTabBarClick first, ExplorerTabBarClick second)
    {
        if (second.Window != first.Window)
            return false;
        var elapsed = second.Tick - first.Tick;
        if (elapsed < 0 || elapsed > _doubleClickTimeMs())
            return false;
        return Math.Abs(second.Point.X - first.Point.X) <= _doubleClickWidthPx() &&
               Math.Abs(second.Point.Y - first.Point.Y) <= _doubleClickHeightPx();
    }

    private async Task ProcessReopenAsync(ExplorerTabBarClick first, ExplorerTabBarClick second)
    {
        var cancellation = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            if (!_active)
            {
                cancellation.Dispose();
                return;
            }
            _generation++;
            generation = _generation;
            _requestCancellation = cancellation;
        }

        var token = cancellation.Token;
        try
        {
            var age = Math.Max(0, Environment.TickCount64 - second.Tick);
            var remaining = RequestLifetimeMs - age;
            if (remaining <= 0) return;
            var hits = await Task.WhenAll(_hitTest(first, token), _hitTest(second, token))
                .WaitAsync(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
            var stillCurrent = _active && generation == _generation && !token.IsCancellationRequested &&
                               Environment.TickCount64 - second.Tick <= RequestLifetimeMs &&
                               _isCurrent(second) && second.Window == first.Window;
            if (!stillCurrent || !hits[0] || !hits[1])
                return;
            await _reopen(second, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Tab-bar double-click reopen failed: {exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_requestCancellation, cancellation))
                    _requestCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    internal void OnForeground(nint window)
    {
        var root = WinApi.GetAncestor(window, WinApi.GA_ROOT);
        if (root != 0) window = root;
        lock (_gate)
        {
            if (_gestureWindow == 0 || window == _gestureWindow) return;
        }
        CancelPending();
    }

    internal void CancelForKeyboard() => CancelPending();

    private void CancelPending()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _generation++;
            _leftDown = false;
            _firstDown = null;
            _secondDown = null;
            _gestureWindow = 0;
            cancellation = _requestCancellation;
            _requestCancellation = null;
        }
        cancellation?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopHook();
        _tabStrip.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
