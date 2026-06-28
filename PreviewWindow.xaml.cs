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
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Filey
{
    /// <summary>
    /// Interaction logic for PreviewWindow.xaml
    /// </summary>
    public partial class PreviewWindow : Wpf.Ui.Controls.FluentWindow
    {
        private const bool UseDarkTitleBar = true;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

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

            if (UseDarkTitleBar)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
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
                    switch (rotation)
                    {
                        case Rotation.Rotate90:
                            _rotationAngle = 90;
                            break;
                        case Rotation.Rotate180:
                            _rotationAngle = 180;
                            break;
                        case Rotation.Rotate270:
                            _rotationAngle = 270;
                            break;
                        default:
                            _rotationAngle = 0;
                            break;
                    }
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
            width = 0;
            height = 0;
            rotation = Rotation.Rotate0;
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // DelayCreation avoids decoding pixel data, reading metadata only
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        width = frame.PixelWidth;
                        height = frame.PixelHeight;

                        // Query EXIF orientation with multiple fallback paths to be completely robust
                        var metadata = frame.Metadata as BitmapMetadata;
                        if (metadata != null)
                        {
                            object val = null;
                            if (metadata.ContainsQuery("/System/Photo/Orientation"))
                            {
                                val = metadata.GetQuery("/System/Photo/Orientation");
                            }
                            else if (metadata.ContainsQuery("System.Photo.Orientation"))
                            {
                                val = metadata.GetQuery("System.Photo.Orientation");
                            }
                            else if (metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                            {
                                val = metadata.GetQuery("/app1/ifd/{ushort=274}");
                            }

                            if (val != null)
                            {
                                ushort orientation = Convert.ToUInt16(val);
                                switch (orientation)
                                {
                                    case 3: // Rotate 180
                                        rotation = Rotation.Rotate180;
                                        break;
                                    case 6: // Rotate 90 CW
                                        rotation = Rotation.Rotate90;
                                        int temp = width;
                                        width = height;
                                        height = temp;
                                        break;
                                    case 8: // Rotate 270 CW
                                        rotation = Rotation.Rotate270;
                                        int temp2 = width;
                                        width = height;
                                        height = temp2;
                                        break;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback will leave them as 0/Rotate0
            }
        }

        private void RotateImage()
        {
            _rotationAngle = (_rotationAngle + 90) % 360;
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

                // Subtract padding
                double margin = 16;
                viewportWidth = Math.Max(100, viewportWidth - margin);
                viewportHeight = Math.Max(100, viewportHeight - margin);

                // Check active rotated dimensions
                bool isRotated90or270 = (_rotationAngle == 90 || _rotationAngle == 270);
                double activeImageWidth = isRotated90or270 ? ContentImage.Height : ContentImage.Width;
                double activeImageHeight = isRotated90or270 ? ContentImage.Width : ContentImage.Height;

                double scaleX = viewportWidth / activeImageWidth;
                double scaleY = viewportHeight / activeImageHeight;

                _zoomFactor = Math.Min(scaleX, scaleY);

                ImageScale.ScaleX = _zoomFactor;
                ImageScale.ScaleY = _zoomFactor;
            }
        }

        private string GetEncodingName(Encoding encoding)
        {
            if (encoding == null) return "Unknown";
            if (encoding.Equals(Encoding.UTF8)) return "UTF-8";
            if (encoding.Equals(Encoding.Unicode)) return "UTF-16 LE";
            if (encoding.Equals(Encoding.BigEndianUnicode)) return "UTF-16 BE";
            if (encoding.Equals(Encoding.ASCII)) return "ASCII";
            return encoding.WebName.ToUpper();
        }

        private string GetFormattedFileSize(string filePath)
        {
            try
            {
                long bytes = new FileInfo(filePath).Length;
                if (bytes >= 1073741824)
                    return $"{(bytes / 1073741824.0):N1} GB";
                if (bytes >= 1048576)
                    return $"{(bytes / 1048576.0):N1} MB";
                if (bytes >= 1024)
                    return $"{(bytes / 1024.0):N1} KB";
                return $"{bytes} B";
            }
            catch
            {
                return string.Empty;
            }
        }

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

                double oldZoom = _zoomFactor;
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

                    // Calculate and apply new scrollbar offsets to center zoom on mouse cursor
                    double newHorizontalOffset = (ImageScrollViewer.HorizontalOffset + mousePosInViewport.X) * scaleRatio - mousePosInViewport.X;
                    double newVerticalOffset = (ImageScrollViewer.VerticalOffset + mousePosInViewport.Y) * scaleRatio - mousePosInViewport.Y;

                    ImageScrollViewer.ScrollToHorizontalOffset(newHorizontalOffset);
                    ImageScrollViewer.ScrollToVerticalOffset(newVerticalOffset);
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
        private PdfDocument _activePdfDocument;
        private CancellationTokenSource _pdfCts;
        private CancellationTokenSource _pdfScrollCts;
        private CancellationTokenSource _thumbnailCts;
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
            _activePdfDocument = null;
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
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file);

                    if (token.IsCancellationRequested) return;
                    _activePdfDocument = pdfDoc;

                    uint pageCount = pdfDoc.PageCount;
                    const int batchSize = 20;

                    for (uint i = 0; i < pageCount; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        using (PdfPage page = pdfDoc.GetPage(i))
                        {
                            double aspectRatio = page.Size.Height / page.Size.Width;
                            pages.Add(new PdfPageViewModel((int)i, aspectRatio));
                            thumbnails.Add(new PdfThumbnailViewModel((int)i, aspectRatio));
                        }

                        bool isFirstBatch = (i == (uint)Math.Min(batchSize - 1, (int)pageCount - 1));
                        bool isBatchBoundary = ((i + 1) % batchSize == 0);
                        bool isLast = (i == pageCount - 1);

                        if (isFirstBatch || isBatchBoundary || isLast)
                        {
                            var snapshot = new System.Collections.Generic.List<PdfPageViewModel>(pages);
                            var thumbSnapshot = new System.Collections.Generic.List<PdfThumbnailViewModel>(thumbnails);
                            bool firstBatch = isFirstBatch;

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
                                    ZoomText.Text = "Fit";

                                    if (InitialPdfPageIndex > 0 && InitialPdfPageIndex < _pdfPages.Count)
                                    {
                                        _currentPageIndex = InitialPdfPageIndex;
                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            ScrollToPdfPage(InitialPdfPageIndex);
                                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                                    }
                                    else
                                    {
                                        _currentPageIndex = 0;
                                        UpdatePdfPagesDisplayWidth();
                                        UpdatePdfViewport();
                                    }
                                    UpdatePageIndicator();
                                    UpdateVisibleThumbnails();
                                }
                                else
                                {
                                    UpdatePageIndicator();
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
                    // Group side by side
                    for (int i = 0; i < _pdfPages.Count; i += 2)
                    {
                        var row = new PdfPageRowViewModel
                        {
                            LeftPage = _pdfPages[i],
                            RightPage = (i + 1 < _pdfPages.Count) ? _pdfPages[i + 1] : null
                        };
                        rows.Add(row);
                    }
                }
                else
                {
                    // Single page per row
                    for (int i = 0; i < _pdfPages.Count; i++)
                    {
                        rows.Add(new PdfPageRowViewModel { LeftPage = _pdfPages[i] });
                    }
                }
            }
            else
            {
                // Page flip mode: only show the current page(s)
                if (_isDualPage)
                {
                    int leftIndex = _currentPageIndex;
                    if (leftIndex % 2 != 0 && leftIndex > 0)
                    {
                        leftIndex--;
                    }
                    var row = new PdfPageRowViewModel
                    {
                        LeftPage = _pdfPages[leftIndex],
                        RightPage = (leftIndex + 1 < _pdfPages.Count) ? _pdfPages[leftIndex + 1] : null
                    };
                    rows.Add(row);
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

        private void ScrollToPdfPage(int pageIndex)
        {
            if (_pdfPages == null || pageIndex < 0 || pageIndex >= _pdfPages.Count) return;
            
            _currentPageIndex = pageIndex;

            if (_isContinuousScroll)
            {
                double currentY = 0;
                const double pageMargin = 24.0;

                if (_isDualPage)
                {
                    int targetRowIndex = pageIndex / 2;
                    for (int i = 0; i < targetRowIndex; i++)
                    {
                        currentY += _pdfRows[i].DisplayHeight + pageMargin;
                    }
                }
                else
                {
                    for (int i = 0; i < pageIndex; i++)
                    {
                        currentY += _pdfPages[i].DisplayHeight + pageMargin;
                    }
                }

                PdfActiveViewer.ScrollToVerticalOffset(currentY);
            }
            else
            {
                RebuildPdfRows();
            }

            UpdatePdfViewport();
            UpdatePageIndicator();
            UpdateVisibleThumbnails();
        }

        private void UpdatePdfPagesDisplayWidth()
        {
            if (_pdfPages == null) return;
            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            if (availableWidth <= 0) availableWidth = this.Width - (_isSidebarVisible ? 220 : 0);
            if (availableWidth <= 0) availableWidth = 800;

            double padding = 48;
            double displayWidth;

            if (_isDualPage)
            {
                displayWidth = Math.Max(100, (availableWidth - padding - 24) / 2.0) * _pdfZoomLevel;
            }
            else
            {
                displayWidth = Math.Max(100, availableWidth - padding) * _pdfZoomLevel;
            }

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
                const double pageMargin = 24.0;

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
                        var pdfDoc = _activePdfDocument;

                        if (pdfDoc == null) continue;

                        Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(80, scrollToken);
                                if (scrollToken.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                                using (PdfPage pdfPage = pdfDoc.GetPage((uint)pageIndex))
                                {
                                    var renderOptions = new PdfPageRenderOptions();
                                    renderOptions.DestinationWidth = targetWidth;
                                    renderOptions.DestinationHeight = targetHeight;

                                    using (var stream = new InMemoryRandomAccessStream())
                                    {
                                        await pdfPage.RenderToStreamAsync(stream, renderOptions);
                                        if (scrollToken.IsCancellationRequested || _pdfCts.IsCancellationRequested) return;

                                        var bitmap = new BitmapImage();
                                        bitmap.BeginInit();
                                        bitmap.StreamSource = stream.AsStream();
                                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                        bitmap.EndInit();
                                        bitmap.Freeze();

                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            if (!scrollToken.IsCancellationRequested && !_pdfCts.IsCancellationRequested && page.DisplayWidth == targetWidth)
                                            {
                                                page.Image = bitmap;
                                            }
                                        }));
                                    }
                                }
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

        private void UpdateVisibleThumbnails()
        {
            if (_pdfThumbnails == null || _activePdfDocument == null || _pdfCts.IsCancellationRequested) return;

            double offset = PdfThumbnailScrollViewer.VerticalOffset;
            double viewport = PdfThumbnailScrollViewer.ViewportHeight;
            if (viewport <= 0) viewport = PdfThumbnailScrollViewer.ActualHeight;
            if (viewport <= 0) viewport = 500;

            double buffer = viewport;
            double top = offset - buffer;
            double bottom = offset + viewport + buffer;

            var visibleIndices = new List<int>();
            double currentY = 0;
            const double margin = 12;

            for (int i = 0; i < _pdfThumbnails.Count; i++)
            {
                double itemHeight = _pdfThumbnails[i].ThumbnailHeight + margin;
                if (currentY + itemHeight >= top && currentY <= bottom)
                {
                    visibleIndices.Add(i);
                }
                currentY += itemHeight;
            }

            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            var token = _thumbnailCts.Token;

            var pdfDoc = _activePdfDocument;

            Task.Run(async () =>
            {
                foreach (int index in visibleIndices)
                {
                    if (token.IsCancellationRequested) return;

                    var thumbnail = _pdfThumbnails[index];
                    if (thumbnail.ThumbnailImage != null) continue;

                    try
                    {
                        await Task.Delay(80, token);
                        if (token.IsCancellationRequested) return;

                        using (PdfPage pdfPage = pdfDoc.GetPage((uint)index))
                        {
                            var renderOptions = new PdfPageRenderOptions();
                            renderOptions.DestinationWidth = 100;
                            renderOptions.DestinationHeight = (uint)(100 * thumbnail.AspectRatio);

                            using (var stream = new InMemoryRandomAccessStream())
                            {
                                await pdfPage.RenderToStreamAsync(stream, renderOptions);
                                if (token.IsCancellationRequested) return;

                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = stream.AsStream();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                bitmap.Freeze();

                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (!token.IsCancellationRequested)
                                    {
                                        thumbnail.ThumbnailImage = bitmap;
                                    }
                                }));
                            }
                        }
                    }
                    catch { }
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
                case Key.Down:
                case Key.Right:
                    if (!_isContinuousScroll)
                    {
                        NavigateNext();
                        handled = true;
                    }
                    break;
                case Key.Up:
                case Key.Left:
                    if (!_isContinuousScroll)
                    {
                        NavigatePrev();
                        handled = true;
                    }
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
                UpdateVisibleThumbnails();
            }
            else
            {
                PdfSidebarColumn.Width = new GridLength(0);
                PdfSidebarBorder.Visibility = Visibility.Collapsed;
            }
            UpdatePdfPagesDisplayWidth();
            UpdatePdfViewport();
        }

        private void ViewModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (ContinuousScrollRadio == null || PageFlipRadio == null) return;

            _isContinuousScroll = ContinuousScrollRadio.IsChecked == true;
            RebuildPdfRows();
            ScrollToPdfPage(_currentPageIndex);
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
            NavigatePrev();
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateNext();
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

            UpdatePdfPagesDisplayWidth();
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

            const double vPadding = 48;
            double desiredWidth = Math.Max(100, (viewportHeight - vPadding) / aspectRatio);

            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            if (availableWidth <= 0) availableWidth = this.Width - (_isSidebarVisible ? 220 : 0);
            if (availableWidth <= 0) availableWidth = 800;

            const double padding = 48;
            double baselineWidth = _isDualPage
                ? Math.Max(100, (availableWidth - padding - 24) / 2.0)
                : Math.Max(100, availableWidth - padding);

            _scaleVisualFactor = 1.0;
            _pdfZoomLevel = desiredWidth / baselineWidth;
            PdfScale.ScaleX = 1.0;
            PdfScale.ScaleY = 1.0;
            _zoomDebounceTimer.Stop();

            UpdatePdfPagesDisplayWidth();
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

            UpdatePdfPagesDisplayWidth();
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
        }

        private void PdfThumbnailScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateVisibleThumbnails();
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
