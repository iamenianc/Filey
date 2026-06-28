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
        private string _activePdfPath;
        private PdfDocument _activePdfDocument;
        private CancellationTokenSource _pdfCts;
        private CancellationTokenSource _pdfScrollCts;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _pdfCts?.Cancel();
            _pdfScrollCts?.Cancel();
        }

        private void LoadPdfFile(string filePath)
        {
            _activePdfPath = filePath;
            _activePdfDocument = null;
            ContentTextBox.Visibility = Visibility.Collapsed;
            ImageScrollViewer.Visibility = Visibility.Collapsed;
            PdfActiveViewer.Visibility = Visibility.Visible;

            PathTextBlock.Text = filePath;
            SizeTextBlock.Text = GetFormattedFileSize(filePath);
            Title = $"{Path.GetFileName(filePath)} — Preview";

            _pdfCts = new CancellationTokenSource();
            var token = _pdfCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    var pages = new System.Collections.Generic.List<PdfPageViewModel>();
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file);

                    if (token.IsCancellationRequested) return;
                    _activePdfDocument = pdfDoc;

                    uint pageCount = pdfDoc.PageCount;

                    for (uint i = 0; i < pageCount; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        using (PdfPage page = pdfDoc.GetPage(i))
                        {
                            double width = page.Size.Width;
                            double height = page.Size.Height;
                            double aspectRatio = height / width;
                            pages.Add(new PdfPageViewModel((int)i, aspectRatio));
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath && !token.IsCancellationRequested)
                        {
                            _pdfPages = pages;

                            UpdatePdfPagesDisplayWidth();
                            PdfPageItemsControl.ItemsSource = _pdfPages;

                            if (InitialPdfPageIndex > 0 && InitialPdfPageIndex < _pdfPages.Count)
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    ScrollToPdfPage(InitialPdfPageIndex);
                                }), System.Windows.Threading.DispatcherPriority.Loaded);
                            }
                            else
                            {
                                UpdatePdfViewport();
                            }
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath)
                        {
                            ContentTextBox.Visibility = Visibility.Visible;
                            PdfActiveViewer.Visibility = Visibility.Collapsed;
                            ContentTextBox.Text = $"Error reading PDF file:\n{ex.Message}";
                        }
                    }));
                }
            }, token);
        }

        private void ScrollToPdfPage(int pageIndex)
        {
            if (_pdfPages == null || pageIndex < 0 || pageIndex >= _pdfPages.Count) return;
            
            double currentY = 0;
            const double pageMargin = 24.0;
            for (int i = 0; i < pageIndex; i++)
            {
                currentY += _pdfPages[i].DisplayHeight + pageMargin;
            }
            PdfActiveViewer.ScrollToVerticalOffset(currentY);
            UpdatePdfViewport();
        }

        private void UpdatePdfPagesDisplayWidth()
        {
            if (_pdfPages == null) return;
            double availableWidth = PdfActiveViewer.ViewportWidth;
            if (availableWidth <= 0) availableWidth = PdfActiveViewer.ActualWidth;
            if (availableWidth <= 0) availableWidth = this.Width;
            
            double displayWidth = Math.Max(100, availableWidth - 48);

            foreach (var page in _pdfPages)
            {
                page.DisplayWidth = displayWidth;
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

            double currentY = 0;
            const double pageMargin = 24.0;

            var visiblePageIndices = new HashSet<int>();

            foreach (var page in _pdfPages)
            {
                double pageHeight = page.DisplayHeight;
                double pageTop = currentY;
                double pageBottom = currentY + pageHeight;

                bool isVisible = (pageBottom >= topLimit) && (pageTop <= bottomLimit);
                if (isVisible)
                {
                    visiblePageIndices.Add(page.PageIndex);
                }

                currentY = pageBottom + pageMargin;
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
                                await Task.Delay(100, scrollToken);
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

        private void PdfActiveViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdatePdfViewport();
        }

        private void PdfActiveViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_pdfPages != null)
            {
                UpdatePdfPagesDisplayWidth();
                UpdatePdfViewport();
            }
        }
    }
}
