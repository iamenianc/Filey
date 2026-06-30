using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FuzzySharp;
using Newtonsoft.Json;

namespace Filey
{
    /// <summary>
    /// Thread-safe in-memory store of <see cref="IndexEntry"/> keyed by full path, with
    /// fuzzy ranked search (FuzzySharp) and JSON persistence via <see cref="AppStorage"/>.
    /// Mutated by the crawler and the watcher; read by the UI search box.
    /// </summary>
    internal sealed class FileIndex
    {
        private const string FileName = "index.json";

        private readonly object _gate = new object();
        private readonly Dictionary<string, IndexEntry> _byPath =
            new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { lock (_gate) return _byPath.Count; } }

        public void AddOrUpdate(IndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.FullPath)) return;
            lock (_gate) _byPath[entry.FullPath] = entry;
        }

        public void Remove(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            lock (_gate)
            {
                _byPath.Remove(fullPath);
                // A deleted directory takes its whole indexed subtree with it.
                string prefix = fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var children = _byPath.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var k in children) _byPath.Remove(k);
            }
        }

        /// <summary>
        /// Reconciles only the direct children of <paramref name="dirPath"/> (one level),
        /// leaving any deeper indexed entries untouched. Used to index the directory the
        /// user is actively viewing without disturbing a deeper crawl already in place.
        /// </summary>
        public void ReplaceDirectoryLevel(string dirPath, IEnumerable<IndexEntry> entries)
        {
            if (string.IsNullOrEmpty(dirPath)) return;
            lock (_gate)
            {
                var stale = _byPath.Values
                    .Where(v => string.Equals(v.ParentPath, dirPath, StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.FullPath)
                    .ToList();
                foreach (var k in stale) _byPath.Remove(k);
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrEmpty(e.FullPath)) _byPath[e.FullPath] = e;
            }
        }

        /// <summary>Replaces every entry under <paramref name="rootPath"/> with a freshly crawled set.</summary>
        public void ReplaceSubtree(string rootPath, IEnumerable<IndexEntry> entries)
        {
            string prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            lock (_gate)
            {
                var stale = _byPath.Keys
                    .Where(k => string.Equals(k, rootPath, StringComparison.OrdinalIgnoreCase)
                             || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var k in stale) _byPath.Remove(k);
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrEmpty(e.FullPath)) _byPath[e.FullPath] = e;
            }
        }

        /// <summary>
        /// Returns up to <paramref name="max"/> results ranked by closeness to
        /// <paramref name="query"/>. Cheap subsequence pre-filter first, then FuzzySharp
        /// scoring on the survivors with a prefix/substring bonus.
        /// </summary>
        public List<IndexEntry> Search(string query, int max = 100)
        {
            var results = new List<IndexEntry>();
            if (string.IsNullOrWhiteSpace(query)) return results;
            query = query.Trim();
            string q = query.ToLowerInvariant();

            IndexEntry[] snapshot;
            lock (_gate) snapshot = _byPath.Values.ToArray();

            var scored = new List<KeyValuePair<int, IndexEntry>>();
            foreach (var e in snapshot)
            {
                string name = e.Name;
                if (string.IsNullOrEmpty(name)) continue;
                string nl = name.ToLowerInvariant();

                int bonus;
                if (!QuickMatch(nl, q, out bonus)) continue;

                int score = Fuzz.WeightedRatio(q, nl) + bonus;
                scored.Add(new KeyValuePair<int, IndexEntry>(score, e));
            }

            return scored
                .OrderByDescending(kv => kv.Key)
                .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(kv => kv.Value)
                .ToList();
        }

        /// <summary>
        /// Cheap gate before fuzzy scoring: requires the query to appear as an in-order
        /// subsequence of the name. Awards a bonus for exact/prefix/substring matches so
        /// they outrank loose fuzzy hits.
        /// </summary>
        private static bool QuickMatch(string nameLower, string queryLower, out int bonus)
        {
            bonus = 0;
            if (nameLower == queryLower) { bonus = 200; return true; }
            if (nameLower.StartsWith(queryLower, StringComparison.Ordinal)) { bonus = 120; return true; }

            int idx = nameLower.IndexOf(queryLower, StringComparison.Ordinal);
            if (idx >= 0) { bonus = 60; return true; }

            // Subsequence test (chars in order, gaps allowed).
            int qi = 0;
            for (int i = 0; i < nameLower.Length && qi < queryLower.Length; i++)
                if (nameLower[i] == queryLower[qi]) qi++;
            return qi == queryLower.Length;
        }

        // --- persistence ----------------------------------------------------------

        public void Save()
        {
            IndexEntry[] snapshot;
            lock (_gate) snapshot = _byPath.Values.ToArray();
            try
            {
                string json = JsonConvert.SerializeObject(snapshot, Formatting.None);
                AppStorage.WriteAllTextAtomic(AppStorage.PathFor(FileName), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"index.json save failed: {ex.Message}");
            }
        }

        public void Load()
        {
            string json = AppStorage.ReadAllTextOrNull(AppStorage.PathFor(FileName));
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var entries = JsonConvert.DeserializeObject<List<IndexEntry>>(json);
                if (entries == null) return;
                lock (_gate)
                {
                    _byPath.Clear();
                    foreach (var e in entries)
                        if (e != null && !string.IsNullOrEmpty(e.FullPath)) _byPath[e.FullPath] = e;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"index.json load failed: {ex.Message}");
            }
        }
    }
}
