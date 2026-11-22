using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace JeekLauncher.Services;

/// <summary>Resolved target path and launch arguments of a Windows <c>.lnk</c> shortcut.</summary>
internal readonly record struct ShellLinkInfo(string? Target, string? Arguments);

/// <summary>Resolves the target path and arguments of a Windows <c>.lnk</c> shortcut via COM.</summary>
internal static class ShellLinkResolver
{
    private const uint SlgpRawPath = 0x4;

    public static string? Resolve(string lnkPath) => ResolveInfo(lnkPath).Target;

    public static ShellLinkInfo ResolveInfo(string lnkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);

            var pathBuilder = new StringBuilder(1024);
            var data = new WIN32_FIND_DATAW();
            link.GetPath(pathBuilder, pathBuilder.Capacity, ref data, SlgpRawPath);
            string path = pathBuilder.ToString();

            var argsBuilder = new StringBuilder(1024);
            link.GetArguments(argsBuilder, argsBuilder.Capacity);
            string args = argsBuilder.ToString().Trim();

            return new ShellLinkInfo(
                string.IsNullOrWhiteSpace(path) ? null : path,
                args.Length == 0 ? null : args);
        }
        catch
        {
            return default;
        }
        finally
        {
            if (link is not null)
                Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, ref WIN32_FIND_DATAW data, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short wHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int iShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int iIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }
}
