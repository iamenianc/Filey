using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Filey
{
    /// <summary>
    /// Crawls seed roots with <see cref="NativeDirectoryEnumerator"/>, honouring
    /// <see cref="IndexPolicy.ShouldSkipDirectory"/> while descending so system/junk
    /// subtrees are never walked. Runs off the UI thread, cancellable, with bounded
    /// concurrency across roots. Read-only.
    /// </summary>
    internal static class FileSystemCrawler
    {
        /// <summary>Safety cap on entries collected per root, so one huge tree can't blow up the index.</summary>
        private const int MaxEntriesPerRoot = 200000;

        private const int MaxConcurrentRoots = 4;

        /// <summary>Crawls every root and reconciles it into <paramref name="index"/>.</summary>
        public static async Task CrawlAsync(IEnumerable<IndexRoot> roots, FileIndex index, CancellationToken token)
        {
            using (var gate = new SemaphoreSlim(MaxConcurrentRoots))
            {
                var tasks = new List<Task>();
                foreach (var root in roots)
                {
                    string path = root.Path;
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    tasks.Add(Task.Run(() =>
                    {
                        try { RecrawlRoot(path, index, token); }
                        finally { gate.Release(); }
                    }, token));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        /// <summary>Re-crawls a single root on a background thread and replaces its subtree in the index.</summary>
        public static Task RecrawlRootAsync(string rootPath, FileIndex index, CancellationToken token)
            => Task.Run(() => RecrawlRoot(rootPath, index, token), token);

        /// <summary>Walks one root (iterative DFS, depth-capped) and replaces its indexed subtree.</summary>
        public static void RecrawlRoot(string rootPath, FileIndex index, CancellationToken token)
        {
            if (string.IsNullOrEmpty(rootPath)) return;

            var collected = new List<IndexEntry>();
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(rootPath, 0));

            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) return;
                if (collected.Count >= MaxEntriesPerRoot) break;

                var current = stack.Pop();
                string dir = current.Key;
                int depth = current.Value;

                List<NativeFileEntry> entries;
                try { entries = NativeDirectoryEnumerator.EnumerateEntries(dir); }
                catch { continue; }

                foreach (var e in entries)
                {
                    if (e.IsDirectory)
                    {
                        if (IndexPolicy.ShouldSkipDirectory(e.FullPath, e.Attributes)) continue;
                        collected.Add(ToEntry(e, dir));
                        if (depth < IndexPolicy.MaxDepth)
                            stack.Push(new KeyValuePair<string, int>(e.FullPath, depth + 1));
                    }
                    else
                    {
                        collected.Add(ToEntry(e, dir));
                    }
                }
            }

            if (token.IsCancellationRequested) return;
            index.ReplaceSubtree(rootPath, collected);
        }

        /// <summary>
        /// Indexes just the direct contents of <paramref name="dirPath"/> (no recursion).
        /// Fast single pass used to prioritise the directory the user is currently viewing.
        /// </summary>
        public static List<IndexEntry> IndexDirectoryLevel(string dirPath)
        {
            var list = new List<IndexEntry>();
            if (string.IsNullOrEmpty(dirPath)) return list;

            List<NativeFileEntry> entries;
            try { entries = NativeDirectoryEnumerator.EnumerateEntries(dirPath); }
            catch { return list; }

            foreach (var e in entries)
            {
                if (e.IsDirectory && IndexPolicy.ShouldSkipDirectory(e.FullPath, e.Attributes)) continue;
                list.Add(ToEntry(e, dirPath));
            }
            return list;
        }

        /// <summary>Cap on entries gathered for a single local-scope search pass.</summary>
        private const int MaxLocalScopeEntries = 20000;

        /// <summary>
        /// Live "local scope" for search: the direct contents of <paramref name="activeDir"/>
        /// plus the immediate contents of each (non-skipped) subfolder directly inside it.
        /// Enumerated fresh so results reflect the folder the user is in whether or not it has
        /// been indexed yet. Bounded by <see cref="MaxLocalScopeEntries"/>.
        /// </summary>
        public static List<IndexEntry> EnumerateLocalScope(string activeDir)
        {
            var list = new List<IndexEntry>();
            if (string.IsNullOrEmpty(activeDir)) return list;

            List<NativeFileEntry> top;
            try { top = NativeDirectoryEnumerator.EnumerateEntries(activeDir); }
            catch { return list; }

            foreach (var e in top)
            {
                // Active directory's own direct children are always in scope.
                list.Add(ToEntry(e, activeDir));
                if (list.Count >= MaxLocalScopeEntries) return list;

                // One level deeper: immediate contents of each subfolder in the active dir.
                if (!e.IsDirectory || IndexPolicy.ShouldSkipDirectory(e.FullPath, e.Attributes)) continue;

                List<NativeFileEntry> children;
                try { children = NativeDirectoryEnumerator.EnumerateEntries(e.FullPath); }
                catch { continue; }

                foreach (var c in children)
                {
                    list.Add(ToEntry(c, e.FullPath));
                    if (list.Count >= MaxLocalScopeEntries) return list;
                }
            }
            return list;
        }

        private static IndexEntry ToEntry(NativeFileEntry e, string parentPath)
        {
            return new IndexEntry
            {
                Name = e.Name,
                FullPath = e.FullPath,
                ParentPath = parentPath,
                IsDirectory = e.IsDirectory,
                Size = e.Size,
                DateModifiedUtc = e.LastWriteTimeUtc
            };
        }
    }
}
