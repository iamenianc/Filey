using System;
using System.Diagnostics;
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
    }
}
