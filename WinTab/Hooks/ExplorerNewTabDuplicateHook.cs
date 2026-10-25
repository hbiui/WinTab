using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using H.Hooks;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>A left click captured on an Explorer window, kept with its event tick for pairing.</summary>
internal sealed record ExplorerNewTabClick(nint Window, Point Point, long Tick);

/// <summary>
/// Makes the new-tab button duplicate the active tab instead of opening the home page (a QtTabBar idea,
/// rebuilt for Win11). The click on the button is swallowed on a low-level mouse hook and the watcher is
/// asked to open the active tab's location again in the same window. The button rectangle comes from the
/// watcher's shared UIA cache; a cold or stale cache fails closed, leaving the native behavior untouched.
/// </summary>
internal sealed class ExplorerNewTabDuplicateHook : IHook
{
    internal const int ButtonSlopPx = 2;

    private readonly Func<Point, long, ExplorerNewTabClick?> _capture;
    private readonly Func<ExplorerNewTabClick, bool> _hitTest;
    private readonly Func<ExplorerNewTabClick, Task> _duplicate;
    private readonly Action _install;
    private readonly TabStripHitTester _tabStrip;
    private readonly bool _ownsTabStrip;
    private LowLevelMouseHook? _lowLevelMouseHook;
    private long _lastFallbackLogAt;
    private volatile bool _active;
    private bool _disposed;

    public bool IsHookActive => _active;

    public ExplorerNewTabDuplicateHook(ExplorerWatcher watcher)
    {
        // Defaults live in the body: the hit test needs a TabStripHitTester and constructor
        // initializers cannot reference instance members.
        _capture = Capture;
        _hitTest = IsOnNewTabButton;
        _duplicate = click => watcher.DuplicateActiveTabAsync(click.Window);
        _install = InstallLowLevelHook;
        // Shared with the watcher and the other tab-bar hooks, which keep it warm; never disposed here.
        _tabStrip = watcher.TabStrip;
        _ownsTabStrip = false;
    }

    internal ExplorerNewTabDuplicateHook(
        Func<Point, long, ExplorerNewTabClick?> capture,
        Func<ExplorerNewTabClick, bool> hitTest,
        Func<ExplorerNewTabClick, Task> duplicate)
    {
        _capture = capture;
        _hitTest = hitTest;
        _duplicate = duplicate;
        _install = () => { };
        _tabStrip = new TabStripHitTester();
        _ownsTabStrip = true;
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
        _lowLevelMouseHook?.Stop();
    }

