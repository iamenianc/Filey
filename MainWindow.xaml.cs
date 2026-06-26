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
    }
}
