using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Filey
{
    /// <summary>How aggressively a seed root is kept fresh.</summary>
    public enum IndexTier
    {
        /// <summary>Indexed and live-watched with a <see cref="FileSystemWatcher"/>.</summary>
        Hot,
        /// <summary>Indexed but refreshed periodically rather than live-watched.</summary>
        Warm
    }

    /// <summary>A folder to crawl, with the freshness tier it was assigned.</summary>
    public struct IndexRoot
    {
        public string Path;
        public IndexTier Tier;
    }

    /// <summary>
    /// Single source of truth for "selective": decides WHICH folders get indexed/watched
    /// (seed roots, tiered by how likely the user is to visit them) and WHICH subtrees are
    /// skipped (system folders, junk, junctions). Pure policy — it does no I/O beyond
    /// existence checks and reads only already-loaded signals.
    /// </summary>
    internal static class IndexPolicy
    {
        /// <summary>Max directory depth crawled below a seed root, to bound index size.</summary>
        public const int MaxDepth = 12;

        /// <summary>Folder names never worth indexing or watching (matched case-insensitively).</summary>
        private static readonly HashSet<string> JunkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".svn", ".hg", "obj", "bin", "$recycle.bin",
            "system volume information", ".vs", ".gradle", ".nuget", "__pycache__",
            "appdata", "packages"
        };

        /// <summary>Absolute roots whose subtrees are never indexed (system locations).</summary>
        private static readonly Lazy<string[]> ExcludedRoots = new Lazy<string[]>(() =>
            new[]
            {
                Folder(Environment.SpecialFolder.Windows),
                Folder(Environment.SpecialFolder.ProgramFiles),
                Folder(Environment.SpecialFolder.ProgramFilesX86),
                Folder(Environment.SpecialFolder.CommonApplicationData),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(NormalizeForCompare)
            .ToArray());

        /// <summary>
        /// Computes the seed roots to index, tiered. Hot = explicit/known user locations
        /// (shell folders, bookmarks, home paths); Warm = recently-visited history paths.
        /// Result is normalised, existence-checked, and de-nested (a root contained inside
        /// another is dropped so no subtree is crawled twice).
        /// </summary>
        public static List<IndexRoot> ComputeRoots(AppSettings settings)
        {
            var ranked = new List<KeyValuePair<string, IndexTier>>();

            foreach (var p in ShellFolders()) ranked.Add(Pair(p, IndexTier.Hot));

            foreach (var b in BookmarkStore.Instance.Items)
                ranked.Add(Pair(b?.Path, IndexTier.Hot));

            if (settings != null)
            {
                ranked.Add(Pair(settings.LeftHomePath, IndexTier.Hot));
                ranked.Add(Pair(settings.RightHomePath, IndexTier.Hot));
            }

            var history = NavigationHistoryStore.Load();
            foreach (var p in history.Left.Concat(history.Right))
                ranked.Add(Pair(p, IndexTier.Warm));

            // Collapse to one entry per path, keeping the strongest (Hot beats Warm) tier,
            // and the strongest-tier-first order so de-nesting prefers hot roots.
            var byPath = new Dictionary<string, IndexTier>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ranked)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                string norm = NormalizeRoot(kv.Key);
                if (norm == null || !DirectoryExists(norm) || IsExcludedPath(norm)) continue;
                if (!byPath.TryGetValue(norm, out var existing) || kv.Value == IndexTier.Hot && existing == IndexTier.Warm)
                    byPath[norm] = kv.Value;
            }

            // Drop roots nested inside another retained root (keep the outer one).
            var roots = byPath
                .OrderBy(kv => kv.Value == IndexTier.Hot ? 0 : 1)
                .ThenBy(kv => kv.Key.Length)
                .ToList();

            var kept = new List<IndexRoot>();
            foreach (var kv in roots)
            {
                if (kept.Any(r => IsUnder(kv.Key, r.Path))) continue;
                kept.Add(new IndexRoot { Path = kv.Key, Tier = kv.Value });
            }
            return kept;
        }

        /// <summary>
        /// True if a directory encountered while crawling should be skipped entirely
        /// (not indexed, not descended into, not watched).
        /// </summary>
        public static bool ShouldSkipDirectory(string path, FileAttributes attrs)
        {
            // Junctions / symlinks: avoid cycles and OneDrive cloud-placeholder recall.
            if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;

            string name = SafeName(path);
            if (name != null && JunkNames.Contains(name)) return true;

            return IsExcludedPath(path);
        }

        /// <summary>
        /// True if a path reported by the watcher is worth indexing: not under a system
        /// root and with no junk directory anywhere in its ancestry (so events under
        /// node_modules/.git/etc. are ignored just like the crawler skips them).
        /// </summary>
        public static bool IsIndexablePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (IsExcludedPath(path)) return false;
            foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (!string.IsNullOrEmpty(segment) && JunkNames.Contains(segment)) return false;
            return true;
        }

        /// <summary>True if <paramref name="path"/> lives under a system/excluded root.</summary>
        public static bool IsExcludedPath(string path)
        {
            string norm = NormalizeForCompare(path);
            if (norm == null) return false;
            foreach (var root in ExcludedRoots.Value)
            {
                if (norm == root || norm.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // --- helpers --------------------------------------------------------------

        private static IEnumerable<string> ShellFolders()
        {
            yield return Folder(Environment.SpecialFolder.DesktopDirectory);
            yield return Folder(Environment.SpecialFolder.MyDocuments);
            yield return Folder(Environment.SpecialFolder.MyPictures);
            yield return Folder(Environment.SpecialFolder.MyMusic);
            yield return Folder(Environment.SpecialFolder.MyVideos);

            string profile = Folder(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                yield return Path.Combine(profile, "Downloads");
        }

        private static KeyValuePair<string, IndexTier> Pair(string path, IndexTier tier)
            => new KeyValuePair<string, IndexTier>(path, tier);

        private static string Folder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); }
            catch { return null; }
        }

        /// <summary>Normalises a candidate root to a full path with no trailing separator.</summary>
        private static string NormalizeRoot(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        private static string NormalizeForCompare(string path)
        {
            string norm = NormalizeRoot(path);
            return norm; // comparisons are done case-insensitively by callers
        }

        private static bool IsUnder(string child, string ancestor)
        {
            return child.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool DirectoryExists(string path)
        {
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { return null; }
        }
    }
}
