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
            RightViewModel.LoadDirectory(startingPath);
        }

        private void Folders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox listBox && listBox.SelectedItem is FolderItem folderItem)
            {
                if (listBox.DataContext is DirectoryViewModel vm)
                {
                    // Temporarily set SelectedItem to null to avoid re-entry loops during collection clear/reload
                    listBox.SelectedItem = null;
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
    }
}
