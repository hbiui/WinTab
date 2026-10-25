using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinTab.Hooks;

namespace WinTab.UI.Views.Controls;

/// <summary>
/// The hover preview card. It never activates (WS_EX_NOACTIVATE + Focusable=False), never shows in the
/// taskbar, and stays above Explorer while its controls stay clickable. Playback stops on every hide.
/// </summary>
public partial class MediaPreviewWindow : Window, IExplorerPreviewWindow
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const int HwndTopmost = -1;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint SwpNosize = 0x0001;
    private const int MonitorDefaulttonearest = 2;
    private const int CursorOffset = 18;

    private nint _handle;
    private bool _muted;
    private System.Drawing.Point? _pendingPosition;

    public bool Visible { get; private set; }

    public MediaPreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(_handle, GwlExstyle);
            _ = SetWindowLong(_handle, GwlExstyle, style | WsExNoactivate | WsExToolwindow);
            // The first Show has no handle yet when Show() asks for a position; applying the pending
            // cursor here places the card before it ever becomes visible.
            if (_pendingPosition is { } pending)
                PositionNear(pending);
        };
    }

    void IExplorerPreviewWindow.Show(ExplorerMediaPreview preview, System.Drawing.Point cursor)
    {
        Player.Close();
        Player.Source = null;
        PreviewImage.Source = null;
        PreviewText.Text = string.Empty;
        DurationText.Text = string.Empty;
        FallbackText.Text = "◌";
        FallbackText.Visibility = Visibility.Collapsed;

        InfoText.Text = $"{preview.Kind.ToString().ToLowerInvariant()} · {preview.Info}";
        switch (preview.Kind)
        {
            case MediaPreviewKind.Image:
                ShowImage(preview);
                break;
            case MediaPreviewKind.Video:
                ShowMedia(preview, video: true);
                break;
            case MediaPreviewKind.Audio:
                ShowMedia(preview, video: false);
                break;
            case MediaPreviewKind.Text:
                ShowText(preview);
                break;
        }

        _pendingPosition = cursor;
        Show();
        PositionNear(cursor);
        Visible = true;
    }

    public new void Hide()
    {
        if (!Visible)
            return;
        Player.Close();
        Player.Source = null;
        HideWindow();
        Visible = false;
    }

    void IDisposable.Dispose() => Close();

    private void ShowImage(ExplorerMediaPreview preview)
    {
        PreviewImage.Visibility = Visibility.Visible;
        Player.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Collapsed;
        ControlsBar.Visibility = Visibility.Collapsed;
        if (preview.HasThumbnail)
            PreviewImage.Source = preview.Thumbnail;
        else
        {
            PreviewImage.Source = null;
            FallbackText.Visibility = Visibility.Visible;
        }
    }

    private void ShowMedia(ExplorerMediaPreview preview, bool video)
    {
        PreviewImage.Visibility = preview.HasThumbnail && !video ? Visibility.Visible : Visibility.Collapsed;
        if (preview.HasThumbnail && !video)
            PreviewImage.Source = preview.Thumbnail;
        FallbackText.Visibility = preview.HasThumbnail || video ? Visibility.Collapsed : Visibility.Visible;
        TextScroll.Visibility = Visibility.Collapsed;
        ControlsBar.Visibility = Visibility.Visible;
        Player.Visibility = video ? Visibility.Visible : Visibility.Hidden;
        Player.IsMuted = _muted;
        Player.Source = BuildFileUri(preview.FilePath);
        Player.Play();
        PlayPauseButton.Content = "⏸";
    }

    /// <summary>
    /// Builds the media source URI. Only '#' and '%' are escaped manually: a full segment escape
    /// turns '(' into %28 and Windows Media Foundation then reports the file as not found.
    /// </summary>
    private static Uri? BuildFileUri(string path)
    {
        try
        {
            var safe = path.Replace("%", "%25").Replace("#", "%23");
            return new Uri(safe, UriKind.Absolute);
        }
        catch (Exception exception)
        {
            WinTab.Hooks.ExplorerDebugLog.Write($"Preview source build failed: {exception.GetType().Name}:{exception.Message}");
            return null;
        }
    }

    private void ShowText(ExplorerMediaPreview preview)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Collapsed;
        ControlsBar.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Visible;
        PreviewText.Text = preview.Text ?? string.Empty;
        TextScroll.ScrollToTop();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source == null)
            return;
        if (Player.CanPause)
        {
            Player.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            Player.Play();
            PlayPauseButton.Content = "⏸";
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        Player.IsMuted = _muted;
        MuteButton.Content = _muted ? "🔇" : "🔊";
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
            DurationText.Text = Player.NaturalDuration.TimeSpan.ToString(@"mm\:ss");
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        WinTab.Hooks.ExplorerDebugLog.Write($"Preview playback failed: {e.ErrorException?.Message} source={Player.Source}");
        // Keep the card (hiding it would make the tick loop retry and flicker); fall back to a
        // placeholder so the user still gets a visible, stable response for unsupported formats.
        Player.Close();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        TextScroll.Visibility = Visibility.Collapsed;
        ControlsBar.Visibility = Visibility.Collapsed;
        FallbackText.Text = "⚠";
        FallbackText.Visibility = Visibility.Visible;
        UpdateLayout();
    }

    /// <summary>
    /// Positions in physical pixels through SetWindowPos so per-monitor DPI never shifts the card away from
    /// the cursor; the card flips to stay inside the monitor's work area.
    /// </summary>
    private void PositionNear(System.Drawing.Point cursor)
    {
        if (_handle == 0)
            return;
        var work = MonitorWorkArea(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (width <= 0)
            width = (int)(Width * dpi.DpiScaleX);

        var x = cursor.X + CursorOffset;
        var y = cursor.Y + CursorOffset;
        if (x + width > work.Right)
            x = cursor.X - CursorOffset - width;
        if (y + height > work.Bottom)
            y = cursor.Y - CursorOffset - height;
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        _ = SetWindowPos(_handle, HwndTopmost, x, y, 0, 0, SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    private static NativeRect MonitorWorkArea(System.Drawing.Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaulttonearest);
        var info = new MonitorInfo();
        info.Size = Marshal.SizeOf<MonitorInfo>();
        return GetMonitorInfo(monitor, ref info) ? info.Work : new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private void HideWindow()
    {
        Visibility = Visibility.Hidden;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint handle, int index, int style);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, int after, int x, int y, int width, int height, uint flags);
}
