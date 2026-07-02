using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Filey
{
    public partial class ExplorerPage : Page
    {
        private readonly MainWindow _mainWindow;
        private readonly AppSettings _settings;

        public DirectoryViewModel LeftViewModel => _mainWindow.LeftViewModel;
        public DirectoryViewModel RightViewModel => _mainWindow.RightViewModel;

        private bool _rightPaneActivated = true;
        private int _fontSizeStep = 0;
        private RightPaneMode _currentRightPaneMode = RightPaneMode.PreviewPane;
        private GridLength _savedLeftPaneWidth = new GridLength(1000, GridUnitType.Star);
        private GridLength _savedRightPaneWidth = new GridLength(440, GridUnitType.Star);

        public ExplorerPage(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            _settings = mainWindow.Settings;

            InitializeComponent();

            // Track left selection and path updates to trigger preview load
            LeftViewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(DirectoryViewModel.SelectedItem) ||
                    ev.PropertyName == nameof(DirectoryViewModel.CurrentPath))
                {
                    OnLeftSelectionChanged();
                }
            };

            // Refresh when directory contents change
            LeftDirectoryPane.DirectoryContentsChanged += (s, path) => OnDirectoryContentsChanged(RightViewModel, path);
            RightDirectoryPane.DirectoryContentsChanged += (s, path) => OnDirectoryContentsChanged(LeftViewModel, path);

            // Search results overlays
            LeftSearchResults.ResultActivated += (s, item) =>
            {
                HideSearchResults(LeftSearchResults);
                OpenSearchResult(item, LeftViewModel, LeftDirectoryPane);
            };
            LeftSearchResults.CloseRequested += (s, e) => HideSearchResults(LeftSearchResults);

            RightSearchResults.ResultActivated += (s, item) =>
            {
                HideSearchResults(RightSearchResults);
                OpenSearchResult(item, RightViewModel, RightDirectoryPane);
            };
            RightSearchResults.CloseRequested += (s, e) => HideSearchResults(RightSearchResults);

            // Favourites panel setup
            LeftFavouritesPanel.CurrentPathProvider = () => GetActiveViewModel().CurrentPath;
            LeftFavouritesPanel.NavigationRequested += (s, path) => GetActiveViewModel().LoadDirectory(path);

            RightPreviewControl.DirectoryClicked += (s, path) =>
            {
                GetActiveViewModel()?.LoadDirectory(path);
            };
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySplitterPositions();

            _currentRightPaneMode = (RightPaneMode)_settings.RightPaneMode;
            if (_currentRightPaneMode == RightPaneMode.Off)
            {
                _currentRightPaneMode = RightPaneMode.RightPane;
            }
            SetRightPaneMode(_currentRightPaneMode);

            CompactModeToggle.IsChecked = _settings.CompactMode;
            ApplyCompactMode(_settings.CompactMode);
        }

        public void SaveSplitterPositions()
        {
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

        public void ApplyCompactMode(bool compact)
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

            FontSizeButtonText.Text = _fontSizeStep == 0 ? "Font: Default" : "Font: Large";
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

        private void OnDirectoryContentsChanged(DirectoryViewModel otherViewModel, string changedPath)
        {
            if (otherViewModel != null &&
                string.Equals(otherViewModel.CurrentDirectory, changedPath, StringComparison.OrdinalIgnoreCase))
            {
                otherViewModel.LoadDirectory(otherViewModel.CurrentPath, pushToHistory: false);
            }

            if (_currentRightPaneMode == RightPaneMode.PreviewPane &&
                string.Equals(LeftViewModel.CurrentDirectory, changedPath, StringComparison.OrdinalIgnoreCase))
            {
                var selected = LeftViewModel.SelectedItem;
                RightPreviewControl.PreviewFile(selected != null ? selected.FullPath : LeftViewModel.CurrentDirectory);
            }
        }

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
                UpdateRightPaneMaxWidth();
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

        public void SetRightPaneMode(RightPaneMode mode)
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

                    OnLeftSelectionChanged();
                    break;

                case RightPaneMode.RightPane:
                    RightPaneCol.MinWidth = 440;
                    LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
                    RightPaneCol.Width = new GridLength(1, GridUnitType.Star);

                    CentreSplitter.Visibility = Visibility.Visible;
                    RightPaneGrid.Visibility = Visibility.Visible;
                    RightPreviewControl.Visibility = Visibility.Collapsed;

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

        public void HandlePreviewMouseDown(MouseButtonEventArgs e)
        {
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

        private void LeftAddressBar_SearchResultChosen(object sender, FolderItem item)
        {
            OpenSearchResult(item, LeftViewModel, LeftDirectoryPane);
        }

        private void RightAddressBar_SearchResultChosen(object sender, FolderItem item)
        {
            OpenSearchResult(item, RightViewModel, RightDirectoryPane);
        }

        private void LeftAddressBar_SearchAllRequested(object sender, SearchAllRequest request)
        {
            ShowSearchResults(LeftSearchResults, request);
        }

        private void RightAddressBar_SearchAllRequested(object sender, SearchAllRequest request)
        {
            ShowSearchResults(RightSearchResults, request);
        }

        private async void ShowSearchResults(SearchResultsView view, SearchAllRequest request)
        {
            if (view == null || request == null) return;

            view.Visibility = Visibility.Visible;
            view.ShowSearching(request.Query);

            var results = await IndexService.Instance.SearchAsync(
                request.Query, request.ActiveDirectory, max: 500);

            if (view.Visibility != Visibility.Visible) return;
            view.ShowResults(request.Query, results);
        }

        private void HideSearchResults(SearchResultsView view)
        {
            if (view == null) return;
            view.Visibility = Visibility.Collapsed;
        }

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

        private string PromptForHomePath(string prompt, string existingHome, string currentDirectory)
            => MainWindow.PromptForHomePath(Window.GetWindow(this), prompt, existingHome, currentDirectory);

        private static string ResolveHomePath(string home)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrEmpty(home) && System.IO.Directory.Exists(home) ? home : userProfile;
        }

        public void HandleSizeChanged(SizeChangedEventArgs e)
        {
            UpdateRightPaneMaxWidth();
        }

        private void UpdateRightPaneMaxWidth()
        {
            if (RightPaneCol == null) return;

            if (_currentRightPaneMode == RightPaneMode.PreviewPane)
            {
                var selected = LeftViewModel.SelectedItem;
                bool isExcel = false;
                if (selected != null && !selected.IsDirectory)
                {
                    string ext = System.IO.Path.GetExtension(selected.FullPath).ToLower();
                    isExcel = ext == ".xlsx" || ext == ".xls" || ext == ".xlsm" || ext == ".xlsb";
                }

                if (isExcel)
                {
                    RightPaneCol.MaxWidth = double.PositiveInfinity;
                }
                else
                {
                    RightPaneCol.MaxWidth = this.ActualWidth / 2;
                }
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
