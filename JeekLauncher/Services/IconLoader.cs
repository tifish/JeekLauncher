using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Platform;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace JeekLauncher.Services;

/// <summary>
/// Extracts and caches icons for shortcuts, executables, and packaged shell items
/// as Avalonia <see cref="AvaloniaBitmap"/> instances using pure Win32 / Shell APIs
/// (no System.Drawing or PNG round-tripping).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconLoader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DiNormal = 0x0003;
    private const int DefaultIconSize = 48;

    private readonly record struct CachedIcon(long Stamp, AvaloniaBitmap? Bitmap);

    private static readonly ConcurrentDictionary<string, CachedIcon> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static AvaloniaBitmap? Load(string path, int size = DefaultIconSize)
    {
        long stamp = GetFileStamp(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Bitmap;

        var bitmap = LoadShellIconCore(path, size) ?? LoadViaShGetFileInfo(path, 32);
        Cache[path] = new CachedIcon(stamp, bitmap);
        return bitmap;
    }

    /// <summary>
    /// Loads the icon for any shell item by parsing name (e.g.
    /// <c>shell:AppsFolder\{aumid}</c>), including packaged / Store apps.
    /// </summary>
    public static AvaloniaBitmap? LoadShellIcon(string parsingName, int size = DefaultIconSize)
    {
        if (Cache.TryGetValue(parsingName, out var cached))
            return cached.Bitmap;

        var bitmap = LoadShellIconCore(parsingName, size);
        Cache[parsingName] = new CachedIcon(0, bitmap);
        return bitmap;
    }

    /// <summary>Drops cache entries for items no longer present in the catalog.</summary>
    public static void PruneCache(ISet<string> activeKeys)
    {
        foreach (string key in Cache.Keys)
        {
            if (!activeKeys.Contains(key))
                Cache.TryRemove(key, out _);
        }
    }

    private static long GetFileStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }

    private static AvaloniaBitmap? LoadShellIconCore(string parsingName, int size)
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

    private static AvaloniaBitmap? LoadViaShGetFileInfo(string path, int size)
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            return ConvertHIcon(info.hIcon, size);
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

    /// <summary>Renders an HICON into a 32bpp premultiplied Avalonia bitmap via Win32 GDI.</summary>
    private static AvaloniaBitmap? ConvertHIcon(IntPtr hIcon, int size)
    {
        byte[]? onBlack = RenderIconOnBackground(hIcon, size, whiteBackground: false);
        if (onBlack is null)
            return null;

        // Modern 32bpp icons populate the alpha channel directly when drawn on black.
        bool hasAlpha = false;
        for (int i = 3; i < onBlack.Length; i += 4)
        {
            if (onBlack[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            // Legacy masked icons leave alpha=0; recover exact alpha by comparing black vs white background.
            byte[]? onWhite = RenderIconOnBackground(hIcon, size, whiteBackground: true);
            if (onWhite is not null)
            {
                for (int i = 0; i < onBlack.Length; i += 4)
                {
                    int alpha = 255 - (onWhite[i] - onBlack[i]);
                    onBlack[i + 3] = (byte)Math.Clamp(alpha, 0, 255);
                }
            }
        }

        return CreateWriteableBitmap(onBlack, size, size);
    }

    private static byte[]? RenderIconOnBackground(IntPtr hIcon, int size, bool whiteBackground)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };

            hBitmap = CreateDIBSection(memDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                return null;

            int byteCount = size * size * 4;
            byte[] pixels = new byte[byteCount];
            if (whiteBackground)
            {
                Array.Fill(pixels, (byte)0xFF);
                Marshal.Copy(pixels, 0, bits, byteCount);
            }

            oldBitmap = SelectObject(memDc, hBitmap);
            if (!DrawIconEx(memDc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DiNormal))
                return null;

            Marshal.Copy(bits, pixels, 0, byteCount);
            return pixels;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
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

        return CreateWriteableBitmap(pixels, width, height);
    }

    private static AvaloniaBitmap CreateWriteableBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
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
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

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
