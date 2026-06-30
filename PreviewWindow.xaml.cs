using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Filey
{
    /// <summary>
    /// Interaction logic for PreviewWindow.xaml
    /// </summary>
    public partial class PreviewWindow : Wpf.Ui.Controls.FluentWindow
    {
        private double _zoomFactor = 1.0;
        private double _rotationAngle = 0;
        private Point _panStart;
        private double _scrollStartH;
        private double _scrollStartV;
        private bool _isPanning;
        private bool _hasManuallyZoomed = false;

        private static readonly string[] ImageExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff"
        };

        public PreviewWindow(string filePath)
        {
            InitializeComponent();

            _zoomDebounceTimer = new System.Windows.Threading.DispatcherTimer();
            _zoomDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _zoomDebounceTimer.Tick += ZoomDebounceTimer_Tick;

            this.SizeChanged += (s, e) =>
            {
                if (ImageScrollViewer.Visibility == Visibility.Visible && !_hasManuallyZoomed)
                {
                    FitImageToWindow();
                }
            };

            this.KeyDown += (s, e) =>
            {
                if (e.Key == Key.R && ImageScrollViewer.Visibility == Visibility.Visible)
                {
                    RotateImage();
                    e.Handled = true;
                }
                else if (PdfViewerGrid.Visibility == Visibility.Visible)
                {
                    HandlePdfKeyDown(e);
                }
            };

            LoadFile(filePath);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Match the title bar to the active theme now that the window has a native handle.
            ThemeService.ApplyTitleBar(this, ThemeService.IsDark);
        }

        private void LoadFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    ContentTextBox.Visibility = Visibility.Visible;
                    ContentTextBox.Text = "File not found.";
                    PathTextBlock.Text = filePath;
                    return;
                }

                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".md")
                {
                    try
                    {
                        MarkdownRenderer.OpenInBrowser(filePath);
                        ContentTextBox.Visibility = Visibility.Visible;
                        ContentTextBox.Text = $"Opened in default browser:\n{filePath}";
                    }
                    catch (Exception ex)
                    {
                        ContentTextBox.Visibility = Visibility.Visible;
                        ContentTextBox.Text = $"Error opening markdown:\n{ex.Message}";
                    }
                    PathTextBlock.Text = filePath;
                    SizeTextBlock.Text = GetFormattedFileSize(filePath);
                    Title = $"{Path.GetFileName(filePath)} — Preview";
                    return;
                }

                bool isImage = Array.Exists(ImageExtensions, e => e == ext);
                bool isPdf = ext == ".pdf";

                if (isImage)
                {
                    ImageScrollViewer.Visibility = Visibility.Visible;
                    ContentTextBox.Visibility = Visibility.Collapsed;

                    // Read original dimensions and rotation from metadata headers (blazingly fast)
                    GetOriginalImageDimensions(filePath, out int originalWidth, out int originalHeight, out Rotation rotation);
                    ContentImage.Width = originalWidth > 0 ? originalWidth : 2600;
                    ContentImage.Height = originalHeight > 0 ? originalHeight : 1950;

                    // Set initial display orientation angle based on EXIF rotation
                    _rotationAngle = ImageView.AngleFor(rotation);
                    ImageRotation.Angle = _rotationAngle;

                    UpdateImageMetadata(filePath, originalWidth, originalHeight);

                    // Asynchronously load the image using native WPF BitmapImage with downsampling
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // releases file lock immediately
                    bitmap.DecodePixelWidth = 1600;
                    bitmap.UriSource = new Uri(filePath);
                    bitmap.EndInit();

                    ContentImage.Source = bitmap;

                    // Fit to window based on the active image dimensions
                    Dispatcher.BeginInvoke(new Action(() => FitImageToWindow()), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else if (isPdf)
                {
                    LoadPdfFile(filePath);
                }
                else
                {
                    ContentTextBox.Visibility = Visibility.Visible;
                    ImageScrollViewer.Visibility = Visibility.Collapsed;

                    // Detect encoding and read content up to limit to keep UI responsive
                    const int MaxPreviewChars = 100000; // ~100 KB
                    char[] buffer = new char[MaxPreviewChars];
                    int charsRead;
                    string content;
                    Encoding encoding;
                    bool isTruncated = false;

                    using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
                    {
                        charsRead = reader.ReadBlock(buffer, 0, MaxPreviewChars);
                        content = new string(buffer, 0, charsRead);
                        encoding = reader.CurrentEncoding;
                        isTruncated = reader.Read() != -1;
                    }

                    if (isTruncated)
                    {
                        content += "\r\n\r\n[... Preview truncated due to large file size ...]";
                    }

                    ContentTextBox.Text = content;
                    PathTextBlock.Text = filePath;
                    EncodingTextBlock.Text = GetEncodingName(encoding);

                    string sizeStr = GetFormattedFileSize(filePath);
                    if (isTruncated)
                    {
                        sizeStr += " (Truncated)";
                    }
                    SizeTextBlock.Text = sizeStr;

                    Title = $"{Path.GetFileName(filePath)} — Preview";
                }
            }
            catch (Exception ex)
            {
                ContentTextBox.Visibility = Visibility.Visible;
                ImageScrollViewer.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}";
                PathTextBlock.Text = filePath;
            }
        }

        private void GetOriginalImageDimensions(string filePath, out int width, out int height, out Rotation rotation)
        {
            var info = ImageView.ReadOrientation(filePath);
            width = info.Width;
            height = info.Height;
            rotation = info.Rotation;
        }

        private void RotateImage()
        {
            _rotationAngle = ImageView.RotateStep(_rotationAngle);
            ImageRotation.Angle = _rotationAngle;

            // Recalculate original metadata display based on rotation state
            GetOriginalImageDimensions(PathTextBlock.Text, out int w, out int h, out Rotation r);
            if (_rotationAngle == 90 || _rotationAngle == 270)
            {
                // Active rotated display dimension
                UpdateImageMetadata(PathTextBlock.Text, h, w);
            }
            else
            {
                UpdateImageMetadata(PathTextBlock.Text, w, h);
            }

            FitImageToWindow();
        }

        private void UpdateImageMetadata(string filePath, int width, int height)
        {
            PathTextBlock.Text = filePath;
            EncodingTextBlock.Text = width > 0 && height > 0 ? $"{width} × {height} px" : "Image";
            SizeTextBlock.Text = GetFormattedFileSize(filePath);
            Title = $"{Path.GetFileName(filePath)} — Preview";
        }

        private void FitImageToWindow()
        {
            if (ContentImage.Source != null && ImageScrollViewer.ActualWidth > 0 && ImageScrollViewer.ActualHeight > 0)
            {
                double viewportWidth = ImageScrollViewer.ViewportWidth > 0 ? ImageScrollViewer.ViewportWidth : ImageScrollViewer.ActualWidth;
                double viewportHeight = ImageScrollViewer.ViewportHeight > 0 ? ImageScrollViewer.ViewportHeight : ImageScrollViewer.ActualHeight;

                _zoomFactor = ImageView.FitScale(viewportWidth, viewportHeight,
                    ContentImage.Width, ContentImage.Height, _rotationAngle);

                ImageScale.ScaleX = _zoomFactor;
                ImageScale.ScaleY = _zoomFactor;
            }
        }

        private string GetEncodingName(Encoding encoding) => ImageView.GetEncodingName(encoding);

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);

        private void ContentTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0)
                {
                    if (ContentTextBox.FontSize < 40)
                    {
                        ContentTextBox.FontSize += 1;
                    }
                }
                else if (e.Delta < 0)
                {
                    if (ContentTextBox.FontSize > 6)
                    {
                        ContentTextBox.FontSize -= 1;
                    }
                }
                e.Handled = true;
            }
        }

        private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _hasManuallyZoomed = true;

                double scaleRatio = 1.15;

                if (e.Delta > 0)
                {
                    if (_zoomFactor < 15.0)
                    {
                        _zoomFactor *= scaleRatio;
                    }
                    else
                    {
                        scaleRatio = 1.0;
                    }
                }
                else if (e.Delta < 0)
                {
                    if (_zoomFactor > 0.01)
                    {
                        _zoomFactor /= scaleRatio;
                        scaleRatio = 1.0 / scaleRatio;
                    }
                    else
                    {
                        scaleRatio = 1.0;
                    }
                }

                if (scaleRatio != 1.0)
                {
                    // Get mouse position relative to the ScrollViewer viewport
                    Point mousePosInViewport = e.GetPosition(ImageScrollViewer);

                    // Update scale transform values
                    ImageScale.ScaleX = _zoomFactor;
                    ImageScale.ScaleY = _zoomFactor;

                    // Force layout updates so scroll viewer calculates new extent size before scroll offsets adjustment
                    ImageScrollViewer.UpdateLayout();

                    // Apply new scrollbar offsets to center zoom on mouse cursor
                    ImageScrollViewer.ScrollToHorizontalOffset(
                        ImageView.ZoomAroundPoint(ImageScrollViewer.HorizontalOffset, mousePosInViewport.X, scaleRatio));
                    ImageScrollViewer.ScrollToVerticalOffset(
                        ImageView.ZoomAroundPoint(ImageScrollViewer.VerticalOffset, mousePosInViewport.Y, scaleRatio));
                }

                e.Handled = true;
            }
        }

        private void ImageGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _hasManuallyZoomed = false;
                _rotationAngle = 0;
                ImageRotation.Angle = 0;

                // Reset original dimensions display
                GetOriginalImageDimensions(PathTextBlock.Text, out int w, out int h, out Rotation r);
                UpdateImageMetadata(PathTextBlock.Text, w, h);

                FitImageToWindow();
                e.Handled = true;
                return;
            }

            if (ImageScrollViewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible ||
                ImageScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
                _panStart = e.GetPosition(ImageScrollViewer);
                _scrollStartH = ImageScrollViewer.HorizontalOffset;
                _scrollStartV = ImageScrollViewer.VerticalOffset;
                _isPanning = true;
                ImageGrid.CaptureMouse();
                ImageGrid.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        private void ImageGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                Point current = e.GetPosition(ImageScrollViewer);
                double deltaX = current.X - _panStart.X;
                double deltaY = current.Y - _panStart.Y;
                ImageScrollViewer.ScrollToHorizontalOffset(_scrollStartH - deltaX);
                ImageScrollViewer.ScrollToVerticalOffset(_scrollStartV - deltaY);
                e.Handled = true;
            }
        }

        private void ImageGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ImageGrid.ReleaseMouseCapture();
                ImageGrid.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        public int InitialPdfPageIndex { get; set; } = 0;
        private System.Collections.Generic.List<PdfPageViewModel> _pdfPages;
        private System.Collections.Generic.List<PdfThumbnailViewModel> _pdfThumbnails;
        private System.Collections.Generic.List<PdfPageRowViewModel> _pdfRows;
        private string _activePdfPath;
        private PdfRenderer _pdfRenderer;
        private CancellationTokenSource _pdfCts;
        private CancellationTokenSource _pdfScrollCts;
        private CancellationTokenSource _thumbnailCts;
        private bool _thumbnailsStarted;
        private System.Windows.Threading.DispatcherTimer _zoomDebounceTimer;
        private double _pdfZoomLevel = 1.0;
        private double _scaleVisualFactor = 1.0;

        private bool _isContinuousScroll = true;
        private bool _isDualPage = false;
        private int _currentPageIndex = 0;
        private bool _isSidebarVisible = true;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _pdfCts?.Cancel();
            _pdfScrollCts?.Cancel();
            _thumbnailCts?.Cancel();
            _zoomDebounceTimer?.Stop();
        }

        private void LoadPdfFile(string filePath)
        {
            _activePdfPath = filePath;
            _pdfRenderer = null;
            _thumbnailsStarted = false;
            ContentTextBox.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Collapsed;
            PdfViewerGrid.Visibility = Visibility.Visible;
            PdfToolbarBorder.Visibility = Visibility.Visible;

            PathTextBlock.Text = filePath;
            SizeTextBlock.Text = GetFormattedFileSize(filePath);
            Title = $"{Path.GetFileName(filePath)} — Preview";

            _pdfCts?.Cancel();
            _pdfCts = new CancellationTokenSource();
            var token = _pdfCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    var pages = new System.Collections.Generic.List<PdfPageViewModel>();
                    var thumbnails = new System.Collections.Generic.List<PdfThumbnailViewModel>();

                    var renderer = new PdfRenderer();
                    if (!await renderer.LoadAsync(filePath, token)) return;
                    _pdfRenderer = renderer;

                    var ratios = renderer.PageAspectRatios();
                    uint pageCount = (uint)ratios.Count;
                    const int batchSize = 20;

                    for (uint i = 0; i < pageCount; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        double aspectRatio = ratios[(int)i];
                        pages.Add(new PdfPageViewModel((int)i, aspectRatio));
                        thumbnails.Add(new PdfThumbnailViewModel((int)i, aspectRatio));

                        bool isFirstBatch = (i == (uint)Math.Min(batchSize - 1, (int)pageCount - 1));
                        bool isBatchBoundary = ((i + 1) % batchSize == 0);
                        bool isLast = (i == pageCount - 1);

                        if (isFirstBatch || isBatchBoundary || isLast)
                        {
                            var snapshot = new System.Collections.Generic.List<PdfPageViewModel>(pages);
                            var thumbSnapshot = new System.Collections.Generic.List<PdfThumbnailViewModel>(thumbnails);
                            bool firstBatch = isFirstBatch;
                            bool lastBatch = isLast;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_activePdfPath != filePath || token.IsCancellationRequested) return;

                                _pdfPages = snapshot;
                                _pdfThumbnails = thumbSnapshot;
                                PdfThumbnailItemsControl.ItemsSource = _pdfThumbnails;

                                RebuildPdfRows();

                                if (firstBatch)
                                {
                                    _pdfZoomLevel = 1.0;
                                    _scaleVisualFactor = 1.0;
                                    PdfScale.ScaleX = 1.0;
                                    PdfScale.ScaleY = 1.0;

                                    if (InitialPdfPageIndex > 0 && InitialPdfPageIndex < _pdfPages.Count)
                                    {
                                        _currentPageIndex = InitialPdfPageIndex;
                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            ScrollToPdfPage(InitialPdfPageIndex);
                                            FitPageButton_Click(null, null);
                                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                                    }
                                    else
                                    {
                                        _currentPageIndex = 0;
                                        UpdatePdfPagesDisplayWidth();
                                        UpdatePdfViewport();
                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            FitPageButton_Click(null, null);
                                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                                    }
                                    UpdatePageIndicator();
                                }
                                else
                                {
                                    UpdatePageIndicator();
                                }

                                if (lastBatch)
                                {
                                    RenderAllThumbnails();
                                }
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath)
                        {
                            ContentTextBox.Visibility = Visibility.Visible;
                            PdfViewerGrid.Visibility = Visibility.Collapsed;
                            PdfToolbarBorder.Visibility = Visibility.Collapsed;
                            ContentTextBox.Text = $"Error reading PDF file:\n{ex.Message}";
                        }
                    }));
                }
            }, token);
        }

        private void RebuildPdfRows()
        {
            if (_pdfPages == null) return;

            var rows = new System.Collections.Generic.List<PdfPageRowViewModel>();

            if (_isContinuousScroll)
            {
                if (_isDualPage)
                {
                    for (int i = 0; i < _pdfPages.Count; i += 2)
                    {
                        rows.Add(new PdfPageRowViewModel
                        {
                            LeftPage = _pdfPages[i],
                            RightPage = (i + 1 < _pdfPages.Count) ? _pdfPages[i + 1] : null
                        });
                    }
                }
                else
                {
                    for (int i = 0; i < _pdfPages.Count; i++)
                    {
                        rows.Add(new PdfPageRowViewModel { LeftPage = _pdfPages[i] });
                    }
                }
            }
            else
            {
                // Single-page mode: show only the current page
                if (_isDualPage)
                {
                    int leftIndex = _currentPageIndex % 2 != 0 && _currentPageIndex > 0 ? _currentPageIndex - 1 : _currentPageIndex;
                    rows.Add(new PdfPageRowViewModel
                    {
                        LeftPage = _pdfPages[leftIndex],
                        RightPage = (leftIndex + 1 < _pdfPages.Count) ? _pdfPages[leftIndex + 1] : null
                    });
                }
                else
                {
                    rows.Add(new PdfPageRowViewModel { LeftPage = _pdfPages[_currentPageIndex] });
                }
            }

            _pdfRows = rows;
            PdfPageItemsControl.ItemsSource = _pdfRows;
            UpdatePdfPagesDisplayWidth();
        }

        private const double PdfPageMargin = 4.0;

        private double GetPageOffset(int pageIndex)
        {
            if (_pdfPages == null) return 0;

            double currentY = 0;
            if (_isDualPage)
            {
                int targetRowIndex = pageIndex / 2;
                for (int i = 0; i < targetRowIndex && i < _pdfRows.Count; i++)
                {
                    currentY += _pdfRows[i].DisplayHeight + PdfPageMargin;
                }
            }
            else
            {
                for (int i = 0; i < pageIndex && i < _pdfPages.Count; i++)
                {
                    currentY += _pdfPages[i].DisplayHeight + PdfPageMargin;
                }
            }
            return currentY;
        }

        private double GetCurrentPageHeight()
        {
            if (_pdfPages == null) return 0;
            if (_isDualPage)
            {
                int rowIndex = _currentPageIndex / 2;
                if (rowIndex >= 0 && rowIndex < _pdfRows.Count) return _pdfRows[rowIndex].DisplayHeight;
            }
            else if (_currentPageIndex >= 0 && _currentPageIndex < _pdfPages.Count)
            {
                return _pdfPages[_currentPageIndex].DisplayHeight;
            }
            return 0;
        }

        // Captures the fractional reading position within the current page, runs the reflow
        // (which changes page heights), then restores that same position so a zoom change keeps
        // the view on the page being read.
        private void ReanchorAcrossReflow(Action reflow)
        {
            double oldPageTop = GetPageOffset(_currentPageIndex);
            double oldPageHeight = GetCurrentPageHeight();
            double fraction = oldPageHeight > 0
                ? Math.Max(0, Math.Min(1, (PdfActiveViewer.VerticalOffset - oldPageTop) / oldPageHeight))
                : 0;

            reflow();

            if (_isContinuousScroll)
            {
                double newPageTop = GetPageOffset(_currentPageIndex);
                double newPageHeight = GetCurrentPageHeight();
                PdfActiveViewer.ScrollToVerticalOffset(newPageTop + fraction * newPageHeight);
            }
        }

        private void ScrollToPdfPage(int pageIndex)
        {
            if (_pdfPages == null || pageIndex < 0 || pageIndex >= _pdfPages.Count) return;

            _currentPageIndex = pageIndex;

            if (_isContinuousScroll)
                PdfActiveViewer.ScrollToVerticalOffset(GetPageOffset(pageIndex));
            else
                RebuildPdfRows();

            UpdatePdfViewport();
            UpdatePageIndicator();
        }

        private void UpdatePdfPagesDisplayWidth()
        {
            if (_pdfPages == null) return;
            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            if (availableWidth <= 0) availableWidth = this.Width - (_isSidebarVisible ? 220 : 0);
            if (availableWidth <= 0) availableWidth = 800;

            double displayWidth = PdfLayout.DisplayWidth(availableWidth, 48, _isDualPage, 24, _pdfZoomLevel);

            foreach (var page in _pdfPages)
            {
                page.DisplayWidth = displayWidth;
            }

            if (_pdfRows != null)
            {
                foreach (var row in _pdfRows)
                {
                    row.NotifyDisplaySizeChanged();
                }
            }
        }

        private void UpdatePdfViewport()
        {
            if (_pdfPages == null || _pdfCts == null || _pdfCts.IsCancellationRequested) return;

            double scrollOffset = PdfActiveViewer.VerticalOffset;
            double viewportHeight = PdfActiveViewer.ViewportHeight;
            if (viewportHeight <= 0) viewportHeight = PdfActiveViewer.ActualHeight;
            if (viewportHeight <= 0) viewportHeight = 600;

            double buffer = viewportHeight;
            double topLimit = scrollOffset - buffer;
            double bottomLimit = scrollOffset + viewportHeight + buffer;

            var visiblePageIndices = new HashSet<int>();

            if (_isContinuousScroll)
            {
                double currentY = 0;
                const double pageMargin = 4.0;

                foreach (var row in _pdfRows)
                {
                    double rowHeight = row.DisplayHeight;
                    double rowTop = currentY;
                    double rowBottom = currentY + rowHeight;

                    bool isVisible = (rowBottom >= topLimit) && (rowTop <= bottomLimit);
                    if (isVisible)
                    {
                        if (row.LeftPage != null) visiblePageIndices.Add(row.LeftPage.PageIndex);
                        if (row.RightPage != null) visiblePageIndices.Add(row.RightPage.PageIndex);
                    }

                    currentY = rowBottom + pageMargin;
                }
            }
            else
            {
                foreach (var row in _pdfRows)
                {
                    if (row.LeftPage != null) visiblePageIndices.Add(row.LeftPage.PageIndex);
                    if (row.RightPage != null) visiblePageIndices.Add(row.RightPage.PageIndex);
                }
            }

            _pdfScrollCts?.Cancel();
            _pdfScrollCts = new CancellationTokenSource();
            var scrollToken = _pdfScrollCts.Token;

            foreach (var page in _pdfPages)
            {
                if (visiblePageIndices.Contains(page.PageIndex))
                {
                    if (page.Image == null)
                    {
                        var pageIndex = page.PageIndex;
                        var targetWidth = (uint)page.DisplayWidth;
                        var targetHeight = (uint)page.DisplayHeight;
                        var renderer = _pdfRenderer;

                        if (renderer == null) continue;

                        Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(80, scrollToken);
                                if (scrollToken.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                                var bitmap = await renderer.RenderPageAsync(pageIndex, targetWidth, targetHeight, scrollToken);
                                if (bitmap == null || scrollToken.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (!scrollToken.IsCancellationRequested && !_pdfCts.IsCancellationRequested && (uint)page.DisplayWidth == targetWidth)
                                    {
                                        page.Image = bitmap;
                                    }
                                }));
                            }
                            catch {}
                        }, scrollToken);
                    }
                }
                else
                {
                    page.Image = null;
                }
            }
        }

        // Renders every thumbnail once in page order, in small batches with a yield between
        // them so the UI stays responsive even for very large PDFs. Idempotent: a second call
        // (e.g. on sidebar open) is ignored once a pass has started for the active document.
        private void RenderAllThumbnails()
        {
            if (_thumbnailsStarted) return;
            if (_pdfThumbnails == null || _pdfRenderer == null || _pdfCts.IsCancellationRequested) return;

            _thumbnailsStarted = true;

            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            var token = _thumbnailCts.Token;

            var renderer = _pdfRenderer;
            var thumbnails = _pdfThumbnails;
            const int batchSize = 10;

            Task.Run(async () =>
            {
                for (int index = 0; index < thumbnails.Count; index++)
                {
                    if (token.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                    var thumbnail = thumbnails[index];
                    if (thumbnail.ThumbnailImage != null) continue;

                    try
                    {
                        var bitmap = await renderer.RenderPageAsync(
                            index, 100, (uint)(100 * thumbnail.AspectRatio), token);
                        if (bitmap == null || token.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                thumbnail.ThumbnailImage = bitmap;
                            }
                        }));
                    }
                    catch { }

                    if ((index + 1) % batchSize == 0)
                    {
                        try { await Task.Delay(15, token); } catch { return; }
                    }
                }
            }, token);
        }

        private void UpdatePageIndicator()
        {
            if (_pdfPages == null) return;
            PageIndicatorText.Text = $"Page {(_currentPageIndex + 1)} of {_pdfPages.Count}";
            PrevPageButton.IsEnabled = (_currentPageIndex > 0);
            NextPageButton.IsEnabled = (_currentPageIndex < _pdfPages.Count - 1);
        }

        private void HandlePdfKeyDown(KeyEventArgs e)
        {
            if (_pdfPages == null || _pdfPages.Count == 0) return;

            bool handled = false;
            switch (e.Key)
            {
                case Key.PageDown:
                case Key.Space:
                    NavigateNext();
                    handled = true;
                    break;
                case Key.PageUp:
                case Key.Back:
                    NavigatePrev();
                    handled = true;
                    break;
                case Key.Home:
                    ScrollToPdfPage(0);
                    handled = true;
                    break;
                case Key.End:
                    ScrollToPdfPage(_pdfPages.Count - 1);
                    handled = true;
                    break;
            }
            if (handled) e.Handled = true;
        }

        private void NavigateNext()
        {
            if (_pdfPages == null) return;
            int step = _isDualPage ? 2 : 1;
            int target = _currentPageIndex + step;
            if (target < _pdfPages.Count)
            {
                ScrollToPdfPage(target);
            }
        }

        private void NavigatePrev()
        {
            if (_pdfPages == null) return;
            int step = _isDualPage ? 2 : 1;
            int target = _currentPageIndex - step;
            if (target >= 0)
            {
                ScrollToPdfPage(target);
            }
            else if (_currentPageIndex > 0)
            {
                ScrollToPdfPage(0);
            }
        }

        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarVisible = !_isSidebarVisible;
            if (_isSidebarVisible)
            {
                PdfSidebarColumn.Width = new GridLength(220);
                PdfSidebarBorder.Visibility = Visibility.Visible;
                RenderAllThumbnails();
            }
            else
            {
                PdfSidebarColumn.Width = new GridLength(0);
                PdfSidebarBorder.Visibility = Visibility.Collapsed;
            }
            UpdatePdfPagesDisplayWidth();
            UpdatePdfViewport();
        }

        private void LayoutModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (SinglePageRadio == null || DualPageRadio == null) return;

            _isDualPage = DualPageRadio.IsChecked == true;
            RebuildPdfRows();
            ScrollToPdfPage(_currentPageIndex);
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            _isContinuousScroll = false;
            NavigatePrev();
            Dispatcher.BeginInvoke(new Action(() => FitPageButton_Click(null, null)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            _isContinuousScroll = false;
            NavigateNext();
            Dispatcher.BeginInvoke(new Action(() => FitPageButton_Click(null, null)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyZoom(0.85);
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyZoom(1.15);
        }

        private void FitWidthButton_Click(object sender, RoutedEventArgs e)
        {
            _scaleVisualFactor = 1.0;
            _pdfZoomLevel = 1.0;
            PdfScale.ScaleX = 1.0;
            PdfScale.ScaleY = 1.0;
            _zoomDebounceTimer.Stop();

            ReanchorAcrossReflow(() => UpdatePdfPagesDisplayWidth());
            UpdatePdfViewport();
            ZoomText.Text = "Fit";
        }

        private void FitPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pdfPages == null || _pdfPages.Count == 0) return;

            int pageIndex = Math.Max(0, Math.Min(_currentPageIndex, _pdfPages.Count - 1));
            double aspectRatio = _pdfPages[pageIndex].AspectRatio;
            if (aspectRatio <= 0) return;

            double viewportHeight = PdfActiveViewer.ViewportHeight;
            if (viewportHeight <= 0) viewportHeight = PdfActiveViewer.ActualHeight;
            if (viewportHeight <= 0) viewportHeight = 600;

            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            if (availableWidth <= 0) availableWidth = this.Width - (_isSidebarVisible ? 220 : 0);
            if (availableWidth <= 0) availableWidth = 800;

            double baselineWidth = PdfLayout.DisplayWidth(availableWidth, 48, _isDualPage, 24, 1.0);

            _scaleVisualFactor = 1.0;
            _pdfZoomLevel = PdfLayout.FitPageZoom(viewportHeight, aspectRatio, baselineWidth, 48);
            PdfScale.ScaleX = 1.0;
            PdfScale.ScaleY = 1.0;
            _zoomDebounceTimer.Stop();

            ReanchorAcrossReflow(() => UpdatePdfPagesDisplayWidth());
            UpdatePdfViewport();
            ZoomText.Text = $"{Math.Round(_pdfZoomLevel * 100)}%";
        }

        private void ApplyZoom(double scaleFactor)
        {
            double newFactor = _scaleVisualFactor * scaleFactor;
            if (_pdfZoomLevel * newFactor >= 0.1 && _pdfZoomLevel * newFactor <= 10.0)
            {
                _scaleVisualFactor = newFactor;
                PdfScale.ScaleX = _scaleVisualFactor;
                PdfScale.ScaleY = _scaleVisualFactor;

                _zoomDebounceTimer.Stop();
                _zoomDebounceTimer.Start();
            }
        }

        private void ZoomDebounceTimer_Tick(object sender, EventArgs e)
        {
            _zoomDebounceTimer.Stop();
            _pdfZoomLevel *= _scaleVisualFactor;
            _scaleVisualFactor = 1.0;

            PdfScale.ScaleX = 1.0;
            PdfScale.ScaleY = 1.0;

            ReanchorAcrossReflow(() => UpdatePdfPagesDisplayWidth());
            UpdatePdfViewport();
            ZoomText.Text = $"{Math.Round(_pdfZoomLevel * 100)}%";
        }

        private void PdfActiveViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_pdfPages == null || !_isContinuousScroll) return;

            double offset = PdfActiveViewer.VerticalOffset;
            double currentY = 0;
            const double pageMargin = 24.0;
            int foundIndex = 0;

            if (_isDualPage)
            {
                for (int i = 0; i < _pdfRows.Count; i++)
                {
                    double rowHeight = _pdfRows[i].DisplayHeight;
                    if (offset >= currentY && offset < currentY + rowHeight + pageMargin)
                    {
                        if (_pdfRows[i].LeftPage != null) foundIndex = _pdfRows[i].LeftPage.PageIndex;
                        break;
                    }
                    currentY += rowHeight + pageMargin;
                }
            }
            else
            {
                for (int i = 0; i < _pdfPages.Count; i++)
                {
                    double pageHeight = _pdfPages[i].DisplayHeight;
                    if (offset >= currentY && offset < currentY + pageHeight + pageMargin)
                    {
                        foundIndex = _pdfPages[i].PageIndex;
                        break;
                    }
                    currentY += pageHeight + pageMargin;
                }
            }

            if (foundIndex != _currentPageIndex)
            {
                _currentPageIndex = foundIndex;
                UpdatePageIndicator();
            }

            UpdatePdfViewport();
        }

        private void PdfActiveViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double zoomRatio = e.Delta > 0 ? 1.15 : 0.85;
                ApplyZoom(zoomRatio);
                e.Handled = true;
            }
            else if (!_isContinuousScroll)
            {
                _isContinuousScroll = true;
                RebuildPdfRows();
                PdfActiveViewer.ScrollToVerticalOffset(GetPageOffset(_currentPageIndex));
            }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null && btn.DataContext is PdfThumbnailViewModel thumbnail)
            {
                ScrollToPdfPage(thumbnail.PageIndex);
            }
        }

        private void PdfPageJumpInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecutePageJump();
            }
        }

        private void PdfPageJumpButton_Click(object sender, RoutedEventArgs e)
        {
            ExecutePageJump();
        }

        private void ExecutePageJump()
        {
            if (_pdfPages == null || string.IsNullOrWhiteSpace(PdfPageJumpInput.Text)) return;

            if (int.TryParse(PdfPageJumpInput.Text.Trim(), out int pageNumber))
            {
                int pageIndex = pageNumber - 1;
                if (pageIndex >= 0 && pageIndex < _pdfPages.Count)
                {
                    ScrollToPdfPage(pageIndex);
                    PdfPageJumpInput.Text = string.Empty;
                }
            }
        }
    }

    public class PdfThumbnailViewModel : ViewModelBase
    {
        private BitmapSource _thumbnailImage;

        public int PageIndex { get; }
        public int PageNumber => PageIndex + 1;
        public double AspectRatio { get; }
        public double ThumbnailHeight => 100.0 * AspectRatio;

        public PdfThumbnailViewModel(int pageIndex, double aspectRatio)
        {
            PageIndex = pageIndex;
            AspectRatio = aspectRatio;
        }

        public BitmapSource ThumbnailImage
        {
            get => _thumbnailImage;
            set => SetField(ref _thumbnailImage, value);
        }
    }

    public class PdfPageRowViewModel : ViewModelBase
    {
        private PdfPageViewModel _leftPage;
        private PdfPageViewModel _rightPage;

        public PdfPageViewModel LeftPage
        {
            get => _leftPage;
            set
            {
                if (_leftPage != null) _leftPage.PropertyChanged -= Page_PropertyChanged;
                _leftPage = value;
                if (_leftPage != null) _leftPage.PropertyChanged += Page_PropertyChanged;
            }
        }

        public PdfPageViewModel RightPage
        {
            get => _rightPage;
            set
            {
                if (_rightPage != null) _rightPage.PropertyChanged -= Page_PropertyChanged;
                _rightPage = value;
                if (_rightPage != null) _rightPage.PropertyChanged += Page_PropertyChanged;
            }
        }

        private void Page_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PdfPageViewModel.Image))
            {
                OnPropertyChanged(nameof(LeftPageImage));
                OnPropertyChanged(nameof(RightPageImage));
            }
            else if (e.PropertyName == nameof(PdfPageViewModel.DisplayWidth) || e.PropertyName == nameof(PdfPageViewModel.DisplayHeight))
            {
                OnPropertyChanged(nameof(DisplayHeight));
                OnPropertyChanged(nameof(LeftPageWidth));
                OnPropertyChanged(nameof(RightPageWidth));
            }
        }

        public double DisplayHeight => Math.Max(
            LeftPage != null ? LeftPage.DisplayHeight : 0,
            RightPage != null ? RightPage.DisplayHeight : 0
        );

        public double LeftPageWidth => LeftPage != null ? LeftPage.DisplayWidth : 0;
        public double RightPageWidth => RightPage != null ? RightPage.DisplayWidth : 0;

        public BitmapSource LeftPageImage => LeftPage?.Image;
        public BitmapSource RightPageImage => RightPage?.Image;

        public Visibility RightPageVisibility => RightPage != null ? Visibility.Visible : Visibility.Collapsed;

        public void NotifyDisplaySizeChanged()
        {
            OnPropertyChanged(nameof(DisplayHeight));
            OnPropertyChanged(nameof(LeftPageWidth));
            OnPropertyChanged(nameof(RightPageWidth));
        }
    }
}
