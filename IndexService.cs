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

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private IndexWatcher _watcher;
        private Timer _warmTimer;
        private int _warmRefreshRunning;
        private List<IndexRoot> _roots = new List<IndexRoot>();
        private string _lastPrioritized;
        private bool _started;

        private IndexService() { }

        public int IndexedCount => (int)SQLiteIndexService.Instance.GetTotalEntryCount();

        public int WatcherCount => _watcher?.WatcherCount ?? 0;

        public IReadOnlyList<IndexRoot> Roots => _roots;

        public void Start(AppSettings settings)
        {
            if (_started) return;
            _started = true;

            _roots = IndexPolicy.ComputeRoots(settings);

            _ = FileSystemCrawler.CrawlAsync(_roots, _cts.Token);

            _watcher = new IndexWatcher();
            _watcher.Watch(_roots.Where(r => r.Tier == IndexTier.Hot).Select(r => r.Path));

            _warmTimer = new Timer(_ => RefreshWarmRoots(), null, WarmRefreshInterval, WarmRefreshInterval);
        }

        public void PrioritizeActiveDirectory(string path)
        {
            if (!_started || _cts.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(path) || !IndexPolicy.IsIndexablePath(path)) return;
            if (string.Equals(path, _lastPrioritized, StringComparison.OrdinalIgnoreCase)) return;
            _lastPrioritized = path;

            _ = Task.Run(() =>
            {
                try
                {
                    SQLiteIndexService.Instance.ReplaceDirectoryLevel(path, FileSystemCrawler.IndexDirectoryLevel(path));
                }
                catch { }
            }, _cts.Token);
        }

        public void RefreshWarmRoots()
        {
            if (_cts.IsCancellationRequested) return;
            if (System.Threading.Interlocked.CompareExchange(ref _warmRefreshRunning, 1, 0) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var tasks = _roots.Where(r => r.Tier == IndexTier.Warm)
                        .Select(r => FileSystemCrawler.RecrawlRootAsync(r.Path, _cts.Token));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _warmRefreshRunning, 0);
                }
            });
        }

        public void EnqueueUiIndexUpdate(IndexEntry entry) { }
        public List<IndexEntry> DequeueAllUiUpdates() => new List<IndexEntry>();
        public void ApplyBatchedIndexUpdates(IEnumerable<IndexEntry> entries) { }

        public async Task<IReadOnlyList<FolderItem>> SearchAsync(string query, string activeDirectory, int max = 100)
        {
            return await Task.Run(async () =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<IndexEntry>();

                if (!string.IsNullOrEmpty(activeDirectory))
                {
                    var local = FileSystemCrawler.EnumerateLocalScope(activeDirectory);
                    foreach (var e in local)
                    {
                        if (e != null && seen.Add(e.FullPath))
                        {
                            candidates.Add(e);
                        }
                    }
                }

                var dbCandidates = await SQLiteIndexService.Instance.GetSearchCandidatesAsync(query);
                foreach (var e in dbCandidates)
                {
                    if (e != null && seen.Add(e.FullPath))
                    {
                        candidates.Add(e);
                    }
                }

                var nodes = DirectoryRegistry.Instance.GetNodesSnapshot();
                var ordered = SearchRanker.Rank(query, candidates, max, activeDirectory, nodes);

                return (IReadOnlyList<FolderItem>)ordered
                    .Select(e => e.ToFolderItem())
                    .ToList();
            });
        }

        public void ForceReCrawl()
        {
            if (!_started || _cts.IsCancellationRequested) return;
            _ = FileSystemCrawler.CrawlAsync(_roots, _cts.Token);
        }

        public void Shutdown()
        {
            if (!_started) return;
            _cts.Cancel();
            _warmTimer?.Dispose();
            _watcher?.Dispose();
            SQLiteIndexService.Instance.Dispose();
        }
    }
}
