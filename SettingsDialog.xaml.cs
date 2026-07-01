using System.Windows;
using System.Windows.Input;

namespace Filey
{
    /// <summary>
    /// Exposes the user-meaningful settings persisted in %APPDATA%\Filey\settings.json.
    /// Every control live-applies through the owning MainWindow and saves immediately,
    /// the same immediate-save-on-change pattern MainWindow itself uses.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        private readonly MainWindow _mainWindow;
        private bool _restoringState;

        public SettingsDialog(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;

            Loaded += (s, e) => RestoreState();
        }

        private void RestoreState()
        {
            _restoringState = true;

            bool isLight = ThemeService.Current == AppTheme.Light;
            ThemeToggleControl.IsChecked = isLight;
            ThemeToggleControl.Content = isLight ? "Theme: Light" : "Theme: Dark";

            CompactModeToggleControl.IsChecked = _mainWindow.Settings.CompactMode;

            LeftHomePathBox.Text = _mainWindow.Settings.LeftHomePath ?? string.Empty;
            RightHomePathBox.Text = _mainWindow.Settings.RightHomePath ?? string.Empty;

            UpdateRightPaneButtons();

            _restoringState = false;
        }

        private void UpdateRightPaneButtons()
        {
            var mode = _mainWindow.CurrentRightPaneMode;
            RightPaneOffButton.Appearance = mode == MainWindow.RightPaneMode.Off
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            RightPanePreviewButton.Appearance = mode == MainWindow.RightPaneMode.PreviewPane
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            RightPaneDualButton.Appearance = mode == MainWindow.RightPaneMode.RightPane
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void ThemeToggleControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_restoringState) return;

            var theme = ThemeToggleControl.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
            ThemeToggleControl.Content = theme == AppTheme.Light ? "Theme: Light" : "Theme: Dark";
            _mainWindow.ApplyThemeFromSettingsDialog(theme);
        }

        private void CompactModeToggleControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_restoringState) return;
            _mainWindow.ApplyCompactModeFromSettingsDialog(CompactModeToggleControl.IsChecked == true);
        }

        private void RightPaneOffButton_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ApplyRightPaneModeFromSettingsDialog(MainWindow.RightPaneMode.Off);
            UpdateRightPaneButtons();
        }

        private void RightPanePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ApplyRightPaneModeFromSettingsDialog(MainWindow.RightPaneMode.PreviewPane);
            UpdateRightPaneButtons();
        }

        private void RightPaneDualButton_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ApplyRightPaneModeFromSettingsDialog(MainWindow.RightPaneMode.RightPane);
            UpdateRightPaneButtons();
        }

        private void BrowseLeftHomeButton_Click(object sender, RoutedEventArgs e)
        {
            string path = MainWindow.PromptForHomePath(this, "Set the left pane's Home folder:",
                _mainWindow.Settings.LeftHomePath, _mainWindow.LeftViewModel.CurrentDirectory);
            if (path == null) return;

            _mainWindow.Settings.LeftHomePath = path;
            SettingsService.Save(_mainWindow.Settings);
            LeftHomePathBox.Text = path;
        }

        private void BrowseRightHomeButton_Click(object sender, RoutedEventArgs e)
        {
            string path = MainWindow.PromptForHomePath(this, "Set the right pane's Home folder:",
                _mainWindow.Settings.RightHomePath, _mainWindow.RightViewModel.CurrentDirectory);
            if (path == null) return;

            _mainWindow.Settings.RightHomePath = path;
            SettingsService.Save(_mainWindow.Settings);
            RightHomePathBox.Text = path;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
