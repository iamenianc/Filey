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
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        public DirectoryViewModel LeftViewModel { get; }
        public DirectoryViewModel RightViewModel { get; }

        private AppSettings _settings;

        /// <summary>True once the right pane has been activated out of its inactive state.</summary>
        private bool _rightPaneActivated = true;
        private int _fontSizeStep = 0;

        /// <summary>Suppresses the theme toggle handler while its initial state is restored at startup.</summary>
        private bool _restoringThemeToggle;

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

            this.Closing += MainWindow_Closing;
            this.SizeChanged += MainWindow_SizeChanged;

            // Refresh warm (history-derived) roots when the window regains focus.
            this.Activated += (s, ev) => IndexService.Instance.RefreshWarmRoots();

            // Track left selection and path updates to trigger preview load
            LeftViewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(DirectoryViewModel.SelectedItem) ||
                    ev.PropertyName == nameof(DirectoryViewModel.CurrentPath))
                {
                    OnLeftSelectionChanged();
                }
            };

            // Prioritise indexing of whichever directory a pane navigates to, so the folder
            // the user is currently in is searchable immediately (ahead of the seed crawl).
            LeftViewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(DirectoryViewModel.CurrentPath))
                    IndexService.Instance.PrioritizeActiveDirectory(LeftViewModel.CurrentDirectory);
            };
            RightViewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(DirectoryViewModel.CurrentPath))
                    IndexService.Instance.PrioritizeActiveDirectory(RightViewModel.CurrentDirectory);
            };

            RightPreviewControl.DirectoryClicked += (s, path) =>
            {
                GetActiveViewModel()?.LoadDirectory(path);
            };

            // Wire up the (single, shared) Favourites panel. It adds bookmarks for the
            // active side's current path and navigates that side.
            LeftFavouritesPanel.CurrentPathProvider = () => GetActiveViewModel().CurrentPath;
            LeftFavouritesPanel.NavigationRequested += (s, path) => GetActiveViewModel().LoadDirectory(path);

            Loaded += (s, e) =>
            {
                ApplySplitterPositions();

                _currentRightPaneMode = (RightPaneMode)_settings.RightPaneMode;
                if (_currentRightPaneMode == RightPaneMode.Off)
                {
                    _currentRightPaneMode = RightPaneMode.RightPane;
                }
                SetRightPaneMode(_currentRightPaneMode);

                // The Show-hidden toggle is intentionally session-only: it always starts
                // off and is never persisted (see ShowHiddenToggle_Changed / AppSettings).

                CompactModeToggle.IsChecked = _settings.CompactMode;
                ApplyCompactMode(_settings.CompactMode);

                // Reflect the already-applied theme on the toggle without re-triggering Apply/Save.
                _restoringThemeToggle = true;
                ThemeToggle.IsChecked = ThemeService.Current == AppTheme.Light;
                ThemeToggle.Content = ThemeService.IsDark ? "Theme: Dark" : "Theme: Light";
                _restoringThemeToggle = false;

                // Build/refresh the selective file index in the background, indexing the
                // already-open directory first so it's searchable immediately.
                IndexService.Instance.Start(_settings);
                IndexService.Instance.PrioritizeActiveDirectory(LeftViewModel.CurrentDirectory);
                if (!string.IsNullOrEmpty(RightViewModel.CurrentDirectory))
                    IndexService.Instance.PrioritizeActiveDirectory(RightViewModel.CurrentDirectory);
            };

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
            _settings.RightPaneMode = (int)_currentRightPaneMode;
            _settings.RightPaneVisible = _currentRightPaneMode != RightPaneMode.Off;
            // ShowHidden is deliberately not persisted; the toggle resets to off each launch.
            _settings.CompactMode = CompactModeToggle.IsChecked == true;

            if (_currentRightPaneMode != RightPaneMode.Off)
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

            NavigationHistoryStore.Save(new NavigationHistoryRecord
            {
                Left = LeftViewModel.GetBackStackSnapshot(),
                Right = RightViewModel.GetBackStackSnapshot(),
            });

            IndexService.Instance.Shutdown();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Match the title bar to the active theme now that the window has a native handle.
            ThemeService.ApplyTitleBar(this, ThemeService.IsDark);
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_restoringThemeToggle) return;

            var theme = ThemeToggle.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
            ThemeService.Apply(theme);
            ThemeToggle.Content = theme == AppTheme.Light ? "Theme: Light" : "Theme: Dark";

            _settings.Theme = ThemeService.ToSettingValue(theme);
            SettingsService.Save(_settings);
        }

        private void SwapPanesButton_Click(object sender, RoutedEventArgs e)
        {

            string leftPath = LeftViewModel.CurrentDirectory;
            string rightPath = RightViewModel.CurrentDirectory;
            if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath)) return;

            LeftViewModel.LoadDirectory(rightPath);
            RightViewModel.LoadDirectory(leftPath);
        }

        private void CompactModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (LeftViewModel == null) return;
            ApplyCompactMode(CompactModeToggle.IsChecked == true);
        }

        private void ApplyCompactMode(bool compact)
        {
            Resources["RowItemPadding"] = compact ? new Thickness(4, 0, 4, 0) : new Thickness(4, 1, 4, 1);
            Resources["RowCellMargin"] = compact ? new Thickness(2, 0, 2, 0) : new Thickness(2, 0, 2, 0);

            double rowHeight = compact ? 19 : 22;
            if (LeftDirectoryPane != null) LeftDirectoryPane.EstimatedRowHeight = rowHeight;
            if (RightDirectoryPane != null) RightDirectoryPane.EstimatedRowHeight = rowHeight;
        }

        private void ShowHiddenToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (LeftViewModel == null) return;
            DirectoryViewModel.ShowHidden = ShowHiddenToggle.IsChecked == true;
            ReloadBothPanes();
        }

        private void FontSizeButton_Click(object sender, RoutedEventArgs e)
        {
            _fontSizeStep = (_fontSizeStep + 1) % 2;
            double offset = _fontSizeStep * 1.5;

            Application.Current.Resources["AppFontSize"] = 11.5 + offset;
            Application.Current.Resources["NormalFontSize"] = 12.0 + offset;
            Application.Current.Resources["SmallFontSize"] = 10.0 + offset;
            Application.Current.Resources["HeaderFontSize"] = 11.0 + offset;
            Application.Current.Resources["LargeFontSize"] = 16.0 + offset;
            Application.Current.Resources["TitleFontSize"] = 14.0 + offset;

            FontSizeButton.Content = _fontSizeStep == 0 ? "Font: Default" : "Font: Large";
        }

        private void ReloadBothPanes()
        {
            if (!string.IsNullOrEmpty(LeftViewModel.CurrentDirectory))
                LeftViewModel.LoadDirectory(LeftViewModel.CurrentDirectory, pushToHistory: false);

            if (_rightPaneActivated && !string.IsNullOrEmpty(RightViewModel.CurrentDirectory))
                RightViewModel.LoadDirectory(RightViewModel.CurrentDirectory, pushToHistory: false);
        }

        public enum RightPaneMode
        {
            Off = 0,
            PreviewPane = 1,
            RightPane = 2
        }

        private RightPaneMode _currentRightPaneMode = RightPaneMode.PreviewPane;
        private GridLength _savedLeftPaneWidth = new GridLength(1000, GridUnitType.Star);
        private GridLength _savedRightPaneWidth = new GridLength(440, GridUnitType.Star);

        private void OnLeftSelectionChanged()
        {
            if (_currentRightPaneMode == RightPaneMode.PreviewPane)
            {
                var selected = LeftViewModel.SelectedItem;
                if (selected != null)
                {
                    RightPreviewControl.PreviewFile(selected.FullPath);
                }
                else if (!string.IsNullOrEmpty(LeftViewModel.CurrentDirectory))
                {
                    RightPreviewControl.PreviewFile(LeftViewModel.CurrentDirectory);
                }
                else
                {
                    RightPreviewControl.PreviewFile(null);
                }
            }
        }

        private void PreviewModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightPaneMode(RightPaneMode.PreviewPane);
        }

        private void DualModeButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightPaneMode(RightPaneMode.RightPane);
        }

        private void UpdateModeButtons()
        {
            if (PreviewModeButton != null)
                PreviewModeButton.IsChecked = (_currentRightPaneMode == RightPaneMode.PreviewPane);
            if (DualModeButton != null)
                DualModeButton.IsChecked = (_currentRightPaneMode == RightPaneMode.RightPane);
        }

        private void SetRightPaneMode(RightPaneMode mode)
        {
            _currentRightPaneMode = mode;
            _settings.RightPaneMode = (int)mode;
            _settings.RightPaneVisible = mode != RightPaneMode.Off;
            SettingsService.Save(_settings);

            UpdateRightPaneLayout();
            UpdateModeButtons();
        }

        private void UpdateRightPaneLayout()
        {
            if (RightPaneCol == null) return;

            switch (_currentRightPaneMode)
            {
                case RightPaneMode.Off:
                    if (RightPaneCol.ActualWidth > 0 && LeftPaneCol.ActualWidth > 0)
                    {
                        _savedRightPaneWidth = new GridLength(RightPaneCol.ActualWidth, GridUnitType.Star);
                        _savedLeftPaneWidth = new GridLength(LeftPaneCol.ActualWidth, GridUnitType.Star);
                    }
                    RightPaneCol.MinWidth = 0;
                    RightPaneCol.Width = new GridLength(0);
                    LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);

                    CentreSplitter.Visibility = Visibility.Collapsed;
                    RightPaneGrid.Visibility = Visibility.Collapsed;
                    RightPreviewControl.Visibility = Visibility.Collapsed;
                    break;

                case RightPaneMode.PreviewPane:
                    RightPaneCol.MinWidth = 440;
                    RightPaneCol.Width = _savedRightPaneWidth;
                    LeftPaneCol.Width = _savedLeftPaneWidth;

                    CentreSplitter.Visibility = Visibility.Visible;
                    RightPaneGrid.Visibility = Visibility.Collapsed;
                    RightPreviewControl.Visibility = Visibility.Visible;

                    OnLeftSelectionChanged(); // Trigger load of currently selected left pane item
                    break;

                case RightPaneMode.RightPane:
                    RightPaneCol.MinWidth = 440;
                    LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
                    RightPaneCol.Width = new GridLength(1, GridUnitType.Star);

                    CentreSplitter.Visibility = Visibility.Visible;
                    RightPaneGrid.Visibility = Visibility.Visible;
                    RightPreviewControl.Visibility = Visibility.Collapsed;

                    // Ensure right pane has a directory loaded
                    if (string.IsNullOrEmpty(RightViewModel.CurrentDirectory))
                    {
                        string syncPath = LeftViewModel.CurrentDirectory;
                        if (string.IsNullOrEmpty(syncPath) || !System.IO.Directory.Exists(syncPath))
                        {
                            syncPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        }
                        RightViewModel.LoadDirectory(syncPath);
                    }
                    break;
            }

            if (RightAddressBar != null)
            {
                RightAddressBar.Visibility = _currentRightPaneMode == RightPaneMode.RightPane
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            UpdateRightPaneMaxWidth();
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
            if (_currentRightPaneMode == RightPaneMode.PreviewPane || _currentRightPaneMode == RightPaneMode.Off)
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

            if (RightPaneGrid != null && RightPaneGrid.IsMouseOver && RightPaneGrid.Visibility == Visibility.Visible)
            {
                return RightViewModel;
            }

            return LeftViewModel;
        }

        // --- Index search -----------------------------------------------------------

        private void LeftAddressBar_SearchResultChosen(object sender, FolderItem item)
        {
            OpenSearchResult(item, LeftViewModel, LeftDirectoryPane);
        }

        private void RightAddressBar_SearchResultChosen(object sender, FolderItem item)
        {
            OpenSearchResult(item, RightViewModel, RightDirectoryPane);
        }

        /// <summary>
        /// Opens a search hit. A folder hit opens that folder; a file hit opens the file's
        /// containing folder and reveals (selects + scrolls to) the file — it is never
        /// launched.
        /// </summary>
        private void OpenSearchResult(FolderItem item, DirectoryViewModel vm, DirectoryPane pane)
        {
            if (item == null || vm == null) return;

            if (item.IsDirectory)
            {
                vm.LoadDirectory(item.FullPath);
                return;
            }

            string parent = System.IO.Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrEmpty(parent)) return;

            vm.LoadDirectory(parent);
            pane?.RevealItem(item.FullPath, isDirectory: false);
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

        // --- Left/Right AddressBar Handlers ---
        private void LeftAddressBar_NavigationRequested(object sender, string targetPath)
        {
            LeftViewModel?.LoadDirectory(targetPath);
        }

        private void LeftAddressBar_GoBackRequested(object sender, EventArgs e)
        {
            LeftViewModel?.GoBack();
        }

        private void LeftAddressBar_GoForwardRequested(object sender, EventArgs e)
        {
            LeftViewModel?.GoForward();
        }

        private void LeftAddressBar_HomeRequested(object sender, EventArgs e)
        {
            LeftDirectoryPane_HomeRequested(sender, e);
        }

        private void LeftAddressBar_SetHomeRequested(object sender, EventArgs e)
        {
            LeftDirectoryPane_SetHomeRequested(sender, e);
        }

        private void RightAddressBar_NavigationRequested(object sender, string targetPath)
        {
            RightViewModel?.LoadDirectory(targetPath);
        }

        private void RightAddressBar_GoBackRequested(object sender, EventArgs e)
        {
            RightViewModel?.GoBack();
        }

        private void RightAddressBar_GoForwardRequested(object sender, EventArgs e)
        {
            RightViewModel?.GoForward();
        }

        private void RightAddressBar_HomeRequested(object sender, EventArgs e)
        {
            RightDirectoryPane_HomeRequested(sender, e);
        }

        private void RightAddressBar_SetHomeRequested(object sender, EventArgs e)
        {
            RightDirectoryPane_SetHomeRequested(sender, e);
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

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateRightPaneMaxWidth();
        }

        private void UpdateRightPaneMaxWidth()
        {
            if (RightPaneCol == null) return;

            if (_currentRightPaneMode == RightPaneMode.PreviewPane)
            {
                RightPaneCol.MaxWidth = this.ActualWidth / 2;
            }
            else
            {
                RightPaneCol.MaxWidth = double.PositiveInfinity;
            }
        }

        public void OpenPathInRightPane(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            bool isDir = System.IO.Directory.Exists(path);
            string targetDir = isDir ? path : System.IO.Path.GetDirectoryName(path);

            if (_currentRightPaneMode != RightPaneMode.RightPane)
            {
                SetRightPaneMode(RightPaneMode.RightPane);
            }

            if (System.IO.Directory.Exists(targetDir))
            {
                RightViewModel.LoadDirectory(targetDir);

                if (!isDir)
                {
                    foreach (var item in RightViewModel.Contents)
                    {
                        if (string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            RightViewModel.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }

        private void CtxOpenRightPane_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item != null) OpenPathInRightPane(item.FullPath);
        }
    }
}
