using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Filey
{
    /// <summary>
    /// Central runtime theme manager. Keeps the application strictly in Dark mode.
    ///
    /// All palette colors/fonts are referenced via DynamicResource/FindResource.
    /// </summary>
    public static class ThemeService
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>Raised after theme accent/styles are set up or refreshed.</summary>
        public static event Action ThemeChanged;

        public static bool IsDark => true;

        /// <summary>Convenience palette lookups for code-behind. Always resolve on demand so they
        /// pick up the current theme.</summary>
        public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

        static ThemeService()
        {
            // Intercept theme changes and force Dark mode
            ApplicationThemeManager.Changed += (theme, accent) =>
            {
                if (theme != ApplicationTheme.Dark)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ApplicationThemeManager.Apply(
                            ApplicationTheme.Dark,
                            Wpf.Ui.Controls.WindowBackdropType.Mica,
                            updateAccent: false);
                    }));
                }

                Color customAccent = Color("AppAccentColor");
                ApplicationAccentColorManager.Apply(customAccent, ApplicationTheme.Dark);
                MapAccentBrushes();
                foreach (Window w in Application.Current.Windows)
                {
                    ApplyTitleBar(w, true);
                }
                ThemeChanged?.Invoke();
            };
        }

        public static Color Color(string key) => (Color)Application.Current.FindResource(key);

        public static void MapAccentBrushes()
        {
            if (Application.Current == null) return;

            if (Application.Current.Resources.Contains("SystemAccentColorSecondaryBrush"))
            {
                Application.Current.Resources["AppAccentBrush"] = Application.Current.Resources["SystemAccentColorSecondaryBrush"];
            }
            if (Application.Current.Resources.Contains("SystemAccentColorTertiaryBrush"))
            {
                Application.Current.Resources["AppAccentHoverBrush"] = Application.Current.Resources["SystemAccentColorTertiaryBrush"];
            }
        }

        /// <summary>Applies the dark theme: WPF-UI dark recolor, custom dark accent application,
        /// dark window title bars, and notifies listeners.</summary>
        public static void Apply()
        {
            // Recolor WPF-UI controls. Keep our own accent (updateAccent: false).
            ApplicationThemeManager.Apply(
                ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                updateAccent: false);

            Color customAccent = Color("AppAccentColor");
            ApplicationAccentColorManager.Apply(
                customAccent,
                ApplicationTheme.Dark);

            MapAccentBrushes();

            foreach (Window w in Application.Current.Windows)
            {
                ApplyTitleBar(w, true);
            }

            ThemeChanged?.Invoke();
        }

        /// <summary>Toggles the immersive dark title bar for a single window. No-op until the window
        /// has a native handle (call again from OnSourceInitialized).</summary>
        public static void ApplyTitleBar(Window window, bool dark)
        {
            if (window == null) return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
    }
}
