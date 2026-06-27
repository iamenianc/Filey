using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

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

        private BookmarkStore() { }

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
            bookmark.FolderGroup = string.IsNullOrWhiteSpace(group) ? DefaultGroup : group;

            if (target == null || target == bookmark) return;

            var list = ForSide(side);
            int from = list.IndexOf(bookmark);
            int to = list.IndexOf(target);
            if (from < 0 || to < 0) return;
            list.Move(from, to);
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

        // --- Persistence (deferred to Bullet 4) -----------------------------------
        private static string StorePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Filey", "bookmarks.json");

        public void LoadFromDisk()
        {
            // TODO (Bullet 4): read StorePath via Newtonsoft.Json into Left/Right.
        }

        public void SaveToDisk()
        {
            // TODO (Bullet 4): serialise Left/Right to StorePath via Newtonsoft.Json.
        }
    }
}
