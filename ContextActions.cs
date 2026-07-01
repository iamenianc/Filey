using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Filey
{
    /// <summary>Shared actions used by the right-click context menus across panels.</summary>
    public static class ContextActions
    {
        public static void OpenInExplorer(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            try
            {
                Process.Start("explorer.exe", $"\"{fullPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open Explorer: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void CopyPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            try
            {
                Clipboard.SetText(fullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not copy path: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Removes the open (encryption) password from each Excel workbook entirely in managed code
        /// (see <see cref="ExcelDecryptor"/>) - no Excel, COM, or PowerShell - saving a password-free
        /// copy alongside each original. Runs on a background thread; decryption is byte-preserving,
        /// so macros and formatting survive intact. Supports Agile-encrypted .xlsx/.xlsm files.
        /// </summary>
        public static async Task<List<ExcelPasswordResult>> RemoveExcelPasswordAsync(IEnumerable<string> filePaths, string password)
        {
            var paths = filePaths.ToList();

            return await Task.Run(() =>
            {
                var results = new List<ExcelPasswordResult>(paths.Count);
                foreach (string path in paths)
                {
                    try
                    {
                        byte[] decrypted = ExcelDecryptor.Decrypt(path, password);
                        string outputPath = GetCollisionSafeOutputPath(path);
                        File.WriteAllBytes(outputPath, decrypted);

                        results.Add(new ExcelPasswordResult { Path = path, OutputPath = outputPath, Success = true });
                    }
                    catch (ExcelDecryptException ex)
                    {
                        results.Add(new ExcelPasswordResult { Path = path, Success = false, Error = ex.Message });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new ExcelPasswordResult { Path = path, Success = false, Error = ex.Message });
                    }
                }
                return results;
            });
        }

        /// <summary>
        /// Returns "<name> (pw removed)<ext>" next to the original, appending " (2)", " (3)", ...
        /// until it finds a path that does not already exist.
        /// </summary>
        private static string GetCollisionSafeOutputPath(string originalPath)
        {
            string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(originalPath);
            string ext = Path.GetExtension(originalPath);

            string candidate = Path.Combine(dir, $"{name} (pw removed){ext}");
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(dir, $"{name} (pw removed) ({n}){ext}");
                n++;
            }
            return candidate;
        }
    }
}
