using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Filey
{
    public static class IconHelper
    {
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

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static ImageSource _folderIcon;
        private static readonly Dictionary<string, ImageSource> _fileIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource GetFolderIcon()
        {
            if (_folderIcon != null)
                return _folderIcon;

            SHFILEINFO shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                "C:\\dummy_folder_path", 
                FILE_ATTRIBUTE_DIRECTORY, 
                ref shinfo, 
                (uint)Marshal.SizeOf(shinfo), 
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    ImageSource img = Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon, 
                        Int32Rect.Empty, 
                        BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    _folderIcon = img;
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }
            return _folderIcon;
        }

        public static ImageSource GetFileIcon(string path)
        {
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                ext = ".unknown";

            if (_fileIconCache.TryGetValue(ext, out var cachedIcon))
                return cachedIcon;

            SHFILEINFO shinfo = new SHFILEINFO();
            // Use the actual path but fall back/speed up with USEFILEATTRIBUTES
            // For general files, USEFILEATTRIBUTES is fine and avoids file access issues.
            IntPtr hImg = SHGetFileInfo(
                path, 
                FILE_ATTRIBUTE_NORMAL, 
                ref shinfo, 
                (uint)Marshal.SizeOf(shinfo), 
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    ImageSource img = Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon, 
                        Int32Rect.Empty, 
                        BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    _fileIconCache[ext] = img;
                    return img;
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }
            return null;
        }
    }
}
