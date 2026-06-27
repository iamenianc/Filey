using System;
using System.Collections.Generic;
using System.ComponentModel;
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

            var source = BookmarkStore.Instance.ForSide(Side);
            _view = CollectionViewSource.GetDefaultView(source);
            _view.Filter = FilterBookmark;
            BookmarksList.ItemsSource = _view;
            UpdatePlaceholders();
        }

        // --- Filtering ------------------------------------------------------------
        private bool FilterBookmark(object obj)
        {
            if (!(obj is Bookmark b)) return false;

            string tagFilter = TagFilterBox.Text?.Trim();
            string search = SearchBox.Text?.Trim();

            if (!string.IsNullOrEmpty(tagFilter))
            {
                bool anyTag = b.Tags != null && b.Tags.Any(
                    t => t.IndexOf(tagFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!anyTag) return false;
            }

            if (!string.IsNullOrEmpty(search))
            {
                bool nameMatch = !string.IsNullOrEmpty(b.Name) &&
                    b.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                bool tagMatch = b.Tags != null && b.Tags.Any(
                    t => t.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!nameMatch && !tagMatch) return false;
            }

            return true;
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            UpdatePlaceholders();
        }

        private void UpdatePlaceholders()
        {
            if (TagFilterPlaceholder != null)
                TagFilterPlaceholder.Visibility = string.IsNullOrEmpty(TagFilterBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (SearchPlaceholder != null)
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearTagFilter_Click(object sender, RoutedEventArgs e)
        {
            TagFilterBox.Clear();
        }

        // --- Activation / navigation ---------------------------------------------
        // Single click navigates (unless the click began a drag or the item is being renamed).
        private void BookmarksList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStarted) return;
            var hit = e.OriginalSource as DependencyObject;
            var b = hit != null ? GetBookmarkFromContainer(hit) : null;
            if (b != null && !b.IsEditing)
            {
                NavigationRequested?.Invoke(this, b.Path);
            }
        }

        // --- Add ------------------------------------------------------------------
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string path = CurrentPathProvider?.Invoke();
            AddFavourite(path);
        }

        /// <summary>Add a favourite immediately using the path's name as the default.
        /// Name and tags can be changed afterwards via the right-click menu.</summary>
        public void AddFavourite(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (BookmarkStore.Instance.Find(Side, path) != null) return;

            string defaultName = path;
            try { defaultName = new System.IO.DirectoryInfo(path).Name; } catch { }

            BookmarkStore.Instance.Add(Side, path, defaultName, Enumerable.Empty<string>());
            _view?.Refresh();
        }

        /// <summary>Open the form in tag-only mode to add tags to an existing bookmark.</summary>
        private void OpenTagPopup(Bookmark target)
        {
            _pendingAddPath = null;
            _tagTarget = target;
            PopupTitle.Text = "Add tags";
            PopupNameSection.Visibility = Visibility.Collapsed;
            PopupConfirmButton.Content = "Add";
            PopupTagsBox.Text = string.Empty;
            AddPopup.IsOpen = true;
            PopupTagsBox.Focus();
        }

        private string _pendingAddPath;
        private Bookmark _tagTarget;

        private static IEnumerable<string> ParseTags(string text)
        {
            return (text ?? string.Empty)
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t));
        }

        private void PopupAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_tagTarget != null)
            {
                foreach (var tag in ParseTags(PopupTagsBox.Text))
                {
                    if (!_tagTarget.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                        _tagTarget.Tags.Add(tag);
                }
                _view?.Refresh();
            }
            else if (!string.IsNullOrEmpty(_pendingAddPath))
            {
                BookmarkStore.Instance.Add(Side, _pendingAddPath, PopupNameBox.Text, ParseTags(PopupTagsBox.Text));
                _view?.Refresh();
            }
            AddPopup.IsOpen = false;
        }

        private void PopupCancel_Click(object sender, RoutedEventArgs e)
        {
            AddPopup.IsOpen = false;
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
            // Folder dragged in from Folders/Contents → new bookmark.
            if (e.Data.GetDataPresent(FolderItemDragFormat) &&
                e.Data.GetData(FolderItemDragFormat) is FolderItem item)
            {
                BookmarkStore.Instance.Add(Side, item.FullPath, item.Name, Enumerable.Empty<string>());
                _view?.Refresh();
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(BookmarkDragFormat) &&
                e.Data.GetData(BookmarkDragFormat) is Bookmark dragged)
            {
                var sourceSide = e.Data.GetDataPresent("SourceSide")
                    ? (Side)e.Data.GetData("SourceSide") : Side;

                if (sourceSide != Side)
                {
                    // Cross-side: copy into this panel.
                    BookmarkStore.Instance.Copy(dragged, Side);
                    _view?.Refresh();
                }
                else
                {
                    // Same side: reorder to dropped position.
                    var list = BookmarkStore.Instance.ForSide(Side);
                    int from = list.IndexOf(dragged);
                    int to = GetDropIndex(e);
                    if (to < 0) to = list.Count - 1;
                    BookmarkStore.Instance.Reorder(Side, from, to);
                    _view?.Refresh();
                }
                e.Handled = true;
            }
        }

        private int GetDropIndex(DragEventArgs e)
        {
            var pos = e.GetPosition(BookmarksList);
            var hit = BookmarksList.InputHitTest(pos) as DependencyObject;
            var target = hit != null ? GetBookmarkFromContainer(hit) : null;
            if (target != null)
                return BookmarkStore.Instance.ForSide(Side).IndexOf(target);
            return -1;
        }

        // --- Favourites context menu ---------------------------------------------
        private static Bookmark BookmarkFromMenu(object sender)
        {
            return (sender as MenuItem)?.DataContext as Bookmark;
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

        private void FavCtxSetRoot_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null) NavigationRequested?.Invoke(this, b.Path);
        }

        private void FavCtxAddTags_Click(object sender, RoutedEventArgs e)
        {
            var b = BookmarkFromMenu(sender);
            if (b != null) OpenTagPopup(b);
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
    }
}
