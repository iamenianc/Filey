using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Filey
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public DirectoryViewModel LeftViewModel { get; }
        public DirectoryViewModel RightViewModel { get; }

        public MainWindow()
        {
            LeftViewModel = new DirectoryViewModel();
            RightViewModel = new DirectoryViewModel();

            InitializeComponent();

            // Subscribe to ViewModel property changes to dynamically adjust folder pane heights
            LeftViewModel.PropertyChanged += LeftViewModel_PropertyChanged;
            RightViewModel.PropertyChanged += RightViewModel_PropertyChanged;
            this.SizeChanged += Window_SizeChanged;

            // Set up initial directories (UserProfile directory as specified in starting state)
            string startingPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            LeftViewModel.LoadDirectory(startingPath);
        }

        private void RightPaneOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (RightPaneOverlay.Visibility == Visibility.Visible)
            {
                string startingPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                RightViewModel.LoadDirectory(startingPath);
                RightPaneOverlay.Visibility = Visibility.Collapsed;
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

                var selector = FindAncestor<System.Windows.Controls.Primitives.Selector>(textBox);
                if (selector?.DataContext is DirectoryViewModel vm)
                {
                    vm.LoadDirectory(vm.CurrentPath);
                }
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

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);

            if (e.ChangedButton == MouseButton.XButton1)
            {
                var activeVm = GetActiveViewModel();
                if (activeVm != null && activeVm.CanGoBack)
                {
                    activeVm.GoBack();
                    e.Handled = true;
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
            }
        }

        private void RightViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DirectoryViewModel.CurrentPath))
            {
                UpdateRightPaneLayout();
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
