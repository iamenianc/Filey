using System;
using System.Windows;
using System.Windows.Controls;

namespace Filey
{
    public partial class SettingsPage : Page
    {
        private readonly MainWindow _mainWindow;
        private readonly AppSettings _settings;
        private bool _restoringState;

        public SettingsPage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            _settings = mainWindow.Settings;

            InitializeComponent();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreState();
        }

        private void RestoreState()
        {
            _restoringState = true;

            CompactModeToggleControl.IsChecked = _settings.CompactMode;

            LeftHomePathBox.Text = _settings.LeftHomePath ?? string.Empty;
            RightHomePathBox.Text = _settings.RightHomePath ?? string.Empty;

            UpdateRightPaneButtons();

            _restoringState = false;
        }

        private void UpdateRightPaneButtons()
        {
            var mode = _settings.RightPaneMode;
            
            RightPaneOffButton.Appearance = mode == 0
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            RightPanePreviewButton.Appearance = mode == 1
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            RightPaneDualButton.Appearance = mode == 2
                ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

        private void CompactModeToggleControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_restoringState) return;

            bool compact = CompactModeToggleControl.IsChecked == true;
            _settings.CompactMode = compact;
            SettingsService.Save(_settings);

            var explorer = _mainWindow.ExplorerPageInstance;
            if (explorer != null)
            {
                explorer.CompactModeToggle.IsChecked = compact;
                explorer.ApplyCompactMode(compact);
            }
        }

        private void RightPaneOffButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyRightPaneMode(0);
        }

        private void RightPanePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyRightPaneMode(1);
        }

        private void RightPaneDualButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyRightPaneMode(2);
        }

        private void ApplyRightPaneMode(int mode)
        {
            _settings.RightPaneMode = mode;
            _settings.RightPaneVisible = mode != 0;
            SettingsService.Save(_settings);

            var explorer = _mainWindow.ExplorerPageInstance;
            if (explorer != null)
            {
                explorer.SetRightPaneMode((ExplorerPage.RightPaneMode)mode);
            }
            UpdateRightPaneButtons();
        }

        private void BrowseLeftHomeButton_Click(object sender, RoutedEventArgs e)
        {
            string path = MainWindow.PromptForHomePath(Window.GetWindow(this), "Set the left pane's Home folder:",
                _settings.LeftHomePath, _mainWindow.LeftViewModel.CurrentDirectory);
            if (path == null) return;

            _settings.LeftHomePath = path;
            SettingsService.Save(_settings);
            LeftHomePathBox.Text = path;
        }

        private void BrowseRightHomeButton_Click(object sender, RoutedEventArgs e)
        {
            string path = MainWindow.PromptForHomePath(Window.GetWindow(this), "Set the right pane's Home folder:",
                _settings.RightHomePath, _mainWindow.RightViewModel.CurrentDirectory);
            if (path == null) return;

            _settings.RightHomePath = path;
            SettingsService.Save(_settings);
            RightHomePathBox.Text = path;
        }
    }
}
