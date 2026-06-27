using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Filey
{
    public enum Side
    {
        Left,
        Right
    }

    /// <summary>
    /// In-memory singleton store for Favourites/bookmarks. Each side is independent.
    /// Disk persistence is deferred to Bullet 4 — Load/Save are stubs for now.
    /// </summary>
    public class BookmarkStore
    {
        private static readonly Lazy<BookmarkStore> _instance = new Lazy<BookmarkStore>(() => new BookmarkStore());
        public static BookmarkStore Instance => _instance.Value;

        /// <summary>Group a bookmark falls into when it has no explicit group. Always shown first.</summary>
        public const string DefaultGroup = "Bookmarked";

        public ObservableCollection<Bookmark> Left { get; } = new ObservableCollection<Bookmark>();
        public ObservableCollection<Bookmark> Right { get; } = new ObservableCollection<Bookmark>();

        /// <summary>Suppresses auto-save while bulk-loading from disk.</summary>
        private bool _suppressSave;

        private BookmarkStore()
        {
            Left.CollectionChanged += OnSideChanged;
            Right.CollectionChanged += OnSideChanged;
        }

        private void OnSideChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (Bookmark b in e.NewItems) b.PropertyChanged += OnBookmarkChanged;
            if (e.OldItems != null)
                foreach (Bookmark b in e.OldItems) b.PropertyChanged -= OnBookmarkChanged;
            SaveToDisk();
        }

        private void OnBookmarkChanged(object sender, PropertyChangedEventArgs e)
        {
            // IsEditing is transient UI state; persist only durable fields.
            if (e.PropertyName == nameof(Bookmark.IsEditing)) return;
            SaveToDisk();
        }

        public ObservableCollection<Bookmark> ForSide(Side side)
        {
            return side == Side.Left ? Left : Right;
        }

        public Bookmark Add(Side side, string path, string name, string folderGroup = null)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var existing = Find(side, path);
            if (existing != null) return existing;

            var bookmark = new Bookmark
            {
                Path = path,
                Name = string.IsNullOrWhiteSpace(name) ? GetDefaultName(path) : name.Trim(),
                FolderGroup = string.IsNullOrWhiteSpace(folderGroup) ? DefaultGroup : folderGroup.Trim()
            };

            // Default-group bookmarks insert at the front so "Bookmarked" stays on top.
            var list = ForSide(side);
            if (bookmark.FolderGroup == DefaultGroup)
                list.Insert(0, bookmark);
            else
                list.Add(bookmark);
            return bookmark;
        }

        /// <summary>
        /// Reassigns a bookmark's group and repositions it next to the drop target so it
        /// lands in the right place visually. With no target it joins the end of its group.
        /// </summary>
        public void SetGroup(Side side, Bookmark bookmark, string group, Bookmark target)
        {
            if (bookmark == null) return;

            // Reposition next to the target first (while indices are stable), then
            // reassign the group, so the grouped view never observes a transient state.
            if (target != null && target != bookmark)
            {
                var list = ForSide(side);
                int from = list.IndexOf(bookmark);
                int to = list.IndexOf(target);
                if (from >= 0 && to >= 0 && from != to)
                    list.Move(from, to);
            }

            bookmark.FolderGroup = string.IsNullOrWhiteSpace(group) ? DefaultGroup : group;
        }

        public void Remove(Side side, Bookmark bookmark)
        {
            if (bookmark == null) return;
            ForSide(side).Remove(bookmark);
        }

        public void Reorder(Side side, int fromIndex, int toIndex)
        {
            var list = ForSide(side);
            if (fromIndex < 0 || fromIndex >= list.Count) return;
            if (toIndex < 0) toIndex = 0;
            if (toIndex >= list.Count) toIndex = list.Count - 1;
            if (fromIndex == toIndex) return;
            list.Move(fromIndex, toIndex);
        }

        public bool Contains(Side side, string path)
        {
            return Find(side, path) != null;
        }

        public Bookmark Find(Side side, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return ForSide(side).FirstOrDefault(
                b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Copy a bookmark to the target side (used for cross-side drag-and-drop).</summary>
        public Bookmark Copy(Bookmark source, Side toSide)
        {
            if (source == null) return null;

            // Duplicate paths are not allowed on a side — skip if already present.
            var existing = Find(toSide, source.Path);
            if (existing != null) return existing;

            var clone = source.Clone();
            ForSide(toSide).Add(clone);
            return clone;
        }

        private static string GetDefaultName(string path)
        {
            try
            {
                var name = new DirectoryInfo(path).Name;
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch
            {
                return path;
            }
        }

        // --- Persistence ----------------------------------------------------------
        private const string FileName = "bookmarks.json";

        /// <summary>Flat on-disk shape: both sides share one file, separated by Side.</summary>
        private class BookmarkRecord
        {
            public string Id { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public string FolderGroup { get; set; }
            public DateTime CreatedAt { get; set; }
            public Side Side { get; set; }
        }

        public void LoadFromDisk()
        {
            string json = AppStorage.ReadAllTextOrNull(AppStorage.PathFor(FileName));
            if (string.IsNullOrEmpty(json)) return;

            List<BookmarkRecord> records;
            try
            {
                records = JsonConvert.DeserializeObject<List<BookmarkRecord>>(json);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"bookmarks.json parse failed: {ex.Message}");
                return;
            }
            if (records == null) return;

            _suppressSave = true;
            try
            {
                Left.Clear();
                Right.Clear();
                foreach (var r in records)
                {
                    if (string.IsNullOrEmpty(r.Path)) continue;
                    var bookmark = new Bookmark
                    {
                        Id = string.IsNullOrEmpty(r.Id) ? Guid.NewGuid().ToString() : r.Id,
                        Path = r.Path,
                        Name = string.IsNullOrWhiteSpace(r.Name) ? GetDefaultName(r.Path) : r.Name,
                        FolderGroup = string.IsNullOrWhiteSpace(r.FolderGroup) ? DefaultGroup : r.FolderGroup,
                        CreatedAt = r.CreatedAt == default(DateTime) ? DateTime.Now : r.CreatedAt
                    };
                    ForSide(r.Side).Add(bookmark);
                }
            }
            finally
            {
                _suppressSave = false;
            }
        }

        public void SaveToDisk()
        {
            if (_suppressSave) return;

            var records = new List<BookmarkRecord>();
            records.AddRange(ToRecords(Left, Side.Left));
            records.AddRange(ToRecords(Right, Side.Right));

            string json = JsonConvert.SerializeObject(records, Formatting.Indented);
            AppStorage.WriteAllTextAtomic(AppStorage.PathFor(FileName), json);
        }

        private static IEnumerable<BookmarkRecord> ToRecords(IEnumerable<Bookmark> source, Side side)
        {
            return source.Select(b => new BookmarkRecord
            {
                Id = b.Id,
                Path = b.Path,
                Name = b.Name,
                FolderGroup = b.FolderGroup,
                CreatedAt = b.CreatedAt,
                Side = side
            });
        }
    }
}
