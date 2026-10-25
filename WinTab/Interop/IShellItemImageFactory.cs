using System;
using System.Runtime.InteropServices;
using System.Threading;
using WinTab.Hooks;

namespace WinTab.Interop;

/// <summary>
/// Public Shell thumbnail entry: IShellItemImageFactory over SHCreateItemFromParsingName.
/// Declared by hand like the other shell interop in this project; no TLB dependency.
/// </summary>
[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    /// <summary>Returns an HBITMAP the caller must delete with DeleteObject after copying its pixels.</summary>
    [PreserveSig]
    int GetImage(NativeSize size, int flags, out nint phBitmap);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeSize
{
    public int Width;
    public int Height;

    public NativeSize(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

public static class ShellItemFactory
{
    private static readonly Guid FactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bc, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    /// <summary>
    /// Loads a thumbnail for a filesystem path. The Shell thumbnail pipeline requires an STA thread:
    /// calls from MTA worker threads fail, so the request runs on a dedicated STA thread and is
    /// joined before returning. SIIGBF_RESIZETOFIT (0x0) is used deliberately: probing on the target
    /// machine showed SIIGBF_THUMBNAILONLY (0x2) returns E_FAIL for some cached files, while 0x0
    /// always succeeded; files without a thumbnail fall back to their file-type icon, which is a
    /// friendlier placeholder than an empty card. Returns null when even that fails.
    /// </summary>
    public static nint? GetThumbnailBitmap(string path, int size)
    {
        nint? bitmap = null;
        var thread = new Thread(() => bitmap = GetThumbnailBitmapOnSta(path, size))
        {
            IsBackground = true,
            Name = "WinTab hover thumbnail"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return bitmap;
    }

    private static nint? GetThumbnailBitmapOnSta(string path, int size)
    {
        try
        {
            var iid = FactoryIid;
            SHCreateItemFromParsingName(path, nint.Zero, ref iid, out var factory);
            var result = factory.GetImage(new NativeSize(size, size), 0x0, out var bitmap);
            if (result < 0 || bitmap == 0)
            {
                ExplorerDebugLog.Write($"Thumbnail failed hr=0x{result:X8} for {path}");
                return null;
            }
            return bitmap;
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Thumbnail failed {exception.GetType().Name}:{exception.Message} for {path}");
            return null;
        }
    }

    public static void DeleteBitmap(nint bitmap)
    {
        if (bitmap != 0)
            DeleteObject(bitmap);
    }
}
