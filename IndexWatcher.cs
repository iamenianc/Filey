using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Filey
{
    /// <summary>
    /// Keeps the index live for a bounded set of "hot" roots using one
    /// <see cref="FileSystemWatcher"/> each. Events are filtered through
    /// <see cref="IndexPolicy.IsIndexablePath"/>, applied incrementally, and coalesced:
    /// directory churn and buffer overflows schedule a debounced re-crawl rather than
    /// thrashing the index per event.
    /// </summary>
    internal sealed class IndexWatcher : IDisposable
    {
        /// <summary>Upper bound on live watchers, regardless of how many hot roots exist.</summary>
        private const int MaxWatchers = 24;

        private readonly FileIndex _index;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly object _pendingGate = new object();
        private readonly HashSet<string> _pendingRecrawls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _flushTimer;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        public IndexWatcher(FileIndex index)
        {
            _index = index;
            _flushTimer = new Timer(FlushPending, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Starts watching the given hot roots (capped at <see cref="MaxWatchers"/>).</summary>
        public void Watch(IEnumerable<string> hotRoots)
        {
            foreach (var root in hotRoots)
            {
                if (_watchers.Count >= MaxWatchers) break;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                     | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    w.Created += OnCreated;
                    w.Deleted += OnDeleted;
                    w.Renamed += OnRenamed;
                    w.Changed += OnChanged;
                    w.Error += OnError;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"IndexWatcher failed for {root}: {ex.Message}");
                }
            }
        }

        public int WatcherCount => _watchers.Count;

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (!IndexPolicy.IsIndexablePath(e.FullPath)) return;
            if (Directory.Exists(e.FullPath))
                ScheduleRecrawl(e.FullPath); // new folder may arrive with contents
            else
                IndexFile(e.FullPath);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            _index.Remove(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            _index.Remove(e.OldFullPath);
            if (!IndexPolicy.IsIndexablePath(e.FullPath)) return;
            if (Directory.Exists(e.FullPath))
                ScheduleRecrawl(e.FullPath);
            else
                IndexFile(e.FullPath);
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!IndexPolicy.IsIndexablePath(e.FullPath)) return;
            if (File.Exists(e.FullPath))
                IndexFile(e.FullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow: events were lost, so re-crawl the whole watched root.
            if (sender is FileSystemWatcher w)
                ScheduleRecrawl(w.Path);
        }

        private void IndexFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
                _index.AddOrUpdate(new IndexEntry
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    ParentPath = fi.DirectoryName,
                    IsDirectory = false,
                    Size = fi.Length,
                    DateModifiedUtc = fi.LastWriteTimeUtc
                });
            }
            catch { /* file vanished or locked; ignore */ }
        }

        private void ScheduleRecrawl(string path)
        {
            lock (_pendingGate)
            {
                _pendingRecrawls.Add(path);
                // Debounce: (re)arm a single one-shot flush ~750ms out to coalesce bursts.
                _flushTimer.Change(750, Timeout.Infinite);
            }
        }

        private void FlushPending(object state)
        {
            List<string> batch;
            lock (_pendingGate)
            {
                if (_pendingRecrawls.Count == 0) return;
                batch = new List<string>(_pendingRecrawls);
                _pendingRecrawls.Clear();
            }
            foreach (var path in batch)
            {
                if (_cts.IsCancellationRequested) return;
                if (Directory.Exists(path))
                    _ = FileSystemCrawler.RecrawlRootAsync(path, _index, _cts.Token);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            foreach (var w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
            _flushTimer.Dispose();
            _cts.Dispose();
        }
    }
}
