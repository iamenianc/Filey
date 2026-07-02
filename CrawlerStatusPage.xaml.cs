using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Filey
{
    public partial class CrawlerStatusPage : Page
    {
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _uiBatchTimer;

        public CrawlerStatusPage()
        {
            InitializeComponent();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _uiBatchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _uiBatchTimer.Tick += UiBatchTimer_Tick;

            this.Unloaded += (s, e) => { _refreshTimer.Stop(); _uiBatchTimer.Stop(); };
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshStats();
            RootsListView.ItemsSource = IndexService.Instance.Roots;
            _refreshTimer.Start();
            _uiBatchTimer.Start();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshStats();
        }

        private void UiBatchTimer_Tick(object sender, EventArgs e)
        {
            var batch = IndexService.Instance.DequeueAllUiUpdates();
            if (batch.Count == 0) return;
            IndexService.Instance.ApplyBatchedIndexUpdates(batch);
            // Refresh any visible stats so the UI reflects the new index size.
            RefreshStats();
        }

        private void RefreshStats()
        {
            IndexedCountText.Text = IndexService.Instance.IndexedCount.ToString("N0");
            WatcherCountText.Text = IndexService.Instance.WatcherCount.ToString("N0");
        }

        private void ReCrawlButton_Click(object sender, RoutedEventArgs e)
        {
            IndexService.Instance.ForceReCrawl();
            MessageBox.Show("Background crawler started! Listings will update dynamically.", "Indexer", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshStats();
        }


    }
}
