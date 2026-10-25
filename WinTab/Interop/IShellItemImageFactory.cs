using System;
using System.Runtime.InteropServices;

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
    /// Loads a thumbnail for a filesystem path on the calling (STA) thread. Returns null when the Shell
    /// cannot provide one; SIIGBF_THUMBNAILONLY (0x2) keeps generic file icons out of the preview.
    /// </summary>
    public static nint? GetThumbnailBitmap(string path, int size)
    {
        try
        {
            var iid = FactoryIid;
            SHCreateItemFromParsingName(path, nint.Zero, ref iid, out var factory);
            var result = factory.GetImage(new NativeSize(size, size), 0x2, out var bitmap);
            if (result < 0 || bitmap == 0)
                return null;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void DeleteBitmap(nint bitmap)
    {
        if (bitmap != 0)
            DeleteObject(bitmap);
    }
}
