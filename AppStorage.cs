using System;
using System.Diagnostics;
using System.IO;

namespace Filey
{
    /// <summary>
    /// Centralises all reads and writes under %APPDATA%\Filey\. No file is written
    /// anywhere else. Writes go to a temp file first, then atomically replace the
    /// target so a crash mid-write can't corrupt persisted state.
    /// </summary>
    internal static class AppStorage
    {
        public static string Directory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Filey");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string PathFor(string fileName) => Path.Combine(Directory, fileName);

        public static string ReadAllTextOrNull(string fullPath)
        {
            try
            {
                return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppStorage read failed for {fullPath}: {ex.Message}");
                return null;
            }
        }

        public static void WriteAllTextAtomic(string fullPath, string contents)
        {
            try
            {
                string temp = fullPath + ".tmp";
                File.WriteAllText(temp, contents);

                if (File.Exists(fullPath))
                {
                    File.Replace(temp, fullPath, null);
                }
                else
                {
                    File.Move(temp, fullPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppStorage write failed for {fullPath}: {ex.Message}");
            }
        }
    }
}