    private void InstallLowLevelHook()
    {
        var hook = new LowLevelMouseHook
        {
            Handling = true
        };
        hook.Down += OnMouseDown;
        hook.Start();
        _lowLevelMouseHook = hook;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.CurrentKey is not (Key.MouseLeft or Key.LButton))
            return;
        if (ExplorerNavigationAccess.HasModifiers())
            return;
        if (ProcessMouseDown(e.Position, Environment.TickCount64))
            e.IsHandled = true;
    }

    /// <summary>Returns true when the click was consumed as a duplicate-the-active-tab request.</summary>
    internal bool ProcessMouseDown(Point point, long tick)
    {
        if (!_active) return false;
        var click = _capture(point, tick);
        if (click == null || !_hitTest(click))
            return false;
        _ = DuplicateAsync(click);
        return true;
    }

    private static ExplorerNewTabClick? Capture(Point point, long tick)
    {
        var hit = WinApi.WindowFromPoint(point);
        if (hit == 0) return null;
        var root = WinApi.GetAncestor(hit, WinApi.GA_ROOT);
        return root == 0 || !ExplorerWindowDiscovery.IsTabbedAppWindow(root)
            ? null
            : new ExplorerNewTabClick(root, point, tick);
    }

    private bool IsOnNewTabButton(ExplorerNewTabClick click)
    {
        // The UIA cache lags behind real tab changes, so the live tab count read from the window
        // itself decides which cached rectangles can be trusted. Ghosts must never hide the button.
        var liveTabs = ExplorerWindowDiscovery.GetAllExplorerTabs(click.Window).Count();
        var hasRects = _tabStrip.TryGetTabRects(click.Window, out var cachedRects);
        var effective = hasRects ? EffectiveTabRects(cachedRects, liveTabs) : [];
        // The cached button rectangle is only trustworthy when the cache matches the live layout.
        var trusted = effective.Length == cachedRects.Length;
        var button = Rectangle.Empty;
        var hasButton = trusted && _tabStrip.TryGetNewTabButton(click.Window, out button);
        if (hasButton)
        {
            // A tiny slop absorbs the 1-2px trembling seen in real clicks; the cached rect still decides.
            button.Inflate(ButtonSlopPx, ButtonSlopPx);
        }
        var hasRow = _tabStrip.TryGetTabRow(click.Window, out var row);
        var decided = IsNewTabButtonArea(hasButton, button, hasRow, row, effective, click.Point);
        if (!hasRects || liveTabs != cachedRects.Length)
        {
            // A layout change the cache has not absorbed yet: refresh now so the next click decides
            // on the current layout instead of repeating this fallback.
            _tabStrip.ScheduleRefresh(click.Window);
        }
        if (decided && !trusted)
        {
            // Debounced: the live tab count caught a ghost cache and the guard band decided instead.
            var now = Environment.TickCount64;
            if (now - _lastFallbackLogAt > 1_000)
            {
                _lastFallbackLogAt = now;
                ExplorerDebugLog.Write($"New-tab button: ghost cache trimmed (cached={cachedRects.Length} live={liveTabs}), guard band matched");
            }
        }
        return decided;
    }

    /// <summary>
    /// Drops cached tab rectangles whose tabs have already closed: the UIA tree can keep a closed
    /// tab visible for a moment, and such a ghost would read a click on the new-tab button as a
    /// click on that tab. Only trailing rectangles are dropped; a closed middle tab keeps its
    /// neighbours misplaced until the next refresh, which the hooks schedule on their own actions.
    /// </summary>
    internal static Rectangle[] EffectiveTabRects(IReadOnlyList<Rectangle> cachedRects, int liveTabCount)
    {
        if (liveTabCount <= 0 || cachedRects.Count <= liveTabCount)
            return [.. cachedRects];
        return [.. cachedRects.Take(liveTabCount)];
    }

    /// <summary>
    /// The click is the new-tab button when it hits the cached button rectangle, or — when the
    /// button could not be identified on this build — when it lands in the guard band just right of
    /// the last live tab inside the tab row. The double-click reopen hook has used that same band
    /// to exclude the button since f12, so its width is field-proven; the band never covers a live
    /// tab title.
    /// </summary>
    internal static bool IsNewTabButtonArea(bool hasButton, Rectangle buttonRect, bool hasRow,
        Rectangle row, IReadOnlyList<Rectangle> tabs, Point point)
    {
        if (hasButton && buttonRect.Contains(point))
            return true;
        if (!hasRow || !row.Contains(point))
            return false;
        if (tabs.Any(rectangle => rectangle.Contains(point)))
            return false;
        int? edge = tabs.Count == 0 ? null : tabs.Max(rectangle => rectangle.Right);
        return edge is { } right && point.X > right &&
               point.X <= right + ExplorerTabBarEmptyDoubleClickHook.NewTabButtonGuardPx;
    }

    private async Task DuplicateAsync(ExplorerNewTabClick click)
    {
        try
        {
            await _duplicate(click).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"New-tab button duplicate failed: {exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            // The duplicate changed the layout; do not let the next click decide on stale geometry.
            _tabStrip.ScheduleRefresh(click.Window);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopHook();
        var hook = _lowLevelMouseHook;
        _lowLevelMouseHook = null;
        hook?.Dispose();
        if (_ownsTabStrip)
            _tabStrip.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
