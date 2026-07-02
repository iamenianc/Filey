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
        public static async Task CrawlAsync(IEnumerable<IndexRoot> roots, CancellationToken token)
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
                        try { RecrawlRoot(path, token); }
                        finally { gate.Release(); }
                    }, token));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        public static Task RecrawlRootAsync(string rootPath, CancellationToken token)
            => Task.Run(() => RecrawlRoot(rootPath, token), token);

        private struct FileScanResult
        {
            public NativeFileEntry Entry { get; }
            public string ParentPath { get; }

            public FileScanResult(NativeFileEntry entry, string parentPath)
            {
                Entry = entry;
                ParentPath = parentPath;
            }
        }

        public static void RecrawlRoot(string rootPath, CancellationToken token)
        {
            if (string.IsNullOrEmpty(rootPath)) return;

            SQLiteIndexService.Instance.DeleteSubtree(rootPath);

            using (var queue = new System.Collections.Concurrent.BlockingCollection<FileScanResult>(10000))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                int collectedCount = 0;

                var readerTask = Task.Run(() =>
                {
                    try
                    {
                        var stack = new Stack<KeyValuePair<string, int>>();
                        stack.Push(new KeyValuePair<string, int>(rootPath, 0));

                        while (stack.Count > 0)
                        {
                            if (linkedCts.Token.IsCancellationRequested) break;
                            if (Volatile.Read(ref collectedCount) >= MaxEntriesPerRoot) break;

                            var current = stack.Pop();
                            string dir = current.Key;
                            int depth = current.Value;

                            List<NativeFileEntry> entries;
                            try { entries = NativeDirectoryEnumerator.EnumerateEntries(dir); }
                            catch { continue; }

                            foreach (var e in entries)
                            {
                                if (linkedCts.Token.IsCancellationRequested) break;
                                if (Volatile.Read(ref collectedCount) >= MaxEntriesPerRoot) break;

                                if (e.IsDirectory)
                                {
                                    if (IndexPolicy.ShouldSkipDirectory(e.FullPath, e.Attributes)) continue;

                                    queue.Add(new FileScanResult(e, dir), linkedCts.Token);

                                    if (depth < IndexPolicy.MaxDepth)
                                        stack.Push(new KeyValuePair<string, int>(e.FullPath, depth + 1));
                                }
                                else
                                {
                                    queue.Add(new FileScanResult(e, dir), linkedCts.Token);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        queue.CompleteAdding();
                    }
                }, linkedCts.Token);

                int numConsumers = Math.Max(1, Environment.ProcessorCount - 1);
                var consumers = new Task[numConsumers];
                for (int i = 0; i < numConsumers; i++)
                {
                    consumers[i] = Task.Run(() =>
                    {
                        try
                        {
                            var batch = new List<IndexEntry>();
                            foreach (var item in queue.GetConsumingEnumerable(linkedCts.Token))
                            {
                                if (Volatile.Read(ref collectedCount) >= MaxEntriesPerRoot)
                                {
                                    linkedCts.Cancel();
                                    break;
                                }

                                var entry = ToEntry(item.Entry, item.ParentPath);
                                batch.Add(entry);

                                if (batch.Count >= 5000)
                                {
                                    SQLiteIndexService.Instance.UpsertEntries(batch);
                                    batch.Clear();
                                }

                                Interlocked.Increment(ref collectedCount);
                            }
                            if (batch.Count > 0)
                            {
                                SQLiteIndexService.Instance.UpsertEntries(batch);
                            }
                        }
                        catch (OperationCanceledException) { }
                    }, linkedCts.Token);
                }

                try
                {
                    Task.WaitAll(consumers);
                    readerTask.Wait();
                }
                catch { }
            }
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
                ParentId = DirectoryRegistry.Instance.GetOrAdd(parentPath),
                IsDirectory = e.IsDirectory,
                Size = e.Size,
                DateModifiedUtc = e.LastWriteTimeUtc
            };
        }
    }
}
