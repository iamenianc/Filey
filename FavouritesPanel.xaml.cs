using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Filey
{
    public partial class FavouritesPanel : UserControl
    {
        public const string BookmarkDragFormat = "Filey.Bookmark";
        public const string FolderItemDragFormat = "Filey.FolderItem";

        private ICollectionView _view;
        private Point _dragStart;
        private Bookmark _dragCandidate;
        private bool _dragStarted;
        private Bookmark _groupTarget;

        /// <summary>Which side this panel represents (set from MainWindow XAML).</summary>
        public Side Side { get; set; } = Side.Left;

        /// <summary>Supplies the current directory path for this side (set by MainWindow).</summary>
        public Func<string> CurrentPathProvider { get; set; }

        /// <summary>Raised when a bookmark is activated; MainWindow navigates that side.</summary>
        public event EventHandler<string> NavigationRequested;

        public FavouritesPanel()
        {
            InitializeComponent();
            Loaded += FavouritesPanel_Loaded;
        }

        private void FavouritesPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_view != null) return;

            _view = CollectionViewSource.GetDefaultView(BookmarkStore.Instance.Items);

            // Group by FolderGroup. Group order follows the underlying collection order,
            // which BookmarkStore keeps with the default "Bookmarked" group pinned first.
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Bookmark.FolderGroup)));

            BookmarksList.ItemsSource = _view;
            RefreshDrives();
        }

        // --- Activation / navigation ---------------------------------------------
        private void BookmarksList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStarted) return;
            var hit = e.OriginalSource as DependencyObject;
            var b = hit != null ? GetBookmarkFromContainer(hit) : null;
            if (b != null && !b.IsEditing)
                NavigationRequested?.Invoke(this, b.Path);
        }

        public void SyncSelectionToPath(string path)
        {
            var match = string.IsNullOrEmpty(path)
                ? null
                : BookmarkStore.Instance.ForSide(Side).FirstOrDefault(
                    b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
            BookmarksList.SelectedItem = match;
        }

        // --- Add ------------------------------------------------------------------
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string path = CurrentPathProvider?.Invoke();
            AddFavourite(path);
        }

        public void AddFavourite(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (BookmarkStore.Instance.Find(Side, path) != null) return;
            string defaultName = path;
            try { defaultName = new System.IO.DirectoryInfo(path).Name; } catch { }
            BookmarkStore.Instance.Add(Side, path, defaultName);
            _view?.Refresh();
        }

        // --- Group assignment -----------------------------------------------------
        private void FavCtxSetGroup_Click(object sender, RoutedEventArgs e)
        {
            _groupTarget = BookmarkFromMenu(sender);
            if (_groupTarget == null) return;
            GroupNameBox.Text = _groupTarget.FolderGroup ?? string.Empty;
            GroupPopup.IsOpen = true;
            GroupNameBox.Focus();
            GroupNameBox.SelectAll();
        }

        private void FavCtxClearGroup_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b == null) return;
            b.FolderGroup = BookmarkStore.DefaultGroup;
            _view?.Refresh();
        }

        private void GroupPopupConfirm_Click(object sender, RoutedEventArgs e) => CommitGroup();
        private void GroupPopupCancel_Click(object sender, RoutedEventArgs e) => GroupPopup.IsOpen = false;

        private void GroupNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitGroup(); e.Handled = true; }
            else if (e.Key == Key.Escape) { GroupPopup.IsOpen = false; e.Handled = true; }
        }

        private void CommitGroup()
        {
            if (_groupTarget == null) { GroupPopup.IsOpen = false; return; }
            string g = GroupNameBox.Text.Trim();
            _groupTarget.FolderGroup = string.IsNullOrEmpty(g) ? BookmarkStore.DefaultGroup : g;
            GroupPopup.IsOpen = false;
            _view?.Refresh();
            _groupTarget = null;
        }

        // --- Delete ---------------------------------------------------------------
        private void BookmarksList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && BookmarksList.SelectedItem is Bookmark b && !b.IsEditing)
            {
                BookmarkStore.Instance.Remove(Side, b);
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && BookmarksList.SelectedItem is Bookmark sel)
            {
                sel.IsEditing = true;
                e.Handled = true;
            }
        }

        // --- Inline rename --------------------------------------------------------
        private void NameEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.IsVisible)
            {
                tb.Dispatcher.BeginInvoke(new Action(() =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void NameEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is Bookmark b)
            {
                if (e.Key == Key.Enter) { CommitRename(tb, b); e.Handled = true; }
                else if (e.Key == Key.Escape) { b.IsEditing = false; tb.Text = b.Name; e.Handled = true; }
            }
        }

        private void NameEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is Bookmark b)
                CommitRename(tb, b);
        }

        private void CommitRename(TextBox tb, Bookmark b)
        {
            if (!b.IsEditing) return;
            b.IsEditing = false;
            string newName = tb.Text.Trim();
            if (!string.IsNullOrEmpty(newName))
                b.Name = newName;
            else
                tb.Text = b.Name;
        }

        // --- Drag and drop --------------------------------------------------------
        private void BookmarksList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragStarted = false;
            _dragCandidate = (e.OriginalSource as DependencyObject) is DependencyObject d
                ? GetBookmarkFromContainer(d) : null;
        }

        private void BookmarksList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
            if (_dragCandidate.IsEditing) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragStarted = true;
            var data = new DataObject();
            data.SetData(BookmarkDragFormat, _dragCandidate);
            data.SetData("SourceSide", Side);
            DragDrop.DoDragDrop(BookmarksList, data, DragDropEffects.Move | DragDropEffects.Copy);
            _dragCandidate = null;
        }

        private void Panel_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(BookmarkDragFormat) || e.Data.GetDataPresent(FolderItemDragFormat))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Panel_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(FolderItemDragFormat) &&
                e.Data.GetData(FolderItemDragFormat) is FolderItem item)
            {
                BookmarkStore.Instance.Add(Side, item.FullPath, item.Name);
                _view?.Refresh();
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(BookmarkDragFormat) &&
                e.Data.GetData(BookmarkDragFormat) is Bookmark dragged)
            {
                // Bookmarks are global, so a drag from either panel is treated the same:
                // drop onto a different group reassigns the group; within the same group
                // it's a manual reorder.
                var target = GetDropTarget(e);
                string targetGroup = GetDropGroup(e, target);

                if (!string.Equals(dragged.FolderGroup, targetGroup, StringComparison.Ordinal))
                {
                    BookmarkStore.Instance.SetGroup(Side, dragged, targetGroup, target);
                }
                else if (target != null && target != dragged)
                {
                    var list = BookmarkStore.Instance.Items;
                    BookmarkStore.Instance.Reorder(Side, list.IndexOf(dragged), list.IndexOf(target));
                }
                _view?.Refresh();
                e.Handled = true;
            }
        }

        private Bookmark GetDropTarget(DragEventArgs e)
        {
            var hit = BookmarksList.InputHitTest(e.GetPosition(BookmarksList)) as DependencyObject;
            return hit != null ? GetBookmarkFromContainer(hit) : null;
        }

        /// <summary>
        /// Resolves the group a bookmark was dropped onto: the group of the target
        /// bookmark, or a group header dropped directly onto. Drops on empty space
        /// fall into the default group.
        /// </summary>
        private string GetDropGroup(DragEventArgs e, Bookmark target)
        {
            if (target != null) return target.FolderGroup;

            var hit = BookmarksList.InputHitTest(e.GetPosition(BookmarksList)) as DependencyObject;
            var header = hit != null ? FindGroupHeaderText(hit) : null;
            return string.IsNullOrEmpty(header) ? BookmarkStore.DefaultGroup : header;
        }

        private static string FindGroupHeaderText(DependencyObject d)
        {
            while (d != null && !(d is GroupItem))
                d = VisualTreeHelper.GetParent(d);
            if (d is GroupItem gi && gi.DataContext is CollectionViewGroup g)
                return g.Name as string;
            return null;
        }

        // --- Context menu helpers -------------------------------------------------
        private static Bookmark BookmarkFromMenu(object sender)
        {
            return (sender as MenuItem)?.DataContext as Bookmark;
        }

        private void FavCtxOpenRightPane_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null)
            {
                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.OpenPathInRightPane(b.Path);
                }
            }
        }

        private void FavCtxOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null) ContextActions.OpenInExplorer(b.Path);
        }

        private void FavCtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null) ContextActions.CopyPath(b.Path);
        }

        private void FavCtxRemove_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null) BookmarkStore.Instance.Remove(Side, b);
        }

        private Bookmark GetBookmarkFromContainer(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem))
                d = VisualTreeHelper.GetParent(d);
            return (d as ListBoxItem)?.DataContext as Bookmark;
        }

        // --- Drives ---------------------------------------------------------------
        private void RefreshDrives()
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d =>
                {
                    string name = string.IsNullOrWhiteSpace(d.VolumeLabel)
                        ? d.Name
                        : $"{d.VolumeLabel} ({d.Name})";
                    return new DriveEntry { Root = d.RootDirectory.FullName, Label = name };
                })
                .ToList();
            DrivesList.ItemsSource = drives;
        }

        private void DrivesToggleButton_Changed(object sender, RoutedEventArgs e)
        {
            DrivesList.Visibility = DrivesToggleButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Drive_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DriveEntry drive)
                NavigationRequested?.Invoke(this, drive.Root);
        }

        private class DriveEntry
        {
            public string Root { get; set; }
            public string Label { get; set; }
        }
    }
}
