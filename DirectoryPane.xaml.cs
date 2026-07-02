using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Filey
{
    /// <summary>
    /// Interaction logic for DirectoryPane.xaml
    /// </summary>
    public partial class DirectoryPane : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register("ViewModel", typeof(DirectoryViewModel), typeof(DirectoryPane),
                new PropertyMetadata(null, OnViewModelChanged));

        public DirectoryViewModel ViewModel
        {
            get => (DirectoryViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public static readonly DependencyProperty ShowAddressBarProperty =
            DependencyProperty.Register("ShowAddressBar", typeof(bool), typeof(DirectoryPane),
                new PropertyMetadata(true));

        public bool ShowAddressBar
        {
            get => (bool)GetValue(ShowAddressBarProperty);
            set => SetValue(ShowAddressBarProperty, value);
        }

        public static readonly DependencyProperty SideProperty =
            DependencyProperty.Register("Side", typeof(Side), typeof(DirectoryPane),
                new PropertyMetadata(Side.Left, OnSideChanged));

        private static void OnSideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DirectoryPane pane)
            {
                pane.ApplySideLayout();
            }
        }

        public Side Side
        {
            get => (Side)GetValue(SideProperty);
            set => SetValue(SideProperty, value);
        }

        public static readonly DependencyProperty EstimatedRowHeightProperty =
            DependencyProperty.Register("EstimatedRowHeight", typeof(double), typeof(DirectoryPane),
                new PropertyMetadata(22.0, OnEstimatedRowHeightChanged));

        public double EstimatedRowHeight
        {
            get => (double)GetValue(EstimatedRowHeightProperty);
            set => SetValue(EstimatedRowHeightProperty, value);
        }

        // The two column definitions are fixed in the grid (col 0 and col 2), but
        // which panel each one hosts flips with Side. Resolve by role so the
        // cross-pane width sync stays correct on both sides.
        private ColumnDefinition ParentFoldersColumn =>
            Side == Side.Right ? ContentsCol : ParentFoldersCol;

        private ColumnDefinition ContentsColumn =>
            Side == Side.Right ? ParentFoldersCol : ContentsCol;

        public GridLength ParentFoldersWidth
        {
            get => ParentFoldersColumn.Width;
            set => ParentFoldersColumn.Width = value;
        }

        public double ParentFoldersActualWidth => ParentFoldersColumn.ActualWidth;

        public GridLength ContentsWidth
        {
            get => ContentsColumn.Width;
            set => ContentsColumn.Width = value;
        }

        public double ContentsActualWidth => ContentsColumn.ActualWidth;

        public bool IsMouseOverParentFolders => ParentFoldersPanel != null && ParentFoldersPanel.IsMouseOver;

        public event EventHandler HomeRequested;
        public event EventHandler SetHomeRequested;
        public event EventHandler<string> DirectoryContentsChanged;

        private GridViewColumn _colFolderName;
        private GridViewColumn _colFolderDateModified;
        private GridViewColumn _colFolderDateCreated;
        private GridViewColumn _colFolderSize;
        private GridViewColumn _colFolderType;

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DirectoryPane pane)
            {
                if (e.OldValue is DirectoryViewModel oldVm)
                {
                    oldVm.PropertyChanged -= pane.ViewModel_PropertyChanged;
                }
                if (e.NewValue is DirectoryViewModel newVm)
                {
                    newVm.PropertyChanged += pane.ViewModel_PropertyChanged;
                }
                pane.UpdatePaneLayout();
                pane.UpdateDirectoryColumns();
            }
        }

        private static void OnEstimatedRowHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DirectoryPane pane)
            {
                pane.UpdatePaneLayout();
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectoryViewModel.CurrentPath))
            {
                UpdatePaneLayout();
                UpdateDirectoryColumns();
            }
        }

        public DirectoryPane()
        {
            InitializeComponent();
            this.SizeChanged += (s, e) => UpdatePaneLayout();
            this.Loaded += (s, e) => {
                ApplySideLayout();
                UpdatePaneLayout();
                UpdateDirectoryColumns();
            };
        }

        private void UpdateDirectoryColumns()
        {
            if (ViewModel == null || FoldersListView == null || !(FoldersListView.View is GridView gridView))
                return;

            // Ensure columns are initialized from XAML
            if (_colFolderName == null)
            {
                if (gridView.Columns.Count >= 5)
                {
                    _colFolderName = gridView.Columns[0];
                    _colFolderDateModified = gridView.Columns[1];
                    _colFolderDateCreated = gridView.Columns[2];
                    _colFolderSize = gridView.Columns[3];
                    _colFolderType = gridView.Columns[4];
                }
                else
                {
                    return;
                }
            }

            // If there are zero files, then all contents loaded in this directory pane are folders.
            bool allFolders = (ViewModel.Contents == null || ViewModel.Contents.Count == 0);

            gridView.Columns.Clear();
            gridView.Columns.Add(_colFolderName);
            gridView.Columns.Add(_colFolderDateModified);

            if (allFolders)
            {
                gridView.Columns.Add(_colFolderDateCreated);
            }
            else
            {
                gridView.Columns.Add(_colFolderSize);
                gridView.Columns.Add(_colFolderType);
            }
        }

        // Mirror the layout for the right pane: parent-folders list sits on the
        // outer edge (rightmost), contents on the inner edge (leftmost).
        private void ApplySideLayout()
        {
            if (ParentFoldersPanel == null || ContentsPanel == null ||
                ParentFoldersCol == null || ContentsCol == null)
                return;

            // Column 0's definition is ParentFoldersCol; column 2's is ContentsCol.
            // The definitions stay put; the panels and their widths move so the
            // contents (star-sized) panel sits on the inner edge.
            if (Side == Side.Right)
            {
                Grid.SetColumn(ContentsPanel, 0);
                Grid.SetColumn(ParentFoldersPanel, 2);
                ParentFoldersCol.Width = new GridLength(2, GridUnitType.Star);
                ParentFoldersCol.MinWidth = 150;
                ContentsCol.Width = new GridLength(150);
                ContentsCol.MinWidth = 100;
            }
            else
            {
                Grid.SetColumn(ParentFoldersPanel, 0);
                Grid.SetColumn(ContentsPanel, 2);
                ParentFoldersCol.Width = new GridLength(150);
                ParentFoldersCol.MinWidth = 100;
                ContentsCol.Width = new GridLength(2, GridUnitType.Star);
                ContentsCol.MinWidth = 150;
            }
        }

        public void UpdatePaneLayout()
        {
            if (FoldersRow == null || FoldersListView == null || GridSplitter == null || SplitterRow == null || FilesRow == null || FilesListView == null)
                return;

            if (ViewModel == null) return;

            int folderCount = ViewModel.Folders.Count;
            int fileCount = ViewModel.Contents.Count;

            // Rule 1: hide If there are no subfolders.
            if (folderCount == 0)
            {
                FoldersRow.Height = new GridLength(0);
                FoldersRow.MinHeight = 0;
                FoldersRow.MaxHeight = 0;
                
                SplitterRow.Height = new GridLength(0);
                
                FilesRow.Height = new GridLength(1, GridUnitType.Star);
                FilesRow.MinHeight = 0;

                FoldersListView.Visibility = Visibility.Collapsed;
                GridSplitter.Visibility = Visibility.Collapsed;
                FilesListView.Visibility = Visibility.Visible;
                return;
            }

            // Rule 4: If no files, then the sub panel shall expand fully and the files subpanel shall hide.
            if (fileCount == 0)
            {
                FoldersRow.Height = new GridLength(1, GridUnitType.Star);
                FoldersRow.MinHeight = 0;
                FoldersRow.MaxHeight = double.PositiveInfinity;
                
                SplitterRow.Height = new GridLength(0);
                
                FilesRow.Height = new GridLength(0);
                FilesRow.MinHeight = 0;

                FoldersListView.Visibility = Visibility.Visible;
                GridSplitter.Visibility = Visibility.Collapsed;
                FilesListView.Visibility = Visibility.Collapsed;
                return;
            }

            // Both folders and files exist.
            double estimatedFoldersHeight = 36 + folderCount * EstimatedRowHeight;
            double splitterHeight = 6;

            FrameworkElement parentGrid = FoldersListView.Parent as FrameworkElement;
            double availableHeight = parentGrid != null && parentGrid.ActualHeight > 0 
                ? parentGrid.ActualHeight 
                : 300; // Fallback

            // The folders panel auto-fits to its folders (shrinking when few) and
            // files take the rest. Only fall back to an equal split when the
            // folders alone would consume more than half the available height —
            // i.e. there are genuinely too many folders, not merely many files.
            bool foldersFitInHalf = estimatedFoldersHeight + splitterHeight <= availableHeight / 2;

            if (!foldersFitInHalf)
            {
                // Rule 5: If there are too many of both folders and files to show without scrolling then the subpanels shall be equal in height.
                FoldersRow.Height = new GridLength(1, GridUnitType.Star);
                FoldersRow.MinHeight = 0;
                FoldersRow.MaxHeight = double.PositiveInfinity;

                FilesRow.Height = new GridLength(1, GridUnitType.Star);
                FilesRow.MinHeight = 0;

                SplitterRow.Height = new GridLength(splitterHeight);

                FoldersListView.Visibility = Visibility.Visible;
                GridSplitter.Visibility = Visibility.Visible;
                FilesListView.Visibility = Visibility.Visible;
            }
            else
            {
                // Rules 2 & 3:
                // - If one subfolder, shorten height of panel to fit exactly.
                // - If more than one subfolder, the height of subpanel shall autofit to show all folders.
                FoldersRow.Height = GridLength.Auto;
                FoldersRow.MinHeight = 0;
                FoldersRow.MaxHeight = double.PositiveInfinity;

                FilesRow.Height = new GridLength(1, GridUnitType.Star);
                FilesRow.MinHeight = 0;

                SplitterRow.Height = new GridLength(splitterHeight);

                FoldersListView.Visibility = Visibility.Visible;
                GridSplitter.Visibility = Visibility.Visible;
                FilesListView.Visibility = Visibility.Visible;
            }
        }

        private string _parentFoldersTypeAheadBuffer = string.Empty;
        private DispatcherTimer _parentFoldersTypeAheadResetTimer;

        private void ParentFoldersList_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text) || ViewModel == null) return;

            if (_parentFoldersTypeAheadResetTimer == null)
            {
                _parentFoldersTypeAheadResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _parentFoldersTypeAheadResetTimer.Tick += (s, args) =>
                {
                    _parentFoldersTypeAheadBuffer = string.Empty;
                    _parentFoldersTypeAheadResetTimer.Stop();
                };
            }
            _parentFoldersTypeAheadResetTimer.Stop();
            _parentFoldersTypeAheadResetTimer.Start();

            _parentFoldersTypeAheadBuffer += e.Text;

            var match = ViewModel.ParentFolders.FirstOrDefault(f =>
                f.Name.StartsWith(_parentFoldersTypeAheadBuffer, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                SmoothScrollHelper.ScrollItemToTop(ParentFoldersList, match);
            }

            e.Handled = true;
        }

        private void ContentsPanel_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void ContentsPanel_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || ViewModel == null) return;

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0) return;

            if (ContextActions.TryResolveDropNavigationTarget(paths[0], out string targetDir, out string selectPath))
            {
                ViewModel.LoadDirectory(targetDir);
                if (!string.IsNullOrEmpty(selectPath))
                {
                    var match = ViewModel.Contents.FirstOrDefault(f =>
                        string.Equals(f.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        ViewModel.SelectedItem = match;
                    }
                }
            }
        }

        private void ParentBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.CanGoToParent)
            {
                ViewModel.GoToParent();
            }
        }

        private void AddressBar_NavigationRequested(object sender, string targetPath)
        {
            ViewModel?.LoadDirectory(targetPath);
        }

        private void AddressBar_GoBackRequested(object sender, EventArgs e)
        {
            ViewModel?.GoBack();
        }

        private void AddressBar_GoForwardRequested(object sender, EventArgs e)
        {
            ViewModel?.GoForward();
        }

        private void AddressBar_HomeRequested(object sender, EventArgs e)
        {
            HomeRequested?.Invoke(this, e);
        }

        private void AddressBar_SetHomeRequested(object sender, EventArgs e)
        {
            SetHomeRequested?.Invoke(this, e);
        }

        private void FoldersItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.DataContext is FolderItem folderItem)
            {
                if (folderItem.IsEditing) return;

                if (ViewModel != null && folderItem.IsDirectory)
                {
                    ViewModel.LoadDirectory(folderItem.FullPath);
                }
            }
        }

        private void ContentsItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item && item.DataContext is FolderItem folderItem)
            {
                if (folderItem.IsEditing) return;

                if (ViewModel != null)
                {
                    if (folderItem.IsDirectory)
                    {
                        ViewModel.LoadDirectory(folderItem.FullPath);
                    }
                    else
                    {
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

        /// <summary>
        /// Selects the item at <paramref name="fullPath"/> in the appropriate list and scrolls
        /// it into view, so a search hit visibly lands on the item without launching it. The
        /// caller is expected to have already navigated this pane to the containing folder.
        /// </summary>
        public void RevealItem(string fullPath, bool isDirectory)
        {
            if (string.IsNullOrEmpty(fullPath)) return;

            var list = isDirectory ? FoldersListView : FilesListView;
            if (list == null) return;

            foreach (var obj in list.Items)
            {
                if (obj is FolderItem fi && string.Equals(fi.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (ViewModel != null) ViewModel.SelectedItem = fi;
                    list.SelectedItem = fi;
                    list.ScrollIntoView(fi);

                    list.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        (list.ItemContainerGenerator.ContainerFromItem(fi) as UIElement)?.Focus();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    break;
                }
            }
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

                UpdateSortIndicators(listView, header, direction);
            }
        }

        private void UpdateSortIndicators(ListView listView, GridViewColumnHeader activeHeader, ListSortDirection direction)
        {
            if (listView.View is GridView gridView)
            {
                foreach (var column in gridView.Columns)
                {
                    if (column.Header is GridViewColumnHeader header)
                    {
                        string text = header.Content as string;
                        if (text != null)
                        {
                            text = text.Replace(" ▲", "").Replace(" ▼", "").Trim();
                            if (header == activeHeader)
                            {
                                header.Content = text + (direction == ListSortDirection.Ascending ? " ▲" : " ▼");
                            }
                            else
                            {
                                header.Content = text;
                            }
                        }
                    }
                }
            }
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

                folderItem.FullPath = newPath;
                folderItem.Name = newName;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to rename item: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                if (ViewModel != null)
                {
                    ViewModel.LoadDirectory(ViewModel.CurrentPath);
                }
            }
        }

        private void CancelRename(TextBox textBox, FolderItem folderItem)
        {
            if (!folderItem.IsEditing) return;
            folderItem.IsEditing = false;
            textBox.Text = folderItem.Name;
        }

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

        private static FolderItem ItemFromMenu(object sender)
        {
            if (sender is MenuItem mi) return mi.DataContext as FolderItem;
            return null;
        }

        /// <summary>
        /// Returns the full multi-selection for the list the context menu was opened on (currently
        /// only <see cref="FilesListView"/> supports multi-select), falling back to the single
        /// clicked item for everything else (e.g. FoldersListView).
        /// </summary>
        private List<FolderItem> SelectedFileItemsFromMenu(object sender)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu menu
                && menu.PlacementTarget is DependencyObject placementTarget)
            {
                var listView = FindAncestor<ListView>(placementTarget);
                if (ReferenceEquals(listView, FilesListView))
                {
                    return FilesListView.SelectedItems.Cast<FolderItem>().ToList();
                }
            }

            var single = ItemFromMenu(sender);
            return single != null ? new List<FolderItem> { single } : new List<FolderItem>();
        }

        private static readonly string[] TextExtensions = new[]
        {
            ".txt", ".ini", ".sql", ".cs", ".json", ".md", ".xml", ".log", ".py",
            ".js", ".ts", ".html", ".css", ".xaml", ".csproj", ".sln", ".slnx",
            ".bat", ".cmd", ".sh", ".yaml", ".yml", ".config"
        };

        private static readonly string[] ImageExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff"
        };

        private bool IsSupportedPreviewFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path);
            return Array.Exists(TextExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                || Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                || string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] ExcelExtensions = new[] { ".xlsx", ".xls", ".xlsm", ".xlsb" };

        private static bool IsExcelFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path);
            return Array.Exists(ExcelExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is ContextMenu menu)) return;
            var item = menu.DataContext as FolderItem;
            foreach (var obj in menu.Items)
            {
                if (obj is MenuItem mi)
                {
                    if (string.Equals(mi.Name, "CtxFavouriteItem")
                        || (mi.Header as string)?.Contains("Favourites") == true)
                    {
                        bool exists = item != null && BookmarkStore.Instance.Contains(Side, item.FullPath);
                        mi.Header = exists ? "Remove from Favourites" : "Add to Favourites";
                    }
                    else if (string.Equals(mi.Name, "CtxPreviewItem"))
                    {
                        if (item != null && !item.IsDirectory && IsSupportedPreviewFile(item.FullPath))
                        {
                            mi.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            mi.Visibility = Visibility.Collapsed;
                        }
                    }
                    else if (string.Equals(mi.Name, "CtxRemoveExcelPasswordItem"))
                    {
                        var selectedItems = SelectedFileItemsFromMenu(mi);
                        bool allExcel = selectedItems.Count > 0
                            && selectedItems.All(i => !i.IsDirectory && IsExcelFile(i.FullPath));
                        mi.Visibility = allExcel ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }

        private void CtxPreview_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item != null && !item.IsDirectory && IsSupportedPreviewFile(item.FullPath))
            {
                var previewWindow = new PreviewWindow(item.FullPath)
                {
                    Owner = Application.Current.MainWindow
                };
                previewWindow.Show();
            }
        }

        private void CtxOpenRightPane_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemFromMenu(sender);
            if (item != null)
            {
                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.OpenPathInRightPane(item.FullPath);
                }
            }
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

            var existing = BookmarkStore.Instance.Find(Side, item.FullPath);
            if (existing != null)
            {
                BookmarkStore.Instance.Remove(Side, existing);
            }
            else
            {
                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.LeftFavouritesPanel.AddFavourite(item.FullPath);
                }
            }
        }

        private async void CtxRemoveExcelPassword_Click(object sender, RoutedEventArgs e)
        {
            var excelItems = SelectedFileItemsFromMenu(sender)
                .Where(i => !i.IsDirectory && IsExcelFile(i.FullPath))
                .ToList();
            if (excelItems.Count == 0) return;

            string prompt = excelItems.Count == 1
                ? $"Enter the password for \"{excelItems[0].Name}\":"
                : $"Enter the password shared by all {excelItems.Count} selected files:";

            var dialog = new PasswordPromptDialog("Remove Excel Password", prompt)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() != true) return;

            List<ExcelPasswordResult> results;
            // Excel COM startup takes a few seconds; show a wait cursor so the app does not appear
            // frozen while the background operation runs.
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                results = await ContextActions.RemoveExcelPasswordAsync(
                    excelItems.Select(i => i.FullPath), dialog.Password);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not run the password removal script: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            int successCount = results.Count(r => r.Success);
            int failCount = results.Count - successCount;

            if (failCount == 0)
            {
                MessageBox.Show(
                    successCount == 1
                        ? "Password removed. A new copy was created."
                        : $"Password removed from all {successCount} files. New copies were created.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var lines = results
                    .Where(r => !r.Success)
                    .Select(r => $"- {System.IO.Path.GetFileName(r.Path)}: {r.Error}");
                string summary = $"{successCount} of {results.Count} succeeded.\n\nFailed:\n" + string.Join("\n", lines);
                MessageBox.Show(summary, "Some files failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (successCount > 0 && ViewModel != null)
            {
                ViewModel.LoadDirectory(ViewModel.CurrentPath);
                DirectoryContentsChanged?.Invoke(this, ViewModel.CurrentDirectory);
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
    }
}
