using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
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
        private static readonly ImageSource _defaultFolderIcon = CreateDefaultFolderIcon();
        private static readonly ImageSource _defaultFileIcon = CreateDefaultFileIcon();

        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith("\\\\", StringComparison.Ordinal))
                return true;

            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                    return false;

                if (root.Length >= 2 && root[1] == ':')
                {
                    var drive = new DriveInfo(root);
                    return drive.DriveType == DriveType.Network;
                }
            }
            catch
            {
            }

            return false;
        }

        public static ImageSource GetFolderIcon(string path = null)
        {
            if (_folderIcon != null)
                return _folderIcon;

            if (string.IsNullOrWhiteSpace(path))
                path = "C:\\dummy_folder_path";

            if (IsNetworkPath(path))
                return _defaultFolderIcon;

            ImageSource icon = TryGetShellIcon(path, true);
            if (icon != null)
            {
                _folderIcon = icon;
                return _folderIcon;
            }

            return _defaultFolderIcon;
        }

        public static ImageSource GetFileIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _defaultFileIcon;

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                ext = ".unknown";

            if (_fileIconCache.TryGetValue(ext, out var cachedIcon))
                return cachedIcon;

            if (IsNetworkPath(path))
                return _defaultFileIcon;

            ImageSource icon = TryGetShellIcon(path, false);
            if (icon != null)
            {
                _fileIconCache[ext] = icon;
                return icon;
            }

            return _defaultFileIcon;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private static ImageSource TryGetShellIcon(string path, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (IsNetworkPath(path))
                return null;

            try
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                IntPtr hImg = SHGetFileInfo(
                    path,
                    attributes,
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
                        return img;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch (Exception ex) when (ex is AccessViolationException || ex is SEHException || ex is UnauthorizedAccessException || ex is COMException)
            {
                return null;
            }

            return null;
        }

        private static ImageSource CreateDefaultFolderIcon()
        {
            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing(
                Brushes.Goldenrod,
                new Pen(Brushes.DarkGoldenrod, 1.0),
                Geometry.Parse("M 0,6 L 0,2 Q 0,0 2,0 L 7,0 Q 8,0 8,1 L 8,3 L 16,3 L 16,14 L 0,14 Z")));

            var image = new DrawingImage(drawingGroup);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateDefaultFileIcon()
        {
            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing(
                Brushes.SteelBlue,
                new Pen(Brushes.DimGray, 1.0),
                Geometry.Parse("M 2,0 L 12,0 L 16,4 L 16,16 L 2,16 Z")));
            drawingGroup.Children.Add(new GeometryDrawing(
                Brushes.White,
                null,
                Geometry.Parse("M 2,0 L 12,0 L 12,4 L 16,4 L 16,16 L 2,16 Z")));

            var image = new DrawingImage(drawingGroup);
            image.Freeze();
            return image;
        }
    }
}
