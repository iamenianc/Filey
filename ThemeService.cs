using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Filey
{
    public enum AppTheme
    {
        Dark,
        Light
    }

    /// <summary>
    /// Central runtime theme manager. Owns the application's Light/Dark state and applies it by
    /// (1) recoloring WPF-UI controls via <see cref="ApplicationThemeManager"/>, (2) swapping our
    /// own semantic color palette (Themes/Colors.Dark.xaml ↔ Colors.Light.xaml) in the merged
    /// dictionaries, and (3) toggling the immersive (dark) title bar on every open window.
    ///
    /// All palette colors/fonts are referenced via DynamicResource/FindResource so replacing the
    /// palette dictionary re-resolves them everywhere in a single pass.
    /// </summary>
    public static class ThemeService
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // The sentinel key present only in Colors.Dark.xaml / Colors.Light.xaml, used to locate the
        // active palette dictionary so it can be replaced at runtime.
        private const string ThemeSentinelKey = "ThemeName";

        /// <summary>Raised after a theme has been applied, so imperative consumers (e.g. the folder
        /// tree, an open markdown preview) can re-render with the new palette.</summary>
        public static event Action ThemeChanged;

        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        public static bool IsDark => Current == AppTheme.Dark;

        /// <summary>Parses a persisted theme string ("light"/"dark"); defaults to Dark.</summary>
        public static AppTheme Parse(string value)
            => string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

        public static string ToSettingValue(AppTheme theme) => theme == AppTheme.Light ? "light" : "dark";

        /// <summary>Convenience palette lookups for code-behind. Always resolve on demand so they
        /// pick up the current theme.</summary>
        public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

        public static Color Color(string key) => (Color)Application.Current.FindResource(key);

        /// <summary>Applies the given theme: WPF-UI recolor first, then our palette swap (so our
        /// keys win over WPF-UI defaults), then per-window title bars, then notifies listeners.</summary>
        public static void Apply(AppTheme theme)
        {
            Current = theme;

            // 1) Recolor WPF-UI controls. This rewrites the WPF-UI ThemesDictionary entry, so it
            //    must run BEFORE our palette swap. Keep our own accent (updateAccent: false).
            ApplicationThemeManager.Apply(
                theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                updateAccent: false);

            // Set our custom neutral accent color for WPF-UI (changes shade based on theme)
            if (theme == AppTheme.Light)
            {
                ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(113, 128, 150), ApplicationTheme.Light);
            }
            else
            {
                ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(74, 85, 104), ApplicationTheme.Dark);
            }

            // 2) Swap our semantic palette dictionary in place.
            SwapPalette(theme);

            // 3) Update the title bar on every open window (MainWindow + any PreviewWindow).
            foreach (Window w in Application.Current.Windows)
            {
                ApplyTitleBar(w, IsDark);
            }

            ThemeChanged?.Invoke();
        }

        private static void SwapPalette(AppTheme theme)
        {
            var dicts = Application.Current.Resources.MergedDictionaries;

            ResourceDictionary current = dicts.FirstOrDefault(d => d.Contains(ThemeSentinelKey));

            var replacement = new ResourceDictionary
            {
                Source = new Uri(
                    theme == AppTheme.Light
                        ? "pack://application:,,,/Themes/Colors.Light.xaml"
                        : "pack://application:,,,/Themes/Colors.Dark.xaml",
                    UriKind.Absolute)
            };

            if (current != null)
            {
                int index = dicts.IndexOf(current);
                // Insert the replacement at the same slot, then drop the old one so DynamicResource
                // references re-resolve against the new palette.
                dicts.Insert(index, replacement);
                dicts.Remove(current);
            }
            else
            {
                dicts.Add(replacement);
            }
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
