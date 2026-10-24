using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinTab.Helpers;
using WinTab.Hooks;

internal static class ExplorerFileListDoubleClickTests
{
    public static IEnumerable<(string Name, Func<Task> Body)> All()
    {
        yield return ("file-list double-click pairs two complete matching clicks", PairsMatchingClicks);
        yield return ("file-list double-click rejects timeout distance and target changes", RejectsMismatches);
        yield return ("file-list double-click never reuses a completed pair", DoesNotReusePair);
        yield return ("file-list double-click requires both backgrounds to be empty", RequiresTwoEmptyHits);
        yield return ("file-list double-click navigates once after two empty hits", NavigatesOnce);
        yield return ("file-list double-click stop retires an unfinished request", StopCancelsRequest);
        yield return ("file-list parent navigation uses public same-browser parent flags", ParentFlags);
    }

    private static ExplorerFileListDoubleClickState NewState() => new(() => 500, () => 4, () => 4);

    private static ExplorerFileListClick Click(long tick, Point? point = null, string location = @"C:\Parent\Child",
        int window = 1, int tab = 2, int view = 3) => new(
        new WindowIdentity(window, 10, 20, 30), new WindowIdentity(tab, 10, 20, 31),
        new WindowIdentity(view, 10, 20, 32), point ?? new Point(100, 100), location, tick);

    private static Task PairsMatchingClicks()
    {
        var state = NewState();
        var first = Click(100);
        var second = Click(300, new Point(101, 101));
        Check.That(ReferenceEquals(first, state.HandleLeftDown(first)), "The first down starts a candidate");
        Check.That(state.HandleLeftUp(first.Point, 110) == null, "The first up performs no navigation");
        Check.That(state.HandleLeftDown(second) == null, "The second down only pairs the candidate");
        var request = state.HandleLeftUp(second.Point, 310);
        Check.That(request != null && ReferenceEquals(first, request.First) && ReferenceEquals(second, request.Second),
            "The second up returns both original snapshots");
        return Task.CompletedTask;
    }

    private static Task RejectsMismatches()
    {
        foreach (var second in new[]
        {
            Click(601), Click(200, new Point(110, 100)), Click(200, location: @"C:\Other"),
            Click(200, window: 9), Click(200, tab: 9), Click(200, view: 9), Click(50)
        })
        {
            var state = NewState();
            var first = Click(100);
            state.HandleLeftDown(first);
            state.HandleLeftUp(first.Point, 100);
            state.HandleLeftDown(second);
            Check.That(state.HandleLeftUp(second.Point, second.Tick + 1) == null,
                "A mismatched second click must not navigate");
        }

        var dragged = NewState();
        var down = Click(100);
        dragged.HandleLeftDown(down);
        dragged.HandleLeftUp(new Point(200, 200), 110);
        var next = Click(200);
        dragged.HandleLeftDown(next);
        Check.That(dragged.HandleLeftUp(next.Point, 210) == null, "A drag invalidates the first click");
        return Task.CompletedTask;
    }

    private static Task DoesNotReusePair()
    {
        var state = NewState();
        var first = Click(100);
        state.HandleLeftDown(first);
        state.HandleLeftUp(first.Point, 100);
        state.HandleLeftDown(first with { Tick = 200 });
        Check.That(state.HandleLeftUp(first.Point, 200) != null, "The initial pair completes");
        var third = Click(300);
        state.HandleLeftDown(third);
        Check.That(state.HandleLeftUp(third.Point, 300) == null, "A third click is a new first click, not another pair");
        state.HandleLeftDown(null);
        Check.That(state.HandleLeftUp(third.Point, 400) == null, "An occupied down clears the candidate");
        return Task.CompletedTask;
    }

    private sealed class HookFixture : IDisposable
    {
        private readonly Queue<Task<bool>> _hits = new();
        private readonly TaskCompletionSource<bool> _navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ExplorerFileListDoubleClickHook Hook;
        internal int NavigateCalls;
        internal int CurrentCalls;
        internal bool Valid = true;

        internal HookFixture()
        {
            Hook = new ExplorerFileListDoubleClickHook(
                _ => new NoOpDisposable(),
                (point, tick) => Click(tick, point),
                (_, _) => _hits.Count == 0 ? Task.FromResult(true) : _hits.Dequeue(),
                (_, _, _) => { NavigateCalls++; _navigated.TrySetResult(true); return Task.FromResult(true); },
                _ => { CurrentCalls++; return Valid; },
                NewState());
            Hook.StartHook();
        }

        internal void QueueHit(Task<bool> hit) => _hits.Enqueue(hit);
        internal Task Navigation => _navigated.Task;

        internal void DoubleClick()
        {
            var point = new Point(100, 100);
            Hook.ProcessPointer(new NavigationPointerInput(NavigationPointerKind.LeftDown, point, false));
            Hook.ProcessPointer(new NavigationPointerInput(NavigationPointerKind.LeftUp, point, false));
            Hook.ProcessPointer(new NavigationPointerInput(NavigationPointerKind.LeftDown, point, false));
            Hook.ProcessPointer(new NavigationPointerInput(NavigationPointerKind.LeftUp, point, false));
        }

        public void Dispose() => Hook.Dispose();
    }

    private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }

    private static async Task RequiresTwoEmptyHits()
    {
        using var fixture = new HookFixture();
        fixture.QueueHit(Task.FromResult(false));
        fixture.QueueHit(Task.FromResult(true));
        fixture.DoubleClick();
        await WaitUntil(() => fixture.CurrentCalls >= 2);
        Check.Equal(0, fixture.NavigateCalls, "An occupied first click must veto the gesture");
    }

    private static async Task NavigatesOnce()
    {
        using var fixture = new HookFixture();
        fixture.DoubleClick();
        await fixture.Navigation.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(1, fixture.NavigateCalls, "One completed pair produces exactly one request");
    }

    private static async Task StopCancelsRequest()
    {
        using var fixture = new HookFixture();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.QueueHit(first.Task);
        fixture.QueueHit(second.Task);
        fixture.DoubleClick();
        fixture.Hook.StopHook();
        first.SetResult(true);
        second.SetResult(true);
        await Task.WhenAll(first.Task, second.Task);
        await Task.Delay(30);
        Check.Equal(0, fixture.NavigateCalls, "Stopped work must not navigate when a stale UIA result arrives");
    }

    private static Task ParentFlags()
    {
        Check.Equal(0x2001u, ExplorerWatcher.ParentBrowseFlags, "SBSP_PARENT | SBSP_SAMEBROWSER must be 0x2001");
        Check.That((ExplorerWatcher.ParentBrowseFlags & 0x4000) == 0, "Parent navigation is not history back");
        Check.That((ExplorerWatcher.ParentBrowseFlags & 0x0402) == 0, "Parent navigation must not use new-tab flags");
        return Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2_000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(5);
        }
    }
}
