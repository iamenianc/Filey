using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        private bool _rightPaneActivated;

        public MainWindow()
        {
            LeftViewModel = new DirectoryViewModel();
            RightViewModel = new DirectoryViewModel();

            _settings = SettingsService.Load();
            BookmarkStore.Instance.LoadFromDisk();

            InitializeComponent();

            this.Closing += MainWindow_Closing;

            // Subscribe to ViewModel property changes to dynamically adjust folder pane heights
            LeftViewModel.PropertyChanged += LeftViewModel_PropertyChanged;
            RightViewModel.PropertyChanged += RightViewModel_PropertyChanged;
            this.SizeChanged += Window_SizeChanged;

            // Wire up the (single, shared) Favourites panel. It adds bookmarks for the
            // active side's current path and navigates that side.
            LeftFavouritesPanel.CurrentPathProvider = () => GetActiveViewModel().CurrentPath;
            LeftFavouritesPanel.NavigationRequested += (s, path) => GetActiveViewModel().LoadDirectory(path);

            // Wire up parent folder lists with filtered views.
            Loaded += (s, e) =>
            {
                var leftView = CollectionViewSource.GetDefaultView(LeftViewModel.ParentFolders);
                leftView.Filter = obj => FilterParentFolder(obj, LeftParentFoldersSearch.Text);
                LeftParentFoldersList.ItemsSource = leftView;

                var rightView = CollectionViewSource.GetDefaultView(RightViewModel.ParentFolders);
                rightView.Filter = obj => FilterParentFolder(obj, RightParentFoldersSearch.Text);
                RightParentFoldersList.ItemsSource = rightView;

                ApplySplitterPositions();

                RightPaneToggle.IsChecked = _settings.RightPaneVisible;
                SetRightPaneVisible(_settings.RightPaneVisible);
            };

            RestorePersistedState();
        }

        /// <summary>
        /// Restores last-used paths and navigation history for both sides. Falls back to
        /// the user profile when a persisted path no longer exists.
        /// </summary>
        private void RestorePersistedState()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string leftPath = ResolveStartPath(_settings.LeftRootPath, userProfile);
            LeftViewModel.LoadDirectory(leftPath);

            var history = HistoryService.Load();
            LeftViewModel.RestoreBackStack(history.Left);

            // Only activate the right pane if a path was persisted for it.
            if (!string.IsNullOrEmpty(_settings.RightRootPath))
            {
                string rightPath = ResolveStartPath(_settings.RightRootPath, userProfile);
                RightViewModel.LoadDirectory(rightPath);
                RightViewModel.RestoreBackStack(history.Right);
                RightPaneOverlay.Visibility = Visibility.Collapsed;
                _rightPaneActivated = true;
            }
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
            LeftParentFoldersCol.Width = new GridLength(widths[1]);
            LeftContentsCol.Width = new GridLength(widths[2]);
            RightContentsCol.Width = new GridLength(widths[3]);
            RightParentFoldersCol.Width = new GridLength(widths[4]);
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _settings.LeftRootPath = LeftViewModel.CurrentDirectory;
            _settings.RightRootPath = _rightPaneActivated ? RightViewModel.CurrentDirectory : null;
            _settings.RightPaneVisible = RightPaneToggle.IsChecked == true;

            // When the right pane is hidden its columns report zero width; keep the
            // previously persisted positions rather than overwriting them with zeros.
            if (_settings.RightPaneVisible)
            {
                _settings.SplitterPositions = new System.Collections.Generic.List<double>
                {
                    LeftFavouritesCol.ActualWidth,
                    LeftParentFoldersCol.ActualWidth,
                    LeftContentsCol.ActualWidth,
                    RightContentsCol.ActualWidth,
                    RightParentFoldersCol.ActualWidth,
                };
            }
            SettingsService.Save(_settings);

            HistoryService.Save(new NavigationHistory
            {
                Left = LeftViewModel.GetBackStackSnapshot(),
                Right = RightViewModel.GetBackStackSnapshot(),
            });

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

        private GridLength _savedRightPaneWidth = new GridLength(1, GridUnitType.Star);

        private void RightPaneToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Guard against running before the named columns exist (during InitializeComponent).
            if (RightPaneCol == null) return;
            SetRightPaneVisible(RightPaneToggle.IsChecked == true);
        }

        /// <summary>
        /// Shows or hides the right pane (and the centre splitter). When hidden the left
        /// pane expands to fill the freed width; the right pane's width is remembered.
        /// </summary>
        private void SetRightPaneVisible(bool visible)
        {
            if (visible)
            {
                CentreSplitterCol.Width = new GridLength(12);
                RightPaneCol.MinWidth = 300;
                RightPaneCol.Width = _savedRightPaneWidth;
                LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
                CentreSplitter.Visibility = Visibility.Visible;
                RightPaneGrid.Visibility = Visibility.Visible;
            }
            else
            {
                if (RightPaneCol.ActualWidth > 0)
                {
                    _savedRightPaneWidth = new GridLength(RightPaneCol.ActualWidth);
                }
                CentreSplitter.Visibility = Visibility.Collapsed;
                RightPaneGrid.Visibility = Visibility.Collapsed;
                CentreSplitterCol.Width = new GridLength(0);
                RightPaneCol.MinWidth = 0;
                RightPaneCol.Width = new GridLength(0);
                LeftPaneCol.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        private void FoldersItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is FolderItem folderItem)
            {
                if (folderItem.IsEditing) return;

                var listBox = FindAncestor<ListBox>(item);
                if (listBox?.DataContext is DirectoryViewModel vm)
                {
                    if (folderItem.IsDirectory)
                    {
                        vm.LoadDirectory(folderItem.FullPath);
                    }
                }
            }
        }

        private void ContentsItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item && item.DataContext is FolderItem folderItem)
            {
                if (folderItem.IsEditing) return;

                var listView = FindAncestor<ListView>(item);
                if (listView?.DataContext is DirectoryViewModel vm)
                {
                    if (folderItem.IsDirectory)
                    {
                        vm.LoadDirectory(folderItem.FullPath);
                    }
                    else
                    {
                        // Double click a file: launch the process with default application
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folderItem.FullPath)
                            {
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
            }
        }

        // --- Parent folder search -------------------------------------------------
        private static bool FilterParentFolder(object obj, string search)
        {
            if (!(obj is FolderItem f)) return false;
            if (string.IsNullOrEmpty(search)) return true;
            return f.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LeftParentFoldersSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            LeftParentFoldersSearchPlaceholder.Visibility = string.IsNullOrEmpty(LeftParentFoldersSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            CollectionViewSource.GetDefaultView(LeftViewModel.ParentFolders)?.Refresh();
        }

        private void RightParentFoldersSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RightParentFoldersSearchPlaceholder.Visibility = string.IsNullOrEmpty(RightParentFoldersSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            CollectionViewSource.GetDefaultView(RightViewModel.ParentFolders)?.Refresh();
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader header && header.Column != null)
            {
                string sortBy = header.Tag as string;
                if (string.IsNullOrEmpty(sortBy)) return;

                var listView = FindAncestor<ListView>(header);
                if (listView == null) return;

                var dataView = CollectionViewSource.GetDefaultView(listView.ItemsSource);
                if (dataView == null) return;

                ListSortDirection direction = ListSortDirection.Ascending;
                if (dataView.SortDescriptions.Count > 0 && dataView.SortDescriptions[0].PropertyName == sortBy)
                {
                    direction = dataView.SortDescriptions[0].Direction == ListSortDirection.Ascending 
                        ? ListSortDirection.Descending 
                        : ListSortDirection.Ascending;
                }

                dataView.SortDescriptions.Clear();
                dataView.SortDescriptions.Add(new SortDescription(sortBy, direction));
                dataView.Refresh();
            }
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

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F2)
            {
                if (sender is System.Windows.Controls.Primitives.Selector selector && selector.SelectedItem is FolderItem selectedItem)
                {
                    selectedItem.IsEditing = true;
                    e.Handled = true;
                }
            }
        }

        private void NameEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.IsVisible)
            {
                FocusAndSelectTextBox(textBox);
            }
        }

        private void FocusAndSelectTextBox(TextBox textBox)
        {
            textBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (textBox.DataContext is FolderItem folderItem)
                {
                    var selector = FindAncestor<System.Windows.Controls.Primitives.Selector>(textBox);
                    if (selector != null)
                    {
                        if (selector.SelectedItem != folderItem)
                        {
                            return;
                        }
                        if (!selector.IsKeyboardFocusWithin)
                        {
                            return;
                        }
                    }

                    textBox.Focus();
                    Keyboard.Focus(textBox);
                    FocusManager.SetFocusedElement(FocusManager.GetFocusScope(textBox), textBox);

                    string text = textBox.Text;
                    int lastDot = text.LastIndexOf('.');
                    if (lastDot > 0 && !folderItem.IsDirectory)
                    {
                        textBox.Select(0, lastDot);
                    }
                    else
                    {
                        textBox.SelectAll();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NameEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is FolderItem folderItem)
            {
                if (e.Key == Key.Enter)
                {
                    CommitRename(textBox, folderItem);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CancelRename(textBox, folderItem);
                    e.Handled = true;
                }
            }
        }

        private void NameEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is FolderItem folderItem)
            {
                CommitRename(textBox, folderItem);
            }
        }

        private void CommitRename(TextBox textBox, FolderItem folderItem)
        {
            if (!folderItem.IsEditing) return;

            folderItem.IsEditing = false;

            string newName = textBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == folderItem.Name)
            {
                return;
            }

            string parentDir = System.IO.Path.GetDirectoryName(folderItem.FullPath);
            if (parentDir == null) return;

            string newPath = System.IO.Path.Combine(parentDir, newName);

            try
            {
                if (folderItem.IsDirectory)
                {
                    if (System.IO.Directory.Exists(newPath))
                    {
                        MessageBox.Show("A folder with that name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    System.IO.Directory.Move(folderItem.FullPath, newPath);
                }
                else
                {
                    if (System.IO.File.Exists(newPath))
                    {
                        MessageBox.Show("A file with that name already exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    System.IO.File.Move(folderItem.FullPath, newPath);
                }

                // Update the bound item in place so the UI reflects the new name
                // immediately, without depending on a full directory reload.
                folderItem.FullPath = newPath;
                folderItem.Name = newName;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rename item: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                var selector = FindAncestor<System.Windows.Controls.Primitives.Selector>(textBox);
                if (selector?.DataContext is DirectoryViewModel vm)
                {
                    vm.LoadDirectory(vm.CurrentPath);
                }
            }
        }

        private void CancelRename(TextBox textBox, FolderItem folderItem)
        {
            if (!folderItem.IsEditing) return;
            folderItem.IsEditing = false;
            textBox.Text = folderItem.Name;
        }

        private void AddressBar_NavigationRequested(object sender, string targetPath)
        {
            if (sender is AddressBar bar && bar.DataContext is DirectoryViewModel vm)
            {
                vm.LoadDirectory(targetPath);
            }
        }

        private void AddressBar_GoBackRequested(object sender, EventArgs e)
        {
            if (sender is AddressBar bar && bar.DataContext is DirectoryViewModel vm)
            {
                vm.GoBack();
            }
        }

        private void AddressBar_GoForwardRequested(object sender, EventArgs e)
        {
            if (sender is AddressBar bar && bar.DataContext is DirectoryViewModel vm)
            {
                vm.GoForward();
            }
        }

        private void ParentBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DirectoryViewModel vm)
            {
                if (vm.CanGoToParent)
                {
                    vm.GoToParent();
                }
            }
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);

            if (e.ChangedButton == MouseButton.XButton1)
            {
                if (LeftParentFoldersPanel != null && LeftParentFoldersPanel.IsMouseOver)
                {
                    if (LeftViewModel.CanGoToParent)
                    {
                        LeftViewModel.GoToParent();
                    }
                    e.Handled = true;
                }
                else if (RightParentFoldersPanel != null && RightParentFoldersPanel.IsMouseOver)
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

        // --- Drag source: folders/files → Favourites panels ----------------------
        private Point _itemDragStart;
        private FolderItem _itemDragCandidate;

        private void ItemList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _itemDragStart = e.GetPosition(null);
            var src = e.OriginalSource as DependencyObject;
            while (src != null && !(src is ListBoxItem) && !(src is ListViewItem))
                src = VisualTreeHelper.GetParent(src);
            _itemDragCandidate = (src as FrameworkElement)?.DataContext as FolderItem;
        }

        private void ItemList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _itemDragCandidate == null) return;
            if (_itemDragCandidate.IsEditing) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _itemDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _itemDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var data = new DataObject();
            data.SetData(FavouritesPanel.FolderItemDragFormat, _itemDragCandidate);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            _itemDragCandidate = null;
        }

        // --- Right-click context menu (Folders / Contents / Parent panels) --------
        private static FolderItem ItemFromMenu(object sender)
        {
            // The ContextMenu inherits the row's DataContext (a FolderItem).
            if (sender is MenuItem mi) return mi.DataContext as FolderItem;
            return null;
        }

        /// <summary>Resolve which side a context menu was raised on via its placement target.</summary>
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
            // Find the "Add/Remove Favourites" item and update its label per side state.
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
                // Bookmarks are global; there is a single shared Favourites panel.
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

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLeftPaneLayout();
            UpdateRightPaneLayout();
        }

        private void LeftViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectoryViewModel.CurrentPath))
            {
                UpdateLeftPaneLayout();
                LeftFavouritesPanel.SyncSelectionToPath(GetActiveViewModel().CurrentPath);
            }
        }

        private void RightViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectoryViewModel.CurrentPath))
            {
                UpdateRightPaneLayout();
                LeftFavouritesPanel.SyncSelectionToPath(GetActiveViewModel().CurrentPath);
            }
        }

        private void UpdateLeftPaneLayout()
        {
            if (LeftFoldersRow == null || LeftFoldersListView == null || LeftGridSplitter == null || LeftSplitterRow == null || LeftFilesRow == null || LeftFilesListView == null)
                return;

            int folderCount = LeftViewModel.Folders.Count;
            int fileCount = LeftViewModel.Contents.Count;

            // Rule 1: hide If there are no subfolders.
            if (folderCount == 0)
            {
                LeftFoldersRow.Height = new GridLength(0);
                LeftFoldersRow.MinHeight = 0;
                LeftFoldersRow.MaxHeight = 0;
                
                LeftSplitterRow.Height = new GridLength(0);
                
                LeftFilesRow.Height = new GridLength(1, GridUnitType.Star);
                LeftFilesRow.MinHeight = 0;

                LeftFoldersListView.Visibility = Visibility.Collapsed;
                LeftGridSplitter.Visibility = Visibility.Collapsed;
                LeftFilesListView.Visibility = Visibility.Visible;
                return;
            }

            // Rule 4: If no files, then the sub panel shall expand fully and the files subpanel shall hide.
            if (fileCount == 0)
            {
                LeftFoldersRow.Height = new GridLength(1, GridUnitType.Star);
                LeftFoldersRow.MinHeight = 0;
                LeftFoldersRow.MaxHeight = double.PositiveInfinity;
                
                LeftSplitterRow.Height = new GridLength(0);
                
                LeftFilesRow.Height = new GridLength(0);
                LeftFilesRow.MinHeight = 0;

                LeftFoldersListView.Visibility = Visibility.Visible;
                LeftGridSplitter.Visibility = Visibility.Collapsed;
                LeftFilesListView.Visibility = Visibility.Collapsed;
                return;
            }

            // Both folders and files exist.
            // Estimate layout heights (headers ~36px, each item ~28px, splitter 6px).
            double estimatedFoldersHeight = 36 + folderCount * 28;
            double estimatedFilesHeight = 36 + fileCount * 28;
            double splitterHeight = 6;

            FrameworkElement parentGrid = LeftFoldersListView.Parent as FrameworkElement;
            double availableHeight = parentGrid != null && parentGrid.ActualHeight > 0 
                ? parentGrid.ActualHeight 
                : this.Height - 150; // Fallback if not yet rendered

            if (estimatedFoldersHeight + estimatedFilesHeight + splitterHeight > availableHeight)
            {
                // Rule 5: If there are too many of both folders and files to show without scrolling then the subpanels shall be equal in height.
                LeftFoldersRow.Height = new GridLength(1, GridUnitType.Star);
                LeftFoldersRow.MinHeight = 0;
                LeftFoldersRow.MaxHeight = double.PositiveInfinity;

                LeftFilesRow.Height = new GridLength(1, GridUnitType.Star);
                LeftFilesRow.MinHeight = 0;

                LeftSplitterRow.Height = new GridLength(splitterHeight);

                LeftFoldersListView.Visibility = Visibility.Visible;
                LeftGridSplitter.Visibility = Visibility.Visible;
                LeftFilesListView.Visibility = Visibility.Visible;
            }
            else
            {
                // Rules 2 & 3:
                // - If one subfolder, shorten height of panel to fit exactly.
                // - If more than one subfolder, the height of subpanel shall autofit to show all folders.
                LeftFoldersRow.Height = GridLength.Auto;
                LeftFoldersRow.MinHeight = 0;
                LeftFoldersRow.MaxHeight = double.PositiveInfinity;

                LeftFilesRow.Height = new GridLength(1, GridUnitType.Star);
                LeftFilesRow.MinHeight = 0;

                LeftSplitterRow.Height = new GridLength(splitterHeight);

                LeftFoldersListView.Visibility = Visibility.Visible;
                LeftGridSplitter.Visibility = Visibility.Visible;
                LeftFilesListView.Visibility = Visibility.Visible;
            }
        }

        private void UpdateRightPaneLayout()
        {
            if (RightFoldersRow == null || RightFoldersListView == null || RightGridSplitter == null || RightSplitterRow == null || RightFilesRow == null || RightFilesListView == null)
                return;

            int folderCount = RightViewModel.Folders.Count;
            int fileCount = RightViewModel.Contents.Count;

            // Rule 1: hide If there are no subfolders.
            if (folderCount == 0)
            {
                RightFoldersRow.Height = new GridLength(0);
                RightFoldersRow.MinHeight = 0;
                RightFoldersRow.MaxHeight = 0;
                
                RightSplitterRow.Height = new GridLength(0);
                
                RightFilesRow.Height = new GridLength(1, GridUnitType.Star);
                RightFilesRow.MinHeight = 0;

                RightFoldersListView.Visibility = Visibility.Collapsed;
                RightGridSplitter.Visibility = Visibility.Collapsed;
                RightFilesListView.Visibility = Visibility.Visible;
                return;
            }

            // Rule 4: If no files, then the sub panel shall expand fully and the files subpanel shall hide.
            if (fileCount == 0)
            {
                RightFoldersRow.Height = new GridLength(1, GridUnitType.Star);
                RightFoldersRow.MinHeight = 0;
                RightFoldersRow.MaxHeight = double.PositiveInfinity;
                
                RightSplitterRow.Height = new GridLength(0);
                
                RightFilesRow.Height = new GridLength(0);
                RightFilesRow.MinHeight = 0;

                RightFoldersListView.Visibility = Visibility.Visible;
                RightGridSplitter.Visibility = Visibility.Collapsed;
                RightFilesListView.Visibility = Visibility.Collapsed;
                return;
            }

            // Both folders and files exist.
            // Estimate layout heights (headers ~36px, each item ~28px, splitter 6px).
            double estimatedFoldersHeight = 36 + folderCount * 28;
            double estimatedFilesHeight = 36 + fileCount * 28;
            double splitterHeight = 6;

            FrameworkElement parentGrid = RightFoldersListView.Parent as FrameworkElement;
            double availableHeight = parentGrid != null && parentGrid.ActualHeight > 0 
                ? parentGrid.ActualHeight 
                : this.Height - 150; // Fallback if not yet rendered

            if (estimatedFoldersHeight + estimatedFilesHeight + splitterHeight > availableHeight)
            {
                // Rule 5: If there are too many of both folders and files to show without scrolling then the subpanels shall be equal in height.
                RightFoldersRow.Height = new GridLength(1, GridUnitType.Star);
                RightFoldersRow.MinHeight = 0;
                RightFoldersRow.MaxHeight = double.PositiveInfinity;

                RightFilesRow.Height = new GridLength(1, GridUnitType.Star);
                RightFilesRow.MinHeight = 0;

                RightSplitterRow.Height = new GridLength(splitterHeight);

                RightFoldersListView.Visibility = Visibility.Visible;
                RightGridSplitter.Visibility = Visibility.Visible;
                RightFilesListView.Visibility = Visibility.Visible;
            }
            else
            {
                // Rules 2 & 3:
                // - If one subfolder, shorten height of panel to fit exactly.
                // - If more than one subfolder, the height of subpanel shall autofit to show all folders.
                RightFoldersRow.Height = GridLength.Auto;
                RightFoldersRow.MinHeight = 0;
                RightFoldersRow.MaxHeight = double.PositiveInfinity;

                RightFilesRow.Height = new GridLength(1, GridUnitType.Star);
                RightFilesRow.MinHeight = 0;

                RightSplitterRow.Height = new GridLength(splitterHeight);

                RightFoldersListView.Visibility = Visibility.Visible;
                RightGridSplitter.Visibility = Visibility.Visible;
                RightFilesListView.Visibility = Visibility.Visible;
            }
        }
    }
}
