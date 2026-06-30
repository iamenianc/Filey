using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Filey
{
    /// <summary>
    /// Singleton orchestrator for Filey's selective file index. Loads the persisted index
    /// for instant search, kicks a background reconcile crawl of the seed roots, live-watches
    /// the hot roots, and periodically refreshes the warm (history-derived) roots. Mirrors
    /// the <see cref="BookmarkStore"/> singleton pattern. Entirely non-admin / user-mode.
    /// </summary>
    public sealed class IndexService
    {
        private static readonly Lazy<IndexService> _instance = new Lazy<IndexService>(() => new IndexService());
        public static IndexService Instance => _instance.Value;

        private static readonly TimeSpan WarmRefreshInterval = TimeSpan.FromMinutes(5);

        private readonly FileIndex _index = new FileIndex();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private IndexWatcher _watcher;
        private Timer _warmTimer;
        private List<IndexRoot> _roots = new List<IndexRoot>();
        private string _lastPrioritized;
        private bool _started;

        private IndexService() { }

        /// <summary>Number of entries currently in the index (for diagnostics/verification).</summary>
        public int IndexedCount => _index.Count;

        /// <summary>Number of live watchers (should equal the hot-root count).</summary>
        public int WatcherCount => _watcher?.WatcherCount ?? 0;

        /// <summary>
        /// Loads the persisted index (instant search), then starts the background crawl,
        /// hot-root watchers, and warm-root refresh timer. Non-blocking past the load.
        /// </summary>
        public void Start(AppSettings settings)
        {
            if (_started) return;
            _started = true;

            _index.Load();
            _roots = IndexPolicy.ComputeRoots(settings);

            // Background reconcile crawl of every seed root.
            _ = FileSystemCrawler.CrawlAsync(_roots, _index, _cts.Token);

            // Live-watch only the hot roots.
            _watcher = new IndexWatcher(_index);
            _watcher.Watch(_roots.Where(r => r.Tier == IndexTier.Hot).Select(r => r.Path));

            // Periodically refresh the warm roots (history-derived, not live-watched).
            _warmTimer = new Timer(_ => RefreshWarmRoots(), null, WarmRefreshInterval, WarmRefreshInterval);
        }

        /// <summary>
        /// Immediately indexes the directory the user is currently viewing so its contents
        /// are searchable right away, ahead of the background seed crawl. Shallow (one level)
        /// and de-duplicated against the last prioritised path, so it's cheap to call on every
        /// navigation.
        /// </summary>
        public void PrioritizeActiveDirectory(string path)
        {
            if (!_started || _cts.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(path) || !IndexPolicy.IsIndexablePath(path)) return;
            if (string.Equals(path, _lastPrioritized, StringComparison.OrdinalIgnoreCase)) return;
            _lastPrioritized = path;

            _ = Task.Run(() =>
            {
                try { _index.ReplaceDirectoryLevel(path, FileSystemCrawler.IndexDirectoryLevel(path)); }
                catch { /* directory vanished or inaccessible; ignore */ }
            }, _cts.Token);
        }

        /// <summary>Re-crawls the warm (history-derived) roots. Safe to call on app activation.</summary>
        public void RefreshWarmRoots()
        {
            if (_cts.IsCancellationRequested) return;
            foreach (var root in _roots.Where(r => r.Tier == IndexTier.Warm))
                _ = FileSystemCrawler.RecrawlRootAsync(root.Path, _index, _cts.Token);
        }

        /// <summary>
        /// Ranked search projected to UI rows, run off the UI thread. The active directory and
        /// the immediate contents of its subfolders are searched live and listed first
        /// (whether or not they are indexed yet); index hits follow, de-duplicated by path.
        /// </summary>
        public Task<IReadOnlyList<FolderItem>> SearchAsync(string query, string activeDirectory, int max = 100)
        {
            return Task.Run<IReadOnlyList<FolderItem>>(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ordered = new List<IndexEntry>();

                // 1) Local-first: active dir + immediate contents of its subfolders, enumerated live.
                if (!string.IsNullOrEmpty(activeDirectory))
                {
                    var local = FileSystemCrawler.EnumerateLocalScope(activeDirectory);
                    foreach (var e in SearchRanker.Rank(query, local, max))
                        if (seen.Add(e.FullPath)) ordered.Add(e);
                }

                // 2) Index hits, appended (skipping anything the local pass already surfaced).
                foreach (var e in _index.Search(query, max))
                    if (seen.Add(e.FullPath)) ordered.Add(e);

                return (IReadOnlyList<FolderItem>)ordered
                    .Take(max)
                    .Select(e => e.ToFolderItem())
                    .ToList();
            });
        }

        /// <summary>Stops watching/crawling and persists the index. Call on shutdown.</summary>
        public void Shutdown()
        {
            if (!_started) return;
            _cts.Cancel();
            _warmTimer?.Dispose();
            _watcher?.Dispose();
            _index.Save();
        }
    }
}
