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
