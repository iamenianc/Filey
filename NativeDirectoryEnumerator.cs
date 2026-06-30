using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Filey
{
    /// <summary>
    /// Lightweight directory entry returned by the fast Win32 enumerator. Kept as a
    /// top-level type so existing <c>List&lt;SimpleDirectoryInfo&gt;</c> call sites compile
    /// unchanged after the P/Invoke was lifted out of <see cref="PreviewPane"/>.
    /// </summary>
    public struct SimpleDirectoryInfo
    {
        public string Name;
        public string FullPath;
        public FileAttributes Attributes;
    }

    /// <summary>
    /// A file or directory entry with the metadata the index needs (size + last-write
    /// time), read directly from <c>WIN32_FIND_DATA</c> so no second stat call is required.
    /// </summary>
    public struct NativeFileEntry
    {
        public string Name;
        public string FullPath;
        public FileAttributes Attributes;
        public bool IsDirectory;
        public long Size;
        public DateTime LastWriteTimeUtc;
    }

    /// <summary>
    /// Shared <c>FindFirstFileEx</c>-based enumeration. Previously private to
    /// <see cref="PreviewPane"/>; extracted so the file index can crawl with the same fast,
    /// SMB-friendly path. Uses <c>FindExInfoBasic</c> (skips short names) and a large fetch
    /// buffer. Read-only and entirely user-mode — no raw volume access.
    /// </summary>
    internal static class NativeDirectoryEnumerator
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            ref WIN32_FIND_DATA lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr hFindFile, ref WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        private const int FIND_FIRST_EX_LARGE_FETCH = 2;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_HIDDEN = 0x02;
        private const uint FILE_ATTRIBUTE_SYSTEM = 0x04;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard,
            FindExInfoBasic,
            FindExInfoMaxInfoLevel
        }

        private enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch,
            FindExSearchLimitToDirectories,
            FindExSearchLimitToDevices,
            FindExSearchMaxSearchOp
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        /// <summary>Returns immediate subdirectories of <paramref name="parentPath"/> (no "." / "..").</summary>
        public static List<SimpleDirectoryInfo> GetSubDirectories(string parentPath)
        {
            var list = new List<SimpleDirectoryInfo>();
            WIN32_FIND_DATA findData = new WIN32_FIND_DATA();
            IntPtr hFind = FindFirstFileEx(
                Path.Combine(parentPath, "*"),
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                ref findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (hFind == INVALID_HANDLE_VALUE) return list;
            try
            {
                do
                {
                    if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        string name = findData.cFileName;
                        if (name != "." && name != "..")
                        {
                            list.Add(new SimpleDirectoryInfo
                            {
                                Name = name,
                                FullPath = Path.Combine(parentPath, name),
                                Attributes = (FileAttributes)findData.dwFileAttributes
                            });
                        }
                    }
                }
                while (FindNextFile(hFind, ref findData));
            }
            finally { FindClose(hFind); }
            return list;
        }

        /// <summary>Counts non-directory entries directly in <paramref name="dirPath"/>.</summary>
        public static int GetFileCount(string dirPath, bool showHidden, CancellationToken token)
        {
            int count = 0;
            WIN32_FIND_DATA findData = new WIN32_FIND_DATA();
            IntPtr hFind = FindFirstFileEx(Path.Combine(dirPath, "*"),
                FINDEX_INFO_LEVELS.FindExInfoBasic, ref findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (hFind == INVALID_HANDLE_VALUE) return 0;
            try
            {
                do
                {
                    if (token.IsCancellationRequested) break;
                    uint attr = findData.dwFileAttributes;
                    if ((attr & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
                    if (!showHidden && ((attr & FILE_ATTRIBUTE_HIDDEN) != 0 || (attr & FILE_ATTRIBUTE_SYSTEM) != 0)) continue;
                    count++;
                }
                while (FindNextFile(hFind, ref findData));
            }
            finally { FindClose(hFind); }
            return count;
        }

        /// <summary>Runs <see cref="GetSubDirectories"/> on a background thread, bounded by a semaphore.</summary>
        public static async Task<List<SimpleDirectoryInfo>> FetchSubDirectoriesAsync(
            string path, SemaphoreSlim semaphore, CancellationToken token)
        {
            await semaphore.WaitAsync(token);
            try { return await Task.Run(() => GetSubDirectories(path), token); }
            catch (OperationCanceledException) { throw; }
            catch { return new List<SimpleDirectoryInfo>(); }
            finally { semaphore.Release(); }
        }

        /// <summary>
        /// Enumerates all entries (files and directories) directly under
        /// <paramref name="parentPath"/>, with size and last-write time populated for files.
        /// Used by the index crawler to capture metadata in a single pass.
        /// </summary>
        public static List<NativeFileEntry> EnumerateEntries(string parentPath)
        {
            var list = new List<NativeFileEntry>();
            WIN32_FIND_DATA findData = new WIN32_FIND_DATA();
            IntPtr hFind = FindFirstFileEx(
                Path.Combine(parentPath, "*"),
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                ref findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (hFind == INVALID_HANDLE_VALUE) return list;
            try
            {
                do
                {
                    string name = findData.cFileName;
                    if (name == "." || name == "..") continue;

                    bool isDir = (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    list.Add(new NativeFileEntry
                    {
                        Name = name,
                        FullPath = Path.Combine(parentPath, name),
                        Attributes = (FileAttributes)findData.dwFileAttributes,
                        IsDirectory = isDir,
                        Size = isDir ? 0 : (((long)findData.nFileSizeHigh) << 32) | findData.nFileSizeLow,
                        LastWriteTimeUtc = FileTimeToUtc(findData.ftLastWriteTime)
                    });
                }
                while (FindNextFile(hFind, ref findData));
            }
            finally { FindClose(hFind); }
            return list;
        }

        private static DateTime FileTimeToUtc(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            long value = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            if (value <= 0) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(value); }
            catch { return DateTime.MinValue; }
        }
    }
}
