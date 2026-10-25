using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinTab.Helpers;
using WinTab.Hooks;

internal static class ExplorerFileHoverPreviewTests
{
    public static IEnumerable<(string Name, Func<Task> Body)> All()
    {
        yield return ("hover preview shows once after the dwell", ShowsAfterDwell);
        yield return ("hover preview resets when the cursor moves", MovingResets);
        yield return ("hover preview ignores non-media files", IgnoresNonMedia);
        yield return ("hover preview requires a filesystem location", RequiresFilesystemPath);
        yield return ("hover preview hides on click and retires the pending result", InputHidesAndRetires);
        yield return ("hover preview retires results after stop", StopRetiresResult);
        yield return ("media classification covers the advertised kinds", ClassifiesKinds);
    }

    private sealed class FakeWindow : IExplorerPreviewWindow
    {
        public bool Visible { get; private set; }
        public int ShowCount;
        public ExplorerMediaPreview? Last;

        public void Show(ExplorerMediaPreview preview, Point cursor)
        {
            ShowCount++;
            Last = preview;
            Visible = true;
        }

        public void Hide() => Visible = false;

        public void Dispose() { }
    }

    private sealed class Fixture
    {
        public Point Cursor = new(100, 100);
        public ExplorerFileHoverItem? Identified = Item("photo.jpg");
        public int IdentifyCalls;
        public string? Path = @"C:\Users\demo\photo.jpg";
        public ExplorerMediaPreview? Resolved;
        public bool PendingResolve;
        public TaskCompletionSource<ExplorerMediaPreview?> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeWindow Window = new();
        public int ResolveCalls;

        public Fixture()
        {
            Resolved = new ExplorerMediaPreview(Item("photo.jpg"), MediaPreviewKind.Image, Path!,
                "1 KB", Thumbnail: null, Text: null);
        }

        public ExplorerFileHoverPreviewHook CreateHook()
        {
            var hook = new ExplorerFileHoverPreviewHook(
                null!,
                _ => new Disposer(),
                () => Cursor,
                _ => { IdentifyCalls++; return Identified; },
                (_, _) => Path,
                (_, _, _) => { ResolveCalls++; return PendingResolve ? Gate.Task : Task.FromResult(Resolved); },
                () => Window,
                _ => true);
            hook.StartHook();
            return hook;
        }
    }

    private sealed class Disposer : IDisposable
    {
        public void Dispose() { }
    }

    private static ExplorerFileHoverItem Item(string name) => new(
        new WindowIdentity(1, 10, 20, 30), new WindowIdentity(2, 10, 20, 31),
        new WindowIdentity(3, 10, 20, 32), name, new Point(100, 100));

    private static void Dwell(ExplorerFileHoverPreviewHook hook, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
            hook.ProcessTick();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Check.That(condition(), "The expected condition was not reached in time");
    }

    private static Task ShowsAfterDwell()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow + 8);
        return WaitForAsync(() => fixture.Window.ShowCount == 1).ContinueWith(_ =>
        {
            Dwell(hook, 4);
            Check.Equal(1, fixture.Window.ShowCount, "A stable hover must resolve and show once");
            Check.That(fixture.Window.Last!.Item.Name == "photo.jpg", "The shown preview must be the hovered file");
            Check.That(fixture.ResolveCalls == 1, "Resolve runs once per dwell");
            hook.Dispose();
        });
    }

    private static Task MovingResets()
    {
        var fixture = new Fixture();
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow - 1);
        fixture.Cursor = new Point(140, 120);
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow - 1);
        return WaitForAsync(() => fixture.IdentifyCalls >= 2).ContinueWith(_ =>
        {
            Check.Equal(0, fixture.ResolveCalls, "A moved cursor must restart the dwell and never resolve");
            Check.Equal(0, fixture.Window.ShowCount, "A moved cursor must never show");
            hook.Dispose();
        });
    }

    private static Task IgnoresNonMedia()
    {
        var fixture = new Fixture { Identified = Item("notes.docx") };
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow + 4);
        return WaitForAsync(() => fixture.IdentifyCalls >= 1).ContinueWith(_ =>
        {
            Check.Equal(0, fixture.ResolveCalls, "Non-media files must not be resolved");
            Check.Equal(0, fixture.Window.ShowCount, "Non-media files must not show a preview");
            hook.Dispose();
        });
    }

    private static Task RequiresFilesystemPath()
    {
        var fixture = new Fixture { Path = null };
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow + 4);
        return WaitForAsync(() => fixture.IdentifyCalls >= 1).ContinueWith(_ =>
        {
            Check.Equal(0, fixture.ResolveCalls, "A virtual location must not resolve");
            Check.Equal(0, fixture.Window.ShowCount, "A virtual location must not show a preview");
            hook.Dispose();
        });
    }

    private static Task InputHidesAndRetires()
    {
        var fixture = new Fixture { PendingResolve = true };
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow + 8);
        return WaitForAsync(() => fixture.ResolveCalls == 1).ContinueWith(async _ =>
        {
            hook.OnPointer(new NavigationPointerInput(NavigationPointerKind.LeftDown, fixture.Cursor, false, 0));
            Check.That(!fixture.Window.Visible, "A click must hide the preview");
            fixture.Gate.TrySetResult(fixture.Resolved);
            await Task.Delay(50);
            Check.Equal(0, fixture.Window.ShowCount, "A retired result must never show");
        }).Unwrap();
    }

    private static Task StopRetiresResult()
    {
        var fixture = new Fixture { PendingResolve = true };
        var hook = fixture.CreateHook();
        Dwell(hook, ExplorerFileHoverPreviewHook.DwellTicksToShow + 8);
        return WaitForAsync(() => fixture.ResolveCalls == 1).ContinueWith(async _ =>
        {
            hook.StopHook();
            fixture.Gate.TrySetResult(fixture.Resolved);
            await Task.Delay(50);
            Check.Equal(0, fixture.Window.ShowCount, "A result after stop must never show");
        }).Unwrap();
    }

    private static Task ClassifiesKinds()
    {
        Check.Equal(MediaPreviewKind.Image, ExplorerMediaPreviewContent.Classify("holiday.JPG"), "Upper case extensions classify");
        Check.Equal(MediaPreviewKind.Video, ExplorerMediaPreviewContent.Classify("clip.mp4"), "Video kinds classify");
        Check.Equal(MediaPreviewKind.Audio, ExplorerMediaPreviewContent.Classify("song.flac"), "Audio kinds classify");
        Check.Equal(MediaPreviewKind.Text, ExplorerMediaPreviewContent.Classify("readme.txt"), "Text kinds classify");
        Check.That(ExplorerMediaPreviewContent.Classify("setup.exe") == null, "Executables are not media");
        Check.That(ExplorerMediaPreviewContent.Classify("noextension") == null, "Files without extension are not media");
        return Task.CompletedTask;
    }
}
