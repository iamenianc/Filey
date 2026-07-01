using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

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
        /// Removes the password from each Excel file via a bundled PowerShell script that drives
        /// Excel COM Automation, saving password-free copies alongside the originals. The password
        /// is piped over the child process's stdin rather than passed as a command-line argument,
        /// so it never appears in a process listing (e.g. Task Manager's "Command line" column).
        /// </summary>
        public static async Task<List<ExcelPasswordResult>> RemoveExcelPasswordAsync(IEnumerable<string> filePaths, string password)
        {
            var paths = filePaths.ToList();
            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "RemoveExcelPassword.ps1");

            if (!File.Exists(scriptPath))
            {
                return paths.Select(p => new ExcelPasswordResult
                {
                    Path = p,
                    Success = false,
                    Error = "The RemoveExcelPassword.ps1 script is missing from the install directory."
                }).ToList();
            }

            var argumentsBuilder = new StringBuilder();
            argumentsBuilder.Append("-NoProfile -ExecutionPolicy Bypass -File \"").Append(scriptPath).Append("\" -Path");
            foreach (string path in paths)
            {
                argumentsBuilder.Append(" \"").Append(path).Append('"');
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = argumentsBuilder.ToString(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string stdout, stderr;
            using (var process = new Process { StartInfo = psi })
            {
                process.Start();

                await process.StandardInput.WriteLineAsync(password);
                process.StandardInput.Close();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                await Task.Run(() => process.WaitForExit());

                stdout = stdoutTask.Result;
                stderr = stderrTask.Result;
            }

            try
            {
                var results = JsonConvert.DeserializeObject<List<ExcelPasswordResult>>(stdout);
                if (results != null && results.Count > 0) return results;
            }
            catch (JsonException)
            {
                // Fall through to the synthesized failure below.
            }

            string message = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : "The password removal script did not return a result.";

            return paths.Select(p => new ExcelPasswordResult
            {
                Path = p,
                Success = false,
                Error = message
            }).ToList();
        }
    }
}
