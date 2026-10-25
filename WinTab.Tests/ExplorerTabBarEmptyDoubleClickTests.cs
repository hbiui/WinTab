using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinTab.Hooks;

internal static class ExplorerTabBarEmptyDoubleClickTests
{
    public static IEnumerable<(string Name, Func<Task> Body)> All()
    {
        yield return ("tab-bar double-click on empty area reopens the closed tab", ReopensAfterDoubleClick);
        yield return ("tab-bar single click never reopens", SingleClickDoesNotReopen);
        yield return ("tab-bar double-click rejects timeout distance and window changes", RejectsMismatches);
        yield return ("tab-bar double-click fails closed when the hit test is not an empty area", FailsClosedOnNonEmpty);
        yield return ("tab-bar double-click stop retires an unfinished reopen", StopRetiresRequest);
    }

    private sealed class Disposer : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Fixture
    {
        public bool EmptyArea = true;
        public bool PendingHitTest;
        public TaskCompletionSource<bool> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Point, long, ExplorerTabBarClick?>? CaptureOverride;
        public Func<ExplorerTabBarClick, bool>? IsCurrentOverride;
        public int ReopenCalls;

        public ExplorerTabBarEmptyDoubleClickHook CreateHook()
        {
            var hook = new ExplorerTabBarEmptyDoubleClickHook(
                _ => new Disposer(),
                (point, tick) => CaptureOverride != null ? CaptureOverride(point, tick) : new ExplorerTabBarClick(7, point, tick),
                (_, token) => PendingHitTest ? Gate.Task.WaitAsync(token) : Task.FromResult(EmptyArea),
                (_, _) => { ReopenCalls++; return Task.CompletedTask; },
                click => IsCurrentOverride?.Invoke(click) ?? true,
                () => 500, () => 4, () => 4);
            hook.StartHook();
            return hook;
        }
    }

    // The hook reconstructs the event tick as Environment.TickCount64 - DelayMs, so each feed
    // converts the requested absolute tick into the matching delay.
    private static NavigationPointerInput Down(Fixture fixture, Point point, long tick) =>
        new(NavigationPointerKind.LeftDown, point, false, Delay(tick));

    private static NavigationPointerInput Up(Fixture fixture, Point point, long tick) =>
        new(NavigationPointerKind.LeftUp, point, false, Delay(tick));

    private static int Delay(long tick) =>
        (int)Math.Clamp(Environment.TickCount64 - tick, 0, int.MaxValue);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Check.That(condition(), "The expected condition was not reached in time");
    }

    private static Task ReopensAfterDoubleClick()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        var tick = Environment.TickCount64;
        hook.ProcessPointer(Down(fixture, new Point(300, 60), tick));
        hook.ProcessPointer(Up(fixture, new Point(300, 60), tick + 10));
        hook.ProcessPointer(Down(fixture, new Point(302, 61), tick + 200));
        hook.ProcessPointer(Up(fixture, new Point(302, 61), tick + 210));
        return WaitForAsync(() => fixture.ReopenCalls == 1).ContinueWith(_ =>
        {
            hook.Dispose();
            Check.Equal(1, fixture.ReopenCalls, "A completed empty tab-bar double click must reopen once");
        });
    }

    private static Task SingleClickDoesNotReopen()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        var tick = Environment.TickCount64;
        hook.ProcessPointer(Down(fixture, new Point(300, 60), tick));
        hook.ProcessPointer(Up(fixture, new Point(300, 60), tick + 10));
        return Task.Delay(150).ContinueWith(_ =>
        {
            hook.Dispose();
            Check.Equal(0, fixture.ReopenCalls, "A single click must never reopen");
        });
    }

    private static Task RejectsMismatches()
    {
        // Timeout, distance and a different window each must break the pair.
        var timeout = new Fixture();
        var timeoutHook = timeout.CreateHook();
        var tick = Environment.TickCount64;
        timeoutHook.ProcessPointer(Down(timeout, new Point(300, 60), tick));
        timeoutHook.ProcessPointer(Down(timeout, new Point(300, 60), tick + 700));
        timeoutHook.Dispose();

        var far = new Fixture();
        var farHook = far.CreateHook();
        farHook.ProcessPointer(Down(far, new Point(300, 60), tick));
        farHook.ProcessPointer(Down(far, new Point(320, 60), tick + 200));
        farHook.Dispose();

        var otherWindow = new Fixture { CaptureOverride = (point, t) => new ExplorerTabBarClick(point.X == 302 ? 8 : 7, point, t) };
        var otherHook = otherWindow.CreateHook();
        otherHook.ProcessPointer(Down(otherWindow, new Point(300, 60), tick));
        otherHook.ProcessPointer(Down(otherWindow, new Point(302, 60), tick + 200));
        otherHook.Dispose();

        return Task.Delay(150).ContinueWith(_ =>
        {
            Check.Equal(0, timeout.ReopenCalls, "A timed-out pair must not reopen");
            Check.Equal(0, far.ReopenCalls, "A too-far pair must not reopen");
            Check.Equal(0, otherWindow.ReopenCalls, "A pair across windows must not reopen");
        });
    }

    private static Task FailsClosedOnNonEmpty()
    {
        var fixture = new Fixture { EmptyArea = false };
        var hook = fixture.CreateHook();
        var tick = Environment.TickCount64;
        hook.ProcessPointer(Down(fixture, new Point(300, 60), tick));
        hook.ProcessPointer(Up(fixture, new Point(300, 60), tick + 10));
        hook.ProcessPointer(Down(fixture, new Point(302, 61), tick + 200));
        hook.ProcessPointer(Up(fixture, new Point(302, 61), tick + 210));
        return Task.Delay(200).ContinueWith(_ =>
        {
            hook.Dispose();
            Check.Equal(0, fixture.ReopenCalls, "A non-empty tab-bar point must fail closed");
        });
    }

    private static async Task StopRetiresRequest()
    {
        var fixture = new Fixture { PendingHitTest = true };
        var hook = fixture.CreateHook();
        var tick = Environment.TickCount64;
        hook.ProcessPointer(Down(fixture, new Point(300, 60), tick));
        hook.ProcessPointer(Up(fixture, new Point(300, 60), tick + 10));
        hook.ProcessPointer(Down(fixture, new Point(302, 61), tick + 200));
        hook.ProcessPointer(Up(fixture, new Point(302, 61), tick + 210));
        // The hit test is parked on the gate; stopping the hook must retire the gesture.
        hook.StopHook();
        fixture.Gate.TrySetResult(true);
        await Task.Delay(50);
        Check.Equal(0, fixture.ReopenCalls, "A stopped hook must retire the pending reopen");
    }
}
