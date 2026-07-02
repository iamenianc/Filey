using System;
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
    public partial class ImagePreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // unused

        private double _zoomFactor = 1.0;
        private double _rotationAngle = 0;
        private Point _panStart;
        private double _scrollStartH;
        private double _scrollStartV;
        private bool _isPanning;
        private bool _hasManuallyZoomed = false;
        private string _currentFilePath;

        public ImagePreview()
        {
            InitializeComponent();

            this.SizeChanged += (s, e) =>
            {
                if (ImageScrollViewer.Visibility == Visibility.Visible && !_hasManuallyZoomed)
                {
                    FitImageToWindow();
                }
            };

            this.Loaded += (s, e) =>
            {
                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    parentWindow.KeyDown += ParentWindow_KeyDown;
                }
            };

            this.Unloaded += (s, e) =>
            {
                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    parentWindow.KeyDown -= ParentWindow_KeyDown;
                }
            };
        }

        private void ParentWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (this.Visibility == Visibility.Visible && ImageScrollViewer.Visibility == Visibility.Visible && e.Key == Key.R)
            {
                RotateImage();
                e.Handled = true;
            }
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _currentFilePath = filePath;
            Task.Run(() => LoadImageFileAsync(filePath, token), token);
        }

        public void Unload()
        {
            ContentImage.Source = null;
            _zoomFactor = 1.0;
            _rotationAngle = 0;
            ImageRotation.Angle = 0;
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;
            _hasManuallyZoomed = false;
            ImageScrollViewer.ScrollToTop();
            ImageScrollViewer.ScrollToHorizontalOffset(0);
        }

        private void LoadImageFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                GetOriginalImageDimensions(filePath, out int originalWidth, out int originalHeight, out Rotation rotation);

                if (token.IsCancellationRequested) return;

                double containerWidth = 800;
                Dispatcher.Invoke(() =>
                {
                    if (ImageScrollViewer.ActualWidth > 0)
                        containerWidth = ImageScrollViewer.ActualWidth;
                });

                if (token.IsCancellationRequested) return;

                var bitmap = ImageView.LoadOptimizedImage(filePath, containerWidth, 0);
                if (bitmap == null || token.IsCancellationRequested) return;

                double initialRotation = ImageView.AngleFor(rotation);
                string sizeStr = GetFormattedFileSize(filePath);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;

                    ContentImage.Width = originalWidth > 0 ? originalWidth : 800;
                    ContentImage.Height = originalHeight > 0 ? originalHeight : 600;

                    _rotationAngle = initialRotation;
                    ImageRotation.Angle = _rotationAngle;

                    ContentImage.Source = bitmap;

                    string encodingText = originalWidth > 0 && originalHeight > 0 ? $"{originalWidth} × {originalHeight} px" : "Image";
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(encodingText, sizeStr));

                    FitImageToWindow();
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error", ex.Message));
                }));
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

            GetOriginalImageDimensions(_currentFilePath, out int w, out int h, out Rotation r);
            string encodingText;
            if (_rotationAngle == 90 || _rotationAngle == 270)
            {
                encodingText = w > 0 && h > 0 ? $"{h} × {w} px" : "Image";
            }
            else
            {
                encodingText = w > 0 && h > 0 ? $"{w} × {h} px" : "Image";
            }
            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(encodingText, GetFormattedFileSize(_currentFilePath)));

            FitImageToWindow();
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

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);

        private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _hasManuallyZoomed = true;

                double scaleRatio = 1.15;

                if (e.Delta > 0)
                {
                    if (_zoomFactor < 15.0)
                        _zoomFactor *= scaleRatio;
                    else
                        scaleRatio = 1.0;
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
                    Point mousePosInViewport = e.GetPosition(ImageScrollViewer);

                    ImageScale.ScaleX = _zoomFactor;
                    ImageScale.ScaleY = _zoomFactor;

                    ImageScrollViewer.UpdateLayout();

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

                GetOriginalImageDimensions(_currentFilePath, out int w, out int h, out Rotation r);
                string encodingText = w > 0 && h > 0 ? $"{w} × {h} px" : "Image";
                StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(encodingText, GetFormattedFileSize(_currentFilePath)));

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
    }
}
