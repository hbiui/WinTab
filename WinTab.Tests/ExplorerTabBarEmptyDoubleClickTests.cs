using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using WinTab.Hooks;

internal static class ExplorerTabBarEmptyDoubleClickTests
{
    public static IEnumerable<(string Name, Func<Task> Body)> All()
    {
        yield return ("tab-bar double-click on empty area reopens and swallows the second down", ReopensAfterDoubleClick);
        yield return ("tab-bar single click never reopens nor swallows", SingleClickDoesNotReopen);
        yield return ("tab-bar double-click rejects timeout distance and window changes", RejectsMismatches);
        yield return ("tab-bar double-click fails closed when the hit test is not an empty area", FailsClosedOnNonEmpty);
        yield return ("tab-bar reopen hook ignores input after stop", StopHookIgnoresInput);
    }

    private sealed class Fixture
    {
        public bool EmptyArea = true;
        public Func<Point, long, ExplorerTabBarClick?>? CaptureOverride;
        public List<nint> WarmedWindows = [];
        public int ReopenCalls;

        public ExplorerTabBarEmptyDoubleClickHook CreateHook()
        {
            var hook = new ExplorerTabBarEmptyDoubleClickHook(
                (point, tick) => CaptureOverride != null ? CaptureOverride(point, tick) : new ExplorerTabBarClick(7, point, tick),
                _ => EmptyArea,
                _ => { ReopenCalls++; return Task.CompletedTask; },
                () => 500, () => 4, () => 4,
                window => WarmedWindows.Add(window));
            hook.StartHook();
            return hook;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Check.That(condition(), "The expected condition was not reached in time");
    }

    private static async Task ReopensAfterDoubleClick()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        try
        {
            var tick = Environment.TickCount64;
            var firstHandled = hook.ProcessMouseDown(new Point(300, 60), tick);
            hook.ProcessMouseUp(new Point(300, 60), tick + 10);
            var secondHandled = hook.ProcessMouseDown(new Point(302, 61), tick + 200);
            Check.That(!firstHandled, "The first down of a fresh gesture must pass through");
            Check.That(secondHandled, "The paired second down over an empty tab bar must be swallowed");
            await WaitForAsync(() => fixture.ReopenCalls == 1);
            Check.Equal(1, fixture.ReopenCalls, "A completed empty tab-bar double click must reopen once");
            Check.Equal(1, fixture.WarmedWindows.Count, "Only the first down should warm the cache");
        }
        finally
        {
            hook.Dispose();
        }
    }

    private static async Task SingleClickDoesNotReopen()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        try
        {
            var tick = Environment.TickCount64;
            var handled = hook.ProcessMouseDown(new Point(300, 60), tick);
            hook.ProcessMouseUp(new Point(300, 60), tick + 10);
            Check.That(!handled, "A first down must never be swallowed");
            await Task.Delay(150);
            Check.Equal(0, fixture.ReopenCalls, "A single click must never reopen");
        }
        finally
        {
            hook.Dispose();
        }
    }

    private static Task RejectsMismatches()
    {
        // Timeout, distance and a different window each must break the pair and pass through.
        var tick = Environment.TickCount64;

        var timeout = new Fixture();
        var timeoutHook = timeout.CreateHook();
        try
        {
            timeoutHook.ProcessMouseDown(new Point(300, 60), tick);
            var secondHandled = timeoutHook.ProcessMouseDown(new Point(300, 60), tick + 700);
            Check.That(!secondHandled, "A timed-out pair must not be swallowed");
        }
        finally { timeoutHook.Dispose(); }

        var far = new Fixture();
        var farHook = far.CreateHook();
        try
        {
            farHook.ProcessMouseDown(new Point(300, 60), tick);
            var secondHandled = farHook.ProcessMouseDown(new Point(320, 60), tick + 200);
            Check.That(!secondHandled, "A too-far pair must not be swallowed");
        }
        finally { farHook.Dispose(); }

        var otherWindow = new Fixture { CaptureOverride = (point, t) => new ExplorerTabBarClick(point.X == 302 ? 8 : 7, point, t) };
        var otherHook = otherWindow.CreateHook();
        try
        {
            otherHook.ProcessMouseDown(new Point(300, 60), tick);
            var secondHandled = otherHook.ProcessMouseDown(new Point(302, 60), tick + 200);
            Check.That(!secondHandled, "A pair across windows must not be swallowed");
        }
        finally { otherHook.Dispose(); }

        return Task.Delay(150).ContinueWith(_ =>
        {
            Check.Equal(0, timeout.ReopenCalls, "A timed-out pair must not reopen");
            Check.Equal(0, far.ReopenCalls, "A too-far pair must not reopen");
            Check.Equal(0, otherWindow.ReopenCalls, "A pair across windows must not reopen");
        });
    }

    private static async Task FailsClosedOnNonEmpty()
    {
        var fixture = new Fixture { EmptyArea = false };
        var hook = fixture.CreateHook();
        try
        {
            var tick = Environment.TickCount64;
            hook.ProcessMouseDown(new Point(300, 60), tick);
            hook.ProcessMouseUp(new Point(300, 60), tick + 10);
            var secondHandled = hook.ProcessMouseDown(new Point(302, 61), tick + 200);
            Check.That(!secondHandled, "A non-empty tab-bar point must fail closed and stay native");
            await Task.Delay(150);
            Check.Equal(0, fixture.ReopenCalls, "A non-empty tab-bar point must never reopen");
        }
        finally
        {
            hook.Dispose();
        }
    }

    private static Task StopHookIgnoresInput()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        hook.StopHook();
        var tick = Environment.TickCount64;
        var handled = hook.ProcessMouseDown(new Point(300, 60), tick);
        hook.ProcessMouseUp(new Point(300, 60), tick + 10);
        Check.That(!handled, "A stopped hook must not swallow input");
        Check.Equal(0, fixture.WarmedWindows.Count, "A stopped hook must not warm the cache");
        Check.Equal(0, fixture.ReopenCalls, "A stopped hook must never reopen");
        hook.Dispose();
        return Task.CompletedTask;
    }
}
