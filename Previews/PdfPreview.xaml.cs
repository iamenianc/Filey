using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Filey.Previews
{
    public partial class PdfPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // unused

        private List<PdfPageViewModel> _pdfPages;
        private string _activePdfPath;
        private PdfRenderer _pdfRenderer;
        private bool _isPdfActive;
        private readonly Dictionary<int, (CancellationTokenSource Cts, uint Width)> _activePageRenders = new Dictionary<int, (CancellationTokenSource Cts, uint Width)>();
        private const double PdfPageMargin = 24.0;
        private CancellationTokenSource _cts;

        public PdfPreview()
        {
            InitializeComponent();
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _activePdfPath = filePath;
            _pdfRenderer = null;
            _isPdfActive = false;

            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _cts.Token;

            PdfSkimGrid.Visibility = Visibility.Visible;
            PdfActiveViewer.Visibility = Visibility.Collapsed;
            PdfSkimGrid.Focus();

            string sizeStr = GetFormattedFileSize(filePath);

            var cached = PdfSkimCache.Get(filePath);
            if (cached != null)
            {
                ApplyPdfSkimLayout(cached.Length);
                PdfSkimImage0.Source = cached.Length > 0 ? cached[0] : null;
                PdfSkimImage1.Source = cached.Length > 1 ? cached[1] : null;
                PdfSkimImage2.Source = cached.Length > 2 ? cached[2] : null;
                PdfSkimImage3.Source = cached.Length > 3 ? cached[3] : null;

                StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("PDF", sizeStr));
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var renderer = new PdfRenderer();
                    if (!await renderer.LoadAsync(filePath, linkedToken)) return;

                    int count = renderer.PageCount >= 4 ? 4 : (renderer.PageCount > 0 ? 1 : 0);
                    var images = new BitmapSource[count];

                    for (int i = 0; i < count; i++)
                    {
                        if (linkedToken.IsCancellationRequested) return;
                        images[i] = await renderer.RenderPageToBoxAsync(i, 600.0, linkedToken);
                        if (linkedToken.IsCancellationRequested) return;
                    }

                    PdfSkimCache.Add(filePath, images);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath && !linkedToken.IsCancellationRequested && !_isPdfActive)
                        {
                            ApplyPdfSkimLayout(images.Length);
                            PdfSkimImage0.Source = images.Length > 0 ? images[0] : null;
                            PdfSkimImage1.Source = images.Length > 1 ? images[1] : null;
                            PdfSkimImage2.Source = images.Length > 2 ? images[2] : null;
                            PdfSkimImage3.Source = images.Length > 3 ? images[3] : null;

                            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("PDF", sizeStr));
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath)
                        {
                            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error", ex.Message));
                        }
                    }));
                }
            }, linkedToken);
        }

        public void Unload()
        {
            _cts?.Cancel();
            _cts = null;

            lock (_activePageRenders)
            {
                foreach (var pair in _activePageRenders.Values)
                {
                    pair.Cts?.Cancel();
                }
                _activePageRenders.Clear();
            }
            _pdfPages = null;
            _activePdfPath = null;
            _pdfRenderer = null;
            _isPdfActive = false;

            PdfSkimImage0.Source = null;
            PdfSkimImage1.Source = null;
            PdfSkimImage2.Source = null;
            PdfSkimImage3.Source = null;
            PdfPageItemsControl.ItemsSource = null;
        }

        public bool IsPdfActive => _isPdfActive;
        public List<PdfPageViewModel> PdfPages => _pdfPages;
        public double VerticalOffset => PdfActiveViewer.VerticalOffset;

        private void ApplyPdfSkimLayout(int pageCount)
        {
            if (pageCount < 4)
            {
                PdfSkimGrid.RowDefinitions.Clear();
                PdfSkimGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                PdfSkimGrid.ColumnDefinitions.Clear();
                PdfSkimGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (PdfSkimBorder1 != null) PdfSkimBorder1.Visibility = Visibility.Collapsed;
                if (PdfSkimBorder2 != null) PdfSkimBorder2.Visibility = Visibility.Collapsed;
                if (PdfSkimBorder3 != null) PdfSkimBorder3.Visibility = Visibility.Collapsed;
            }
            else
            {
                PdfSkimGrid.RowDefinitions.Clear();
                PdfSkimGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PdfSkimGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                PdfSkimGrid.ColumnDefinitions.Clear();
                PdfSkimGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                PdfSkimGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (PdfSkimBorder1 != null) PdfSkimBorder1.Visibility = Visibility.Visible;
                if (PdfSkimBorder2 != null) PdfSkimBorder2.Visibility = Visibility.Visible;
                if (PdfSkimBorder3 != null) PdfSkimBorder3.Visibility = Visibility.Visible;
            }
        }

        public void ActivateReader()
        {
            if (_isPdfActive || string.IsNullOrEmpty(_activePdfPath)) return;
            _isPdfActive = true;

            PdfSkimGrid.Visibility = Visibility.Collapsed;
            PdfActiveViewer.Visibility = Visibility.Visible;

            var filePath = _activePdfPath;
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    var renderer = new PdfRenderer();
                    if (!await renderer.LoadAsync(filePath, token)) return;

                    var ratios = renderer.PageAspectRatios();
                    var pages = new List<PdfPageViewModel>();
                    for (int i = 0; i < ratios.Count; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        pages.Add(new PdfPageViewModel(i, ratios[i]));
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath && !token.IsCancellationRequested)
                        {
                            _pdfRenderer = renderer;
                            _pdfPages = pages;

                            UpdatePdfPagesDisplayWidth();
                            PdfPageItemsControl.ItemsSource = _pdfPages;
                            UpdatePdfViewport();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath)
                        {
                            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error", ex.Message));
                        }
                    }));
                }
            }, token);
        }

        private void UpdatePdfPagesDisplayWidth()
        {
            if (_pdfPages == null) return;
            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            double displayWidth = Math.Round(PdfLayout.DisplayWidth(availableWidth, 48, false, 0, 1.0));

            foreach (var page in _pdfPages)
            {
                page.DisplayWidth = displayWidth;
            }
        }

        private void UpdatePdfViewport()
        {
            if (_pdfPages == null || _cts == null || _cts.IsCancellationRequested) return;

            double scrollOffset = PdfActiveViewer.VerticalOffset;
            double viewportHeight = PdfActiveViewer.ViewportHeight;
            if (viewportHeight <= 0) viewportHeight = PdfActiveViewer.ActualHeight;
            if (viewportHeight <= 0) viewportHeight = 600;

            var pageHeights = _pdfPages.ConvertAll(p => p.DisplayHeight);
            var visiblePageIndices = PdfLayout.VisiblePageIndices(
                scrollOffset, viewportHeight, pageHeights, PdfPageMargin, viewportHeight);

            foreach (var page in _pdfPages)
            {
                if (visiblePageIndices.Contains(page.PageIndex))
                {
                    uint targetWidth = (uint)page.DisplayWidth;
                    uint targetHeight = (uint)page.DisplayHeight;
                    const uint maxResolution = 2560;
                    if (targetWidth > maxResolution)
                    {
                        double ratio = (double)maxResolution / targetWidth;
                        targetWidth = maxResolution;
                        targetHeight = (uint)(targetHeight * ratio);
                    }

                    bool needsRender = page.Image == null;
                    if (!needsRender)
                    {
                        double renderedWidth = page.Image.PixelWidth;
                        if (Math.Abs(renderedWidth - targetWidth) > 2.0)
                        {
                            needsRender = true;
                        }
                    }

                    if (needsRender)
                    {
                        bool alreadyRendering = false;
                        lock (_activePageRenders)
                        {
                            if (_activePageRenders.TryGetValue(page.PageIndex, out var active))
                            {
                                if (active.Width == targetWidth)
                                {
                                    alreadyRendering = true;
                                }
                                else
                                {
                                    active.Cts.Cancel();
                                    _activePageRenders.Remove(page.PageIndex);
                                }
                            }
                        }

                        if (!alreadyRendering)
                        {
                            var pageCts = new CancellationTokenSource();
                            lock (_activePageRenders)
                            {
                                _activePageRenders[page.PageIndex] = (pageCts, targetWidth);
                            }

                            var pageIndex = page.PageIndex;
                            var pageToken = pageCts.Token;
                            var renderer = _pdfRenderer;

                            if (renderer != null)
                            {
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(100, pageToken);
                                        if (pageToken.IsCancellationRequested || _cts.IsCancellationRequested) return;

                                        var bitmap = await renderer.RenderPageAsync(pageIndex, targetWidth, targetHeight, pageToken);
                                        if (bitmap == null || pageToken.IsCancellationRequested || _cts.IsCancellationRequested) return;

                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            if (!pageToken.IsCancellationRequested && !_cts.IsCancellationRequested)
                                            {
                                                page.Image = bitmap;
                                                lock (_activePageRenders)
                                                {
                                                    if (_activePageRenders.TryGetValue(pageIndex, out var currentActive) && currentActive.Cts == pageCts)
                                                    {
                                                        _activePageRenders.Remove(pageIndex);
                                                    }
                                                }
                                            }
                                        }));
                                    }
                                    catch {}
                                }, pageToken);
                            }
                        }
                    }
                }
                else
                {
                    lock (_activePageRenders)
                    {
                        if (_activePageRenders.TryGetValue(page.PageIndex, out var active))
                        {
                            active.Cts.Cancel();
                            _activePageRenders.Remove(page.PageIndex);
                        }
                    }
                    page.Image = null;
                }
            }
        }

        private void PdfSkimGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                var window = Window.GetWindow(this);
                if (window != null && !string.IsNullOrEmpty(_activePdfPath))
                {
                    var previewWindow = new PreviewWindow(_activePdfPath)
                    {
                        Owner = window
                    };
                    previewWindow.Show();
                }
            }
        }

        private void PdfActiveViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isPdfActive)
            {
                UpdatePdfViewport();
            }
        }

        private void PdfActiveViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isPdfActive && _pdfPages != null)
            {
                UpdatePdfPagesDisplayWidth();
                UpdatePdfViewport();
            }
        }

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);
    }
}

