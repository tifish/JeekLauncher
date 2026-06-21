using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Platform;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace JeekLauncher.Services;

/// <summary>
/// Extracts the associated icon for a file (shortcut, exe, …) and converts it into
/// an Avalonia <see cref="Bitmap"/>. System.Drawing does the awkward HICON/alpha
/// handling for us. Windows-only by construction.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    public static AvaloniaBitmap? Load(string path)
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            return new AvaloniaBitmap(stream);
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>
    /// Loads the icon for any shell item by parsing name (e.g.
    /// <c>shell:AppsFolder\{aumid}</c>), including packaged / Store apps.
    /// </summary>
    public static AvaloniaBitmap? LoadShellIcon(string parsingName, int size = 48)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, in iid, out var factory) != 0 || factory is null)
                return null;

            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.ResizeToFit, out hbitmap) != 0
                    || hbitmap == IntPtr.Zero)
                {
                    return null;
                }

                return ConvertHBitmap(hbitmap);
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    /// <summary>Converts a 32bpp premultiplied-alpha HBITMAP into an Avalonia bitmap.</summary>
    private static AvaloniaBitmap? ConvertHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return null;

        int width = bm.bmWidth;
        int height = bm.bmHeight;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        byte[] pixels = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bitmap.Lock())
        {
            int srcStride = width * 4;
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
        }

        return bitmap;
    }

    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
