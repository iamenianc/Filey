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
                    Error = $"The password-removal script was not found at: {scriptPath}"
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

            // Excel COM startup and the whole launch/IO sequence run on a background thread so the
            // caller's UI thread is never blocked (process.Start() and the stdin write are otherwise
            // synchronous). Excel can also hang on an unexpected dialog, so the wait is time-capped.
            const int timeoutMs = 120000;
            string stdout = string.Empty, stderr = string.Empty;
            int exitCode = -1;
            bool timedOut = false;

            await Task.Run(() =>
            {
                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();

                    process.StandardInput.WriteLine(password);
                    process.StandardInput.Close();

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        timedOut = true;
                        try { process.Kill(); } catch { /* already exiting */ }
                    }

                    // Both reads complete once the streams close (on exit or kill).
                    stdout = stdoutTask.Result ?? string.Empty;
                    stderr = stderrTask.Result ?? string.Empty;
                    exitCode = process.HasExited ? process.ExitCode : -1;
                }
            });

            if (timedOut)
            {
                return paths.Select(p => new ExcelPasswordResult
                {
                    Path = p,
                    Success = false,
                    Error = "Timed out waiting for Excel to respond. It may be blocked on a dialog or not installed correctly."
                }).ToList();
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

            // The script produced no usable JSON. Surface whatever diagnostics we captured so the
            // real cause (stderr, unexpected stdout, or a non-zero exit) is visible instead of a
            // generic message.
            string message;
            if (!string.IsNullOrWhiteSpace(stderr))
                message = stderr.Trim();
            else if (!string.IsNullOrWhiteSpace(stdout))
                message = $"Unexpected script output: {stdout.Trim()}";
            else
                message = $"The password removal script returned no result (exit code {exitCode}).";

            return paths.Select(p => new ExcelPasswordResult
            {
                Path = p,
                Success = false,
                Error = message
            }).ToList();
        }
    }
}
