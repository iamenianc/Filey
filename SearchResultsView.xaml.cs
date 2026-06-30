using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Filey
{
    /// <summary>
    /// Dedicated, information-dense pane that lists the full ranked result set for a search
    /// query the user committed with Enter (without picking a specific hit). Rows carry every
    /// detail column and default to search-rank order; column headers re-sort. Activating a row
    /// raises <see cref="ResultActivated"/>; the host reveals the item and closes the pane.
    /// </summary>
    public partial class SearchResultsView : UserControl
    {
        /// <summary>Raised when the user opens a result (Enter / double-click).</summary>
        public event EventHandler<FolderItem> ResultActivated;

        /// <summary>Raised when the user dismisses the pane (Esc / close button).</summary>
        public event EventHandler CloseRequested;

        private string _query;
        private string _sortColumn;
        private ListSortDirection _sortDirection;

        public SearchResultsView()
        {
            InitializeComponent();
        }

        /// <summary>Shows a spinner-free "searching" state while results are fetched.</summary>
        public void ShowSearching(string query)
        {
            _query = query;
            QueryRun.Text = query;
            CountRun.Text = "searching…";
            ResultsListView.ItemsSource = null;
            ResultsListView.Visibility = Visibility.Collapsed;
            StatusText.Text = "Searching…";
            StatusText.Visibility = Visibility.Visible;
        }

        /// <summary>Populates the pane with the ranked results and focuses the first row.</summary>
        public void ShowResults(string query, IReadOnlyList<FolderItem> results)
        {
            _query = query;
            QueryRun.Text = query;

            int n = results?.Count ?? 0;
            CountRun.Text = n == 1 ? "1 result" : $"{n} results";

            // Assign 1-based rank and resolve icons before the rows render.
            if (results != null)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    var item = results[i];
                    item.Rank = i + 1;
                    if (item.Icon == null)
                        item.Icon = item.IsDirectory
                            ? IconHelper.GetFolderIcon()
                            : IconHelper.GetFileIcon(item.FullPath);
                }
            }

            // A fresh result set resets to rank order.
            _sortColumn = null;
            _sortDirection = ListSortDirection.Ascending;

            ResultsListView.ItemsSource = results;
            if (n == 0)
            {
                ResultsListView.Visibility = Visibility.Collapsed;
                StatusText.Text = "No matches.";
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
            ResultsListView.Visibility = Visibility.Visible;

            ResultsListView.SelectedIndex = 0;
            ResultsListView.Dispatcher.BeginInvoke(new Action(() =>
            {
                ResultsListView.Focus();
                (ResultsListView.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader header) || header.Column == null) return;
            string sortBy = header.Tag as string;
            if (string.IsNullOrEmpty(sortBy)) return;

            var view = CollectionViewSource.GetDefaultView(ResultsListView.ItemsSource);
            if (view == null) return;

            // Toggle direction when re-clicking the active column.
            _sortDirection = (sortBy == _sortColumn && _sortDirection == ListSortDirection.Ascending)
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _sortColumn = sortBy;

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(SortPropertyFor(sortBy), _sortDirection));
            view.Refresh();
        }

        /// <summary>Maps a column tag to the FolderItem property the view should sort on.</summary>
        private static string SortPropertyFor(string tag)
        {
            switch (tag)
            {
                case "Name": return nameof(FolderItem.Name);
                case "ParentFolder": return nameof(FolderItem.ParentFolder);
                case "Type": return nameof(FolderItem.Type);
                case "DateModified": return nameof(FolderItem.DateModified);
                case "Size": return nameof(FolderItem.Size);
                default: return nameof(FolderItem.Rank);
            }
        }

        private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ActivateSelected();
        }

        private void ResultsListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ActivateSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ActivateSelected()
        {
            if (ResultsListView.SelectedItem is FolderItem item)
                ResultActivated?.Invoke(this, item);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
