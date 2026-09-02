using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fangcun
{
    /// <summary>
    /// 抽取真实文件/快捷方式自身的图标（只读，不触碰桌面壳）。
    /// 优先取真实文件图标；失败（如 OneDrive 占位/未同步/路径不可达）则按扩展名+类型回退到系统注册图标，
    /// 保证任何条目都有正确的类型图标，而不是空白。
    /// </summary>
    internal static class IconService
    {
        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static ImageSource? GetIcon(string path, bool small = false)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Cache.TryGetValue(path, out var cached)) return cached;
            try
            {
                var fi = new SHFILEINFO();
                uint flags = SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                // 1) 真实文件/目录图标
                IntPtr hr = SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                // 2) 失败则按扩展名+类型取系统注册图标（不访问磁盘，对 OneDrive 占位文件也有效）
                if (hr == IntPtr.Zero || fi.hIcon == IntPtr.Zero)
                {
                    bool isDir = Directory.Exists(path) || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) != 0);
                    uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                    hr = SHGetFileInfo(path, attr, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags | SHGFI_USEFILEATTRIBUTES);
                }
                if (hr != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    DestroyIcon(fi.hIcon);
                    return Cache[path] = src;
                }
                return Cache[path] = null;
            }
            catch
            {
                return Cache[path] = null;
            }
        }
    }
}
