using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WinTab.Interop;

namespace WinTab.Hooks;

public enum MediaPreviewKind { Image, Video, Audio, Text }

/// <summary>Fully resolved preview payload, ready to hand to the window on the UI thread.</summary>
internal sealed record ExplorerMediaPreview(
    ExplorerFileHoverItem Item,
    MediaPreviewKind Kind,
    string FilePath,
    string Info,
    BitmapSource? Thumbnail,
    string? Text)
{
    /// <summary>The Shell gave no thumbnail; the window may show its kind-specific placeholder.</summary>
    public bool HasThumbnail => Thumbnail != null;
}

/// <summary>
/// Turns a hovered filesystem item into preview content on a background thread. Every step is bounded and
/// cancels cleanly: a preview that loses its hover must never outlive the gesture that requested it.
/// </summary>
internal static class ExplorerMediaPreviewContent
{
    private const int ThumbnailSize = 256;
    private const long MaximumTextBytes = 1_000_000;
    private const int MaximumTextChars = 20_000;

    internal static bool IsMediaFile(string name) => Classify(name) != null;

    internal static MediaPreviewKind? Classify(string name)
    {
        var extension = Path.GetExtension(name.AsSpan());
        if (extension.Length == 0)
            return null;
        return extension.ToString().ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif"
                or ".tif" or ".tiff" or ".ico" or ".jfif" => MediaPreviewKind.Image,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".m4v" or ".wmv" or ".flv"
                or ".mpg" or ".mpeg" or ".ts" or ".3gp" => MediaPreviewKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" or ".wma" or ".opus" => MediaPreviewKind.Audio,
            ".txt" or ".log" or ".ini" => MediaPreviewKind.Text,
            _ => null
        };
    }

    /// <summary>Runs on a worker thread. Text files render inline; media asks the Shell for a thumbnail.</summary>
    internal static async Task<ExplorerMediaPreview?> ResolveAsync(ExplorerFileHoverItem item, string path,
        CancellationToken cancellationToken)
    {
        var kind = Classify(item.Name);
        if (kind == null)
            return null;

        return kind switch
        {
            MediaPreviewKind.Text => await Task.Run(() => ResolveText(item, path, cancellationToken), cancellationToken).ConfigureAwait(false),
            _ => await Task.Run(() => ResolveMedia(item, path, kind.Value, cancellationToken), cancellationToken).ConfigureAwait(false)
        };
    }

    private static ExplorerMediaPreview? ResolveText(ExplorerFileHoverItem item, string path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaximumTextBytes)
                return null;

            using var stream = info.OpenRead();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaximumTextChars];
            var length = reader.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            var text = new string(buffer, 0, length);
            if (text.Length == 0)
                return null;

            return new ExplorerMediaPreview(item, MediaPreviewKind.Text, path,
                FormatSize(info.Length), Thumbnail: null, Text: text);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Text preview failed: {exception.GetType().Name}:{exception.Message}");
            return null;
        }
    }

    private static ExplorerMediaPreview? ResolveMedia(ExplorerFileHoverItem item, string path,
        MediaPreviewKind kind, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
                return null;

            var thumbnail = kind == MediaPreviewKind.Video ? null : ShellItemFactory.GetThumbnailBitmap(path, ThumbnailSize);
            BitmapSource? source = null;
            if (thumbnail is { } handle)
            {
                try
                {
                    source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        handle, nint.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    // Shell thumbnails often carry an all-zero alpha channel; WPF would then paint the
                    // card fully transparent ("empty" card). Drop alpha so the picture is always opaque.
                    source = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        source, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                    source.Freeze();
                }
                finally
                {
                    ShellItemFactory.DeleteBitmap(handle);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var infoText = kind == MediaPreviewKind.Image
                ? DescribeImage(source, info.Length)
                : FormatSize(info.Length);
            return new ExplorerMediaPreview(item, kind, path, infoText, source, Text: null);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Media preview failed: {exception.GetType().Name}:{exception.Message}");
            return null;
        }
    }

    private static string DescribeImage(BitmapSource? source, long size)
    {
        var dimensions = source == null ? string.Empty : $"{source.PixelWidth}×{source.PixelHeight} · ";
        return dimensions + FormatSize(size);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1_024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1_024.0:0.#} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:0.#} MB",
        _ => $"{bytes / 1_073_741_824.0:0.##} GB"
    };
}
