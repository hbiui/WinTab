using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using H.Hooks;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>A left click captured on an Explorer window, kept with its event tick for pairing.</summary>
internal sealed record ExplorerTabBarClick(nint Window, Point Point, long Tick);

/// <summary>
/// Reopens the most recently closed tab when the user double-clicks an empty area of the tab bar
/// (QTTabBar-style, rebuilt for Win11). The gesture runs on a low-level mouse hook so the paired
/// second down can be swallowed: Win11 treats the tab bar as the title bar and its native
/// double-click maximizes/restores the window, so the only way to keep that from firing is to eat
/// the event. The first down only warms the UIA cache; the second down decides synchronously from
/// the cache (a cold cache fails closed: no swallow, no reopen, native behavior stays). The point
/// must land inside the tab row but on no tab title and not on the new-tab button.
/// </summary>
internal sealed class ExplorerTabBarEmptyDoubleClickHook : IHook
{
    internal const int NewTabButtonGuardPx = 48;

    private readonly object _gate = new();
    private readonly Func<Point, long, ExplorerTabBarClick?> _capture;
    private readonly Func<ExplorerTabBarClick, bool> _hitTest;
    private readonly Func<ExplorerTabBarClick, Task> _reopen;
    private readonly Func<int> _doubleClickTimeMs;
    private readonly Func<int> _doubleClickWidthPx;
    private readonly Func<int> _doubleClickHeightPx;
    private readonly Action<nint> _warmCache;
    private readonly Action _install;
    private readonly TabStripHitTester _tabStrip;
    private LowLevelMouseHook? _lowLevelMouseHook;
    private ExplorerTabBarClick? _firstDown;
    private bool _leftDown;
    private volatile bool _active;
    private bool _disposed;

    public bool IsHookActive => _active;

    public ExplorerTabBarEmptyDoubleClickHook(ExplorerWatcher watcher)
    {
        // Defaults live in the body: the empty-area hit test needs this hook's own TabStripHitTester,
        // and constructor initializers cannot reference instance members.
        _capture = Capture;
        _hitTest = IsEmptyTabBarArea;
        _reopen = click => watcher.ReopenClosedTabAsync();
        _doubleClickTimeMs = () => (int)WinApi.GetDoubleClickTime();
        _doubleClickWidthPx = () => WinApi.GetSystemMetrics(36);
        _doubleClickHeightPx = () => WinApi.GetSystemMetrics(37);
        _warmCache = window => _tabStrip.ScheduleRefresh(window);
        _install = InstallLowLevelHook;
        _tabStrip = new TabStripHitTester();
    }

    internal ExplorerTabBarEmptyDoubleClickHook(
        Func<Point, long, ExplorerTabBarClick?> capture,
        Func<ExplorerTabBarClick, bool> hitTest,
        Func<ExplorerTabBarClick, Task> reopen,
        Func<int> doubleClickTimeMs,
        Func<int> doubleClickWidthPx,
        Func<int> doubleClickHeightPx,
        Action<nint> warmCache)
    {
        _capture = capture;
        _hitTest = hitTest;
        _reopen = reopen;
        _doubleClickTimeMs = doubleClickTimeMs;
        _doubleClickWidthPx = doubleClickWidthPx;
        _doubleClickHeightPx = doubleClickHeightPx;
        _warmCache = warmCache;
        _install = () => { };
        _tabStrip = new TabStripHitTester();
    }

    public void StartHook()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active) return;
        _install();
        _active = true;
    }

    public void StopHook()
    {
        if (!_active) return;
        _active = false;
        CancelPending();
        _lowLevelMouseHook?.Stop();
    }

    private void InstallLowLevelHook()
    {
        var hook = new LowLevelMouseHook
        {
            Handling = true
        };
        hook.Down += OnMouseDown;
        hook.Up += OnMouseUp;
        hook.Wheel += OnWheel;
        hook.Start();
        _lowLevelMouseHook = hook;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.CurrentKey is not (Key.MouseLeft or Key.LButton))
        {
            CancelPending();
            return;
        }
        if (ExplorerNavigationAccess.HasModifiers())
        {
            CancelPending();
            return;
        }
        if (ProcessMouseDown(e.Position, Environment.TickCount64))
            e.IsHandled = true;
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.CurrentKey is not (Key.MouseLeft or Key.LButton))
            return;
        ProcessMouseUp(e.Position, Environment.TickCount64);
    }

    private void OnWheel(object? sender, MouseEventArgs e) => CancelPending();

    /// <summary>
    /// Handles a left-button down. Returns true when the second down of an empty-area double click
    /// was consumed; the caller must swallow it so the native title-bar maximize never fires.
    /// </summary>
    internal bool ProcessMouseDown(Point point, long tick)
    {
        var click = _capture(point, tick);
        ExplorerTabBarClick? first;
        bool paired;
        lock (_gate)
        {
            if (!_active) return false;
            if (_leftDown)
            {
                // A down without a matching up (button spam) restarts cleanly.
                _leftDown = false;
                _firstDown = null;
                return false;
            }
            _leftDown = true;

            if (click == null)
            {
                _firstDown = null;
                return false;
            }

            if (_firstDown is { } candidate && IsDoubleClickPair(candidate, click))
            {
                // The gesture is consumed here no matter what the hit tests decide below.
                _leftDown = false;
                first = candidate;
                _firstDown = null;
                paired = true;
            }
            else
            {
                _firstDown = click;
                paired = false;
                first = null;
            }
        }

        if (!paired)
        {
            // First down of a possible gesture: warm the UIA cache so the second down can decide
            // synchronously while we are still inside the hook.
            _warmCache(click.Window);
            return false;
        }

        // Synchronous, cache-backed hit test inside a low-level hook: it must be quick, which is
        // exactly why the first down warmed the cache. A cold cache fails closed.
        var emptyFirst = _hitTest(first!);
        var emptySecond = emptyFirst && _hitTest(click);
        if (!emptyFirst || !emptySecond)
            return false;

        _ = ReopenAsync(click);
        return true;
    }

    internal void ProcessMouseUp(Point point, long tick)
    {
        lock (_gate)
        {
            _leftDown = false;
        }
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

    private bool IsEmptyTabBarArea(ExplorerTabBarClick click)
    {
        if (!_tabStrip.TryGetTabRow(click.Window, out var band) || !band.Contains(click.Point))
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

    private async Task ReopenAsync(ExplorerTabBarClick click)
    {
        try
        {
            await _reopen(click).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Tab-bar double-click reopen failed: {exception.GetType().Name}:{exception.Message}");
        }
    }

    private void CancelPending()
    {
        lock (_gate)
        {
            _leftDown = false;
            _firstDown = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopHook();
        var hook = _lowLevelMouseHook;
        _lowLevelMouseHook = null;
        hook?.Dispose();
        _tabStrip.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