namespace Filey
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Media.Imaging;

    public class PdfPageViewModel : ViewModelBase
    {
        private BitmapSource _image;
        private double _displayWidth;

        public int PageIndex { get; }
        public double AspectRatio { get; }

        public PdfPageViewModel(int pageIndex, double aspectRatio)
        {
            PageIndex = pageIndex;
            AspectRatio = aspectRatio;
        }

        public BitmapSource Image
        {
            get => _image;
            set => SetField(ref _image, value);
        }

        public double DisplayWidth
        {
            get => _displayWidth;
            set
            {
                if (SetField(ref _displayWidth, value))
                {
                    OnPropertyChanged(nameof(DisplayHeight));
                }
            }
        }

        public double DisplayHeight => Math.Round(DisplayWidth * AspectRatio);
    }

    public static class PdfSkimCache
    {
        private static readonly Dictionary<string, BitmapSource[]> _cache = new Dictionary<string, BitmapSource[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _lruList = new List<string>();
        private const int MaxCacheSize = 100;

        public static BitmapSource[] Get(string path)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(path, out var bitmaps))
                {
                    _lruList.Remove(path);
                    _lruList.Add(path);
                    return bitmaps;
                }
                return null;
            }
        }

        public static void Add(string path, BitmapSource[] bitmaps)
        {
            lock (_cache)
            {
                if (_cache.ContainsKey(path))
                {
                    _cache[path] = bitmaps;
                    _lruList.Remove(path);
                    _lruList.Add(path);
                    return;
                }

                if (_lruList.Count >= MaxCacheSize)
                {
                    string oldest = _lruList[0];
                    _lruList.RemoveAt(0);
                    _cache.Remove(oldest);
                }

                _cache[path] = bitmaps;
                _lruList.Add(path);
            }
        }
    }

    public class ExcelDateConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd");
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;
        }
    }
}
