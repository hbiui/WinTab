using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using WinTab.Hooks;

internal static class ExplorerNewTabDuplicateTests
{
    public static IEnumerable<(string Name, Func<Task> Body)> All()
    {
        yield return ("new-tab button click duplicates the active tab and swallows the click", DuplicatesOnButton);
        yield return ("new-tab button click elsewhere passes through untouched", PassesThroughElsewhere);
        yield return ("new-tab duplicate hook ignores input after stop", StopHookIgnoresInput);
        yield return ("new-tab duplicate failure does not swallow retries", FailureKeepsHookWorking);
        yield return ("new-tab button area decides by rect then guard band", ButtonAreaDecision);
    }

    private sealed class Fixture
    {
        public bool OnButton = true;
        public Func<Point, long, ExplorerNewTabClick?>? CaptureOverride;
        public List<nint> DuplicateCalls = [];
        public int FailuresLeft;

        public ExplorerNewTabDuplicateHook CreateHook()
        {
            var hook = new ExplorerNewTabDuplicateHook(
                (point, tick) => CaptureOverride != null ? CaptureOverride(point, tick) : new ExplorerNewTabClick(7, point, tick),
                _ => OnButton,
                click =>
                {
                    if (FailuresLeft > 0)
                    {
                        FailuresLeft--;
                        throw new InvalidOperationException("simulated shell failure");
                    }
                    DuplicateCalls.Add(click.Window);
                    return Task.CompletedTask;
                });
            hook.StartHook();
            return hook;
        }
    }

    private static Task DuplicatesOnButton()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        try
        {
            var handled = hook.ProcessMouseDown(new Point(340, 60), Environment.TickCount64);
            Check.That(handled, "A click on the new-tab button must be swallowed");
            Check.Equal(1, fixture.DuplicateCalls.Count, "The click must ask the watcher for one duplicate");
            Check.Equal(7, fixture.DuplicateCalls[0], "The duplicate must target the clicked window");
        }
        finally
        {
            hook.Dispose();
        }
        return Task.CompletedTask;
    }

    private static Task PassesThroughElsewhere()
    {
        var fixture = new Fixture { OnButton = false };
        var hook = fixture.CreateHook();
        try
        {
            var handled = hook.ProcessMouseDown(new Point(300, 60), Environment.TickCount64);
            Check.That(!handled, "A click off the new-tab button must pass through");
            Check.Equal(0, fixture.DuplicateCalls.Count, "A click off the new-tab button must never duplicate");
        }
        finally
        {
            hook.Dispose();
        }
        return Task.CompletedTask;
    }

    private static Task StopHookIgnoresInput()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        hook.StopHook();
        var handled = hook.ProcessMouseDown(new Point(340, 60), Environment.TickCount64);
        Check.That(!handled, "A stopped hook must not swallow input");
        Check.Equal(0, fixture.DuplicateCalls.Count, "A stopped hook must never duplicate");
        hook.Dispose();
        return Task.CompletedTask;
    }

    private static Task ButtonAreaDecision()
    {
        var rect = new Rectangle(340, 50, 32, 32);
        var row = new Rectangle(0, 40, 800, 42);
        Point At(int x, int y) => new(x, y);

        Check.That(ExplorerNewTabDuplicateHook.IsNewTabButtonArea(true, rect, true, row, false, 330, At(350, 60)),
            "A point inside the cached button rectangle must be the button");
        Check.That(!ExplorerNewTabDuplicateHook.IsNewTabButtonArea(true, rect, true, row, false, 330, At(420, 60)),
            "A point outside both the rectangle and the guard band must not be the button");
        Check.That(ExplorerNewTabDuplicateHook.IsNewTabButtonArea(false, default, true, row, false, 330, At(350, 60)),
            "Without a UIA rectangle, the guard band right of the last tab must decide the button");
        Check.That(!ExplorerNewTabDuplicateHook.IsNewTabButtonArea(false, default, true, row, false, 330, At(420, 60)),
            "The open empty area beyond the guard band must stay native");
        Check.That(!ExplorerNewTabDuplicateHook.IsNewTabButtonArea(false, default, true, row, true, 330, At(350, 60)),
            "A point on a tab title must never count as the button");
        Check.That(!ExplorerNewTabDuplicateHook.IsNewTabButtonArea(false, default, false, default, null, null, At(350, 60)),
            "A cold cache must fail closed");
        return Task.CompletedTask;
    }

    private static async Task FailureKeepsHookWorking()
    {
        var fixture = new Fixture { FailuresLeft = 1 };
        var hook = fixture.CreateHook();
        try
        {
            var handled = hook.ProcessMouseDown(new Point(340, 60), Environment.TickCount64);
            Check.That(handled, "The click is still consumed even when the duplicate later fails");
            await Task.Delay(100);
            var retried = hook.ProcessMouseDown(new Point(340, 60), Environment.TickCount64);
            Check.That(retried, "A retry after a failed duplicate must be swallowed again");
            Check.Equal(1, fixture.DuplicateCalls.Count, "Only the retry may reach the watcher");
        }
        finally
        {
            hook.Dispose();
        }
    }
}
