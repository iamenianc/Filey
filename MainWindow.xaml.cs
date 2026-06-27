using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Filey
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Set false to keep the native (light) Windows title bar.
        private const bool UseDarkTitleBar = true;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public DirectoryViewModel LeftViewModel { get; }
        public DirectoryViewModel RightViewModel { get; }

        private AppSettings _settings;

        /// <summary>True once the right pane has been activated out of its inactive state.</summary>
        private bool _rightPaneActivated;

        public MainWindow()
        {
            LeftViewModel = new DirectoryViewModel();
            RightViewModel = new DirectoryViewModel();

            _settings = SettingsService.Load();
            BookmarkStore.Instance.LoadFromDisk();

            InitializeComponent();

            this.Closing += MainWindow_Closing;

            // Wire up the (single, shared) Favourites panel. It adds bookmarks for the
            // active side's current path and navigates that side.
            LeftFavouritesPanel.CurrentPathProvider = () => GetActiveViewModel().CurrentPath;
            LeftFavouritesPanel.NavigationRequested += (s, path) => GetActiveViewModel().LoadDirectory(path);

            Loaded += (s, e) =>
            {
                ApplySplitterPositions();

                RightPaneToggle.IsChecked = _settings.RightPaneVisible;
                SetRightPaneVisible(_settings.RightPaneVisible);

                ShowHiddenToggle.IsChecked = _settings.ShowHidden;

                CompactModeToggle.IsChecked = _settings.CompactMode;
                ApplyCompactMode(_settings.CompactMode);
            };

            DirectoryViewModel.ShowHidden = _settings.ShowHidden;

            RestorePersistedState();
        }

        /// <summary>
        /// Opens the left pane at its configured Home path on startup.
        /// </summary>
        private void RestorePersistedState()
        {
            LeftViewModel.LoadDirectory(ResolveHomePath(_settings.LeftHomePath));
        }

        private static string ResolveHomePath(string home)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return ResolveStartPath(home, userProfile);
        }

        private static string ResolveStartPath(string preferred, string fallback)
        {
            return !string.IsNullOrEmpty(preferred) && System.IO.Directory.Exists(preferred)
                ? preferred
                : fallback;
        }

        private void ApplySplitterPositions()
        {
            var widths = _settings.SplitterPositions;
            if (widths == null || widths.Count != 5) return;

            LeftFavouritesCol.Width = new GridLength(widths[0]);
            LeftDirectoryPane.ParentFoldersWidth = new GridLength(widths[1]);
            LeftDirectoryPane.ContentsWidth = new GridLength(widths[2]);
            RightDirectoryPane.ContentsWidth = new GridLength(widths[3]);
            RightDirectoryPane.ParentFoldersWidth = new GridLength(widths[4]);
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _settings.RightPaneVisible = RightPaneToggle.IsChecked == true;
            _settings.ShowHidden = ShowHiddenToggle.IsChecked == true;
            _settings.CompactMode = CompactModeToggle.IsChecked == true;

            if (_settings.RightPaneVisible)
            {
                _settings.SplitterPositions = new System.Collections.Generic.List<double>
                {
                    LeftFavouritesCol.ActualWidth,
                    LeftDirectoryPane.ParentFoldersActualWidth,
                    LeftDirectoryPane.ContentsActualWidth,
                    RightDirectoryPane.ContentsActualWidth,
                    RightDirectoryPane.ParentFoldersActualWidth,
                };
            }
            SettingsService.Save(_settings);

            BookmarkStore.Instance.SaveToDisk();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (UseDarkTitleBar)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
        }

        private void RightPaneOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (RightPaneOverlay.Visibility == Visibility.Visible)
            {
                ActivateRightPane();
            }
        }

        /// <summary>
        /// Brings the right pane out of its inactive state, opening it at the same
        /// directory the left pane is currently viewing for side-by-side operations.
        /// </summary>
        private void ActivateRightPane()
        {
            string syncPath = LeftViewModel.CurrentDirectory;
            if (string.IsNullOrEmpty(syncPath) || !System.IO.Directory.Exists(syncPath))
            {
                syncPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            RightViewModel.LoadDirectory(syncPath);
            RightPaneOverlay.Visibility = Visibility.Collapsed;
            _rightPaneActivated = true;
        }

        private void CompactModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (LeftViewModel == null) return;
            ApplyCompactMode(CompactModeToggle.IsChecked == true);
        }

        private void ApplyCompactMode(bool compact)
        {
            Resources["RowItemPadding"] = compact ? new Thickness(4, 1, 4, 1) : new Thickness(4, 4, 4, 4);
            Resources["RowCellMargin"] = compact ? new Thickness(2, 0, 2, 0) : new Thickness(2);

            double rowHeight = compact ? 22 : 28;
            if (LeftDirectoryPane != null) LeftDirectoryPane.EstimatedRowHeight = rowHeight;
            if (RightDirectoryPane != null) RightDirectoryPane.EstimatedRowHeight = rowHeight;
        }

        private void ShowHiddenToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (LeftViewModel == null) return;
            DirectoryViewModel.ShowHidden = ShowHiddenToggle.IsChecked == true;
            ReloadBothPanes();
        }

        private void ReloadBothPanes()
        {
            if (!string.IsNullOrEmpty(LeftViewModel.CurrentDirectory))
                LeftViewModel.LoadDirectory(LeftViewModel.CurrentDirectory, pushToHistory: false);

            if (_rightPaneActivated && !string.IsNullOrEmpty(RightViewModel.CurrentDirectory))
                RightViewModel.LoadDirectory(RightViewModel.CurrentDirectory, pushToHistory: false);
        }

        private GridLength _savedRightPaneWidth = new GridLength(1, GridUnitType.Star);

        private void RightPaneToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (RightPaneCol == null) return;
            SetRightPaneVisible(RightPaneToggle.IsChecked == true);
        }

        private void SetRightPaneVisible(bool visible)
        {
            if (visible)
            {
                RightPaneCol.MinWidth = 300;
                RightPaneCol.Width = _savedRightPaneWidth;
                LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                if (RightPaneCol.ActualWidth > 0)
                {
                    _savedRightPaneWidth = new GridLength(RightPaneCol.ActualWidth);
                }
                RightPaneCol.MinWidth = 0;
                RightPaneCol.Width = new GridLength(0);
                LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);

            if (e.ChangedButton == MouseButton.XButton1)
            {
                if (LeftDirectoryPane != null && LeftDirectoryPane.IsMouseOverParentFolders)
                {
                    if (LeftViewModel.CanGoToParent)
                    {
                        LeftViewModel.GoToParent();
                    }
                    e.Handled = true;
                }
                else if (RightDirectoryPane != null && RightDirectoryPane.IsMouseOverParentFolders)
                {
                    if (RightViewModel.CanGoToParent)
                    {
                        RightViewModel.GoToParent();
                    }
                    e.Handled = true;
                }
                else
                {
                    var activeVm = GetActiveViewModel();
                    if (activeVm != null && activeVm.CanGoBack)
                    {
                        activeVm.GoBack();
                        e.Handled = true;
                    }
                }
            }
            else if (e.ChangedButton == MouseButton.XButton2)
            {
                var activeVm = GetActiveViewModel();
                if (activeVm != null && activeVm.CanGoForward)
                {
                    activeVm.GoForward();
                    e.Handled = true;
                }
            }
        }

        private static FolderItem ItemFromMenu(object sender)
        {
            if (sender is MenuItem mi) return mi.DataContext as FolderItem;
            return null;
        }

        private DirectoryViewModel SideViewModelFromMenu(object sender)
        {
            var mi = sender as MenuItem;
            var menu = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                       ?? FindAncestor<ContextMenu>(mi);
            var target = menu?.PlacementTarget as DependencyObject;
            DependencyObject cur = target;
            while (cur != null)
            {
                if (cur is FrameworkElement fe)
                {
                    if (fe.DataContext == LeftViewModel) return LeftViewModel;
                    if (fe.DataContext == RightViewModel) return RightViewModel;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return GetActiveViewModel();
        }

        private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is ContextMenu menu)) return;
            var item = menu.DataContext as FolderItem;
            foreach (var obj in menu.Items)
            {
                if (obj is MenuItem mi && (string.Equals(mi.Name, "CtxFavouriteItem")
                    || (mi.Header as string)?.Contains("Favourites") == true))
                {
                    var side = SideForElement(menu.PlacementTarget as DependencyObject);
                    bool exists = item != null && BookmarkStore.Instance.Contains(side, item.FullPath);
                    mi.Header = exists ? "Remove from Favourites" : "Add to Favourites";
                }
            }
        }

        private Side SideForElement(DependencyObject target)
        {
            DependencyObject cur = target;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && fe.DataContext == RightViewModel)
                    return Side.Right;
                if (cur is FrameworkElement fe2 && fe2.DataContext == LeftViewModel)
                    return Side.Left;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return Side.Left;
        }

        private void CtxOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item != null) ContextActions.OpenInExplorer(item.FullPath);
        }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item != null) ContextActions.CopyPath(item.FullPath);
        }

        private void CtxToggleFavourite_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item == null) return;
            var vm = SideViewModelFromMenu(sender);
            var side = vm == RightViewModel ? Side.Right : Side.Left;

            var existing = BookmarkStore.Instance.Find(side, item.FullPath);
            if (existing != null)
            {
                BookmarkStore.Instance.Remove(side, existing);
            }
            else
            {
                LeftFavouritesPanel.AddFavourite(item.FullPath);
            }
        }

        private DirectoryViewModel GetActiveViewModel()
        {
            if (RightPaneOverlay.Visibility == Visibility.Visible)
            {
                return LeftViewModel;
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused != null)
            {
                DependencyObject current = focused;
                while (current != null)
                {
                    if (current is FrameworkElement fe)
                    {
                        if (fe.DataContext == LeftViewModel) return LeftViewModel;
                        if (fe.DataContext == RightViewModel) return RightViewModel;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (RightPaneGrid != null && RightPaneGrid.IsMouseOver)
            {
                return RightViewModel;
            }

            return LeftViewModel;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T ancestor)
                {
                    return ancestor;
                }
                current = VisualTreeHelper.GetParent(current);
            } while (current != null);
            return null;
        }

        // --- Left/Right DirectoryPane Home Handlers ---
        private void LeftDirectoryPane_HomeRequested(object sender, EventArgs e)
        {
            LeftViewModel.LoadDirectory(ResolveHomePath(_settings.LeftHomePath));
        }

        private void LeftDirectoryPane_SetHomeRequested(object sender, EventArgs e)
        {
            string path = PromptForHomePath("Set the left pane's Home folder:", _settings.LeftHomePath, LeftViewModel.CurrentDirectory);
            if (path == null) return;

            _settings.LeftHomePath = path;
            SettingsService.Save(_settings);
        }

        private void RightDirectoryPane_HomeRequested(object sender, EventArgs e)
        {
            string home = _settings.RightHomePath;
            RightViewModel.LoadDirectory(ResolveHomePath(home));
        }

        private void RightDirectoryPane_SetHomeRequested(object sender, EventArgs e)
        {
            string path = PromptForHomePath("Set the right pane's Home folder:", _settings.RightHomePath, RightViewModel.CurrentDirectory);
            if (path == null) return;

            _settings.RightHomePath = path;
            SettingsService.Save(_settings);
        }

        /// <summary>
        /// Shows a free-text input dialog for a Home path, pre-filled with the existing
        /// setting (falling back to the pane's current folder). Returns the trimmed path,
        /// or null if cancelled or the entered folder doesn't exist.
        /// </summary>
        private string PromptForHomePath(string prompt, string existingHome, string currentDirectory)
        {
            string initial = !string.IsNullOrEmpty(existingHome) ? existingHome : currentDirectory;

            var dialog = new InputDialog("Set Home Folder", prompt, initial) { Owner = this };
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
