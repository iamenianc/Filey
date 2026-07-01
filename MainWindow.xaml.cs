using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

namespace Filey
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        public DirectoryViewModel LeftViewModel { get; }
        public DirectoryViewModel RightViewModel { get; }
        internal AppSettings Settings => _settings;
        private AppSettings _settings;

        public ExplorerPage ExplorerPageInstance { get; private set; }
        private SettingsPage _settingsPageInstance;
        private CrawlerStatusPage _crawlerStatusPageInstance;

        public FavouritesPanel LeftFavouritesPanel => ExplorerPageInstance?.LeftFavouritesPanel;

        public void OpenPathInRightPane(string path)
        {
            ExplorerPageInstance?.OpenPathInRightPane(path);
        }

        public MainWindow()
        {
            LeftViewModel = new DirectoryViewModel();
            RightViewModel = new DirectoryViewModel();

            _settings = SettingsService.Load();
            ThemeService.Apply(ThemeService.Parse(_settings.Theme));
            BookmarkStore.Instance.LoadFromDisk();

            var history = NavigationHistoryStore.Load();
            LeftViewModel.RestoreBackStack(history.Left);
            RightViewModel.RestoreBackStack(history.Right);

            InitializeComponent();

            // Set up Windows 11 system accent/theme sync
            SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: true);

            this.Closing += MainWindow_Closing;

            // Refresh warm roots when the window gets activated
            this.Activated += (s, ev) => IndexService.Instance.RefreshWarmRoots();

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialize pages
            ExplorerPageInstance = new ExplorerPage(this);
            _settingsPageInstance = new SettingsPage(this);
            _crawlerStatusPageInstance = new CrawlerStatusPage();

            // Default to Explorer View
            ExplorerItem.IsActive = true;
            RootFrame.Navigate(ExplorerPageInstance);

            // Startup Crawler Service
            IndexService.Instance.Start(_settings);
            IndexService.Instance.PrioritizeActiveDirectory(LeftViewModel.CurrentDirectory);
            if (!string.IsNullOrEmpty(RightViewModel.CurrentDirectory))
                IndexService.Instance.PrioritizeActiveDirectory(RightViewModel.CurrentDirectory);
        }

        private void NavigationItem_Click(object sender, RoutedEventArgs e)
        {
            if (RootFrame == null) return;

            if (sender == ExplorerItem)
            {
                ExplorerItem.IsActive = true;
                IndexerItem.IsActive = false;
                SettingsItem.IsActive = false;
                RootFrame.Navigate(ExplorerPageInstance);
            }
            else if (sender == IndexerItem)
            {
                ExplorerItem.IsActive = false;
                IndexerItem.IsActive = true;
                SettingsItem.IsActive = false;
                RootFrame.Navigate(_crawlerStatusPageInstance);
            }
            else if (sender == SettingsItem)
            {
                ExplorerItem.IsActive = false;
                IndexerItem.IsActive = false;
                SettingsItem.IsActive = true;
                RootFrame.Navigate(_settingsPageInstance);
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (ExplorerPageInstance != null)
            {
                ExplorerPageInstance.SaveSplitterPositions();
                _settings.CompactMode = ExplorerPageInstance.CompactModeToggle.IsChecked == true;
            }

            _settings.Theme = ThemeService.ToSettingValue(ThemeService.Current);
            SettingsService.Save(_settings);

            BookmarkStore.Instance.SaveToDisk();

            NavigationHistoryStore.Save(new NavigationHistoryRecord
            {
                Left = LeftViewModel.GetBackStackSnapshot(),
                Right = RightViewModel.GetBackStackSnapshot(),
            });

            IndexService.Instance.Shutdown();
            SystemThemeWatcher.UnWatch(this);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ThemeService.ApplyTitleBar(this, ThemeService.IsDark);
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (ExplorerPageInstance != null)
            {
                ExplorerPageInstance.HandlePreviewMouseDown(e);
            }
        }

        public static string PromptForHomePath(Window owner, string prompt, string existingHome, string currentDirectory)
        {
            string initial = !string.IsNullOrEmpty(existingHome) ? existingHome : currentDirectory;

            var dialog = new InputDialog("Set Home Folder", prompt, initial) { Owner = owner };
            if (dialog.ShowDialog() != true) return null;

            string path = dialog.Value;
            if (!System.IO.Directory.Exists(path))
            {
                MessageBox.Show(
                    $"Folder does not exist:\n\n{path}",
                    "Set Home Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return path;
        }
    }
}
