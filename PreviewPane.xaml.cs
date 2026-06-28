using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Filey
{
    /// <summary>
    /// Interaction logic for PreviewPane.xaml
    /// </summary>
    public partial class PreviewPane : UserControl
    {
        private double _zoomFactor = 1.0;
        private double _rotationAngle = 0;
        private Point _panStart;
        private double _scrollStartH;
        private double _scrollStartV;
        private bool _isPanning;
        private bool _hasManuallyZoomed = false;

        private string _currentFilePath;
        private CancellationTokenSource _cts;
        private int _previewDepth = 1;
        private List<FastNode> _currentRenderedNodes;
        private CancellationTokenSource _scrollCts;

        private List<PdfPageViewModel> _pdfPages;
        private string _activePdfPath;
        private bool _isPdfActive;
        #region Win32 FindFirstFileEx Declarations

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            ref WIN32_FIND_DATA lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr hFindFile, ref WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        private const int FIND_FIRST_EX_LARGE_FETCH = 2;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard,
            FindExInfoBasic,
            FindExInfoMaxInfoLevel
        }

        private enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch,
            FindExSearchLimitToDirectories,
            FindExSearchLimitToDevices,
            FindExSearchMaxSearchOp
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        private struct SimpleDirectoryInfo
        {
            public string Name;
            public string FullPath;
            public System.IO.FileAttributes Attributes;
        }

        private static List<SimpleDirectoryInfo> GetSubDirectoriesWin32(string parentPath)
        {
            var list = new List<SimpleDirectoryInfo>();
            string searchPath = System.IO.Path.Combine(parentPath, "*");
            
            WIN32_FIND_DATA findData = new WIN32_FIND_DATA();
            IntPtr hFind = FindFirstFileEx(
                searchPath,
                FINDEX_INFO_LEVELS.FindExInfoBasic, // Bypasses querying the short file name, reducing metadata sizes
                ref findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH // Enables large-buffer querying, critical for high-latency SMB
            );

            if (hFind != INVALID_HANDLE_VALUE)
            {
                try
                {
                    do
                    {
                        if ((findData.dwFileAttributes & 0x10) != 0) // FILE_ATTRIBUTE_DIRECTORY
                        {
                            string name = findData.cFileName;
                            if (name != "." && name != "..")
                            {
                                list.Add(new SimpleDirectoryInfo
                                {
                                    Name = name,
                                    FullPath = System.IO.Path.Combine(parentPath, name),
                                    Attributes = (System.IO.FileAttributes)findData.dwFileAttributes
                                });
                            }
                        }
                    }
                    while (FindNextFile(hFind, ref findData));
                }
                finally
                {
                    FindClose(hFind);
                }
            }

            return list;
        }

        #endregion

        private static readonly string[] TextExtensions = new[]
        {
            ".txt", ".ini", ".sql", ".cs", ".json", ".md", ".xml", ".log", ".py",
            ".xaml", ".csproj", ".sln", ".config", ".js", ".ts", ".css", ".html",
            ".h", ".cpp", ".c", ".sh", ".bat", ".ps1", ".yml", ".yaml"
        };

        private static readonly string[] ImageExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff"
        };

        public PreviewPane()
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
                _cts?.Cancel();
                DisposePdf();

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

        public void PreviewFile(string filePath)
        {
            // Cancel any pending load immediately
            _cts?.Cancel();
            _cts = null;

            _currentFilePath = filePath;

            // Instantly recycle visual elements to prevent showing stale previews
            RecyclePreview();

            if (string.IsNullOrEmpty(filePath))
            {
                ShowEmptyState("Select a file to preview", "Support for text, code files, and images");
                return;
            }

            if (Directory.Exists(filePath))
            {
                _cts = new CancellationTokenSource();
                var dirToken = _cts.Token;
                EmptyStateBorder.Visibility = Visibility.Collapsed;
                FolderScrollViewer.Visibility = Visibility.Visible;
                DepthSelectorPanel.Visibility = Visibility.Visible;
                if (ScrollPromptTextBlock != null)
                {
                    ScrollPromptTextBlock.Visibility = Visibility.Visible;
                }
                UpdateDepthButtonHighlights();

                Task.Run(() => LoadDirectoryPreviewAsync(filePath, dirToken), dirToken);
                return;
            }

            if (!File.Exists(filePath))
            {
                ShowEmptyState("Select a file to preview", "Support for text, code files, and images");
                return;
            }

            string ext = Path.GetExtension(filePath).ToLower();
            bool isText = Array.Exists(TextExtensions, x => x == ext);
            bool isImage = Array.Exists(ImageExtensions, x => x == ext);
            bool isPdf = ext == ".pdf";

            if (!isText && !isImage && !isPdf)
            {
                ShowEmptyState("Preview not available for this file type.", Path.GetFileName(filePath));
                PathTextBlock.Text = filePath;
                SizeTextBlock.Text = GetFormattedFileSize(filePath);
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            EmptyStateBorder.Visibility = Visibility.Collapsed;

            if (isText)
            {
                Task.Run(() => LoadTextFileAsync(filePath, token), token);
            }
            else if (isImage)
            {
                Task.Run(() => LoadImageFileAsync(filePath, token), token);
            }
            else if (isPdf)
            {
                LoadPdfFile(filePath);
            }
        }

        private void RecyclePreview()
        {
            DisposePdf();

            if (PdfViewerGrid != null)
            {
                PdfViewerGrid.Visibility = Visibility.Collapsed;
                PdfSkimGrid.Visibility = Visibility.Visible;
                PdfActiveViewer.Visibility = Visibility.Collapsed;
                PdfSkimImage0.Source = null;
                PdfSkimImage1.Source = null;
                PdfSkimImage2.Source = null;
                PdfSkimImage3.Source = null;
                PdfPageItemsControl.ItemsSource = null;
            }

            ContentTextBox.Visibility = Visibility.Collapsed;
            ContentTextBox.Text = string.Empty;
            ContentTextBox.ScrollToHome();

            ImageScrollViewer.Visibility = Visibility.Collapsed;
            ContentImage.Source = null;
            _zoomFactor = 1.0;
            _rotationAngle = 0;
            ImageRotation.Angle = 0;
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;
            _hasManuallyZoomed = false;
            ImageScrollViewer.ScrollToTop();
            ImageScrollViewer.ScrollToHorizontalOffset(0);

            if (FolderScrollViewer != null)
            {
                FolderScrollViewer.Visibility = Visibility.Collapsed;
                FolderScrollViewer.ScrollToTop();
                FolderScrollViewer.ScrollToHorizontalOffset(0);
            }

            if (DepthSelectorPanel != null)
            {
                DepthSelectorPanel.Visibility = Visibility.Collapsed;
            }

            if (ScrollPromptTextBlock != null)
            {
                ScrollPromptTextBlock.Visibility = Visibility.Collapsed;
            }

            _currentRenderedNodes = null;

            PathTextBlock.Text = string.Empty;
            EncodingTextBlock.Text = string.Empty;
            SizeTextBlock.Text = string.Empty;
        }

        private void ShowEmptyState(string mainText, string subText)
        {
            RecyclePreview();
            EmptyStateMainText.Text = mainText;
            EmptyStateSubText.Text = subText;
            EmptyStateBorder.Visibility = Visibility.Visible;
        }

        private void FolderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (e.Delta > 0)
                {
                    ChangeDepth(Math.Min(10, _previewDepth + 1));
                }
                else if (e.Delta < 0)
                {
                    ChangeDepth(Math.Max(1, _previewDepth - 1));
                }
            }
            else
            {
                // Custom faster scroll amount per wheel tick (120px is ~6.5 lines)
                e.Handled = true;
                double scrollOffset = FolderScrollViewer.VerticalOffset - (e.Delta > 0 ? 120 : -120);
                FolderScrollViewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(FolderScrollViewer.ScrollableHeight, scrollOffset)));
            }
        }

        private void FolderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_currentRenderedNodes != null && (e.VerticalChange != 0 || e.ViewportHeightChange != 0))
            {
                // Debounce/Throttle redraw to avoid doing it for every single tick of the wheel
                _scrollCts?.Cancel();
                _scrollCts = new CancellationTokenSource();
                var token = _scrollCts.Token;

                var verticalOffset = FolderScrollViewer.VerticalOffset;
                var viewportHeight = FolderScrollViewer.ViewportHeight;

                Task.Run(async () =>
                {
                    try
                    {
                        // Wait 30ms to debounce scroll ticks
                        await Task.Delay(30, token);
                        if (token.IsCancellationRequested) return;

                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            FolderTreeVisualHost.RenderTree(_currentRenderedNodes, verticalOffset, viewportHeight);
                        }));
                    }
                    catch (TaskCanceledException) { }
                }, token);
            }
        }

        private void DepthButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int targetDepth))
            {
                ChangeDepth(targetDepth);
            }
        }

        private void ChangeDepth(int newDepth)
        {
            if (_previewDepth == newDepth) return;
            _previewDepth = newDepth;

            UpdateDepthButtonHighlights();

            if (!string.IsNullOrEmpty(_currentFilePath) && Directory.Exists(_currentFilePath))
            {
                PreviewFile(_currentFilePath);
            }
        }

        private void UpdateDepthButtonHighlights()
        {
            var activeBrush = new SolidColorBrush(Color.FromRgb(234, 179, 8)); // Premium Gold/Amber accent
            var inactiveBrush = new SolidColorBrush(Color.FromRgb(133, 133, 133)); // Muted gray

            if (DepthButton1 != null) DepthButton1.Foreground = (_previewDepth == 1) ? activeBrush : inactiveBrush;
            if (DepthButton2 != null) DepthButton2.Foreground = (_previewDepth == 2) ? activeBrush : inactiveBrush;
            if (DepthButton3 != null) DepthButton3.Foreground = (_previewDepth == 3) ? activeBrush : inactiveBrush;
            if (DepthButton4 != null) DepthButton4.Foreground = (_previewDepth == 4) ? activeBrush : inactiveBrush;
            if (DepthButton5 != null) DepthButton5.Foreground = (_previewDepth == 5) ? activeBrush : inactiveBrush;
            if (DepthButton6 != null) DepthButton6.Foreground = (_previewDepth == 6) ? activeBrush : inactiveBrush;
            if (DepthButton7 != null) DepthButton7.Foreground = (_previewDepth == 7) ? activeBrush : inactiveBrush;
            if (DepthButton8 != null) DepthButton8.Foreground = (_previewDepth == 8) ? activeBrush : inactiveBrush;
            if (DepthButton9 != null) DepthButton9.Foreground = (_previewDepth == 9) ? activeBrush : inactiveBrush;
            if (DepthButton10 != null) DepthButton10.Foreground = (_previewDepth == 10) ? activeBrush : inactiveBrush;
        }

        private async void LoadDirectoryPreviewAsync(string folderPath, CancellationToken token)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;

                var nodes = new List<FastNode>();
                double[] currentY = new double[] { 10.0 };
                double rowHeight        = 18.50; // Explicit spacing
                double indentWidth      = 40;
                double historyIndent    = 20;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                bool[] wasLimited = new bool[] { false };

                // Collect the entire hierarchy of parent paths leading to this folder
                var historyPaths = new List<string>();
                try
                {
                    var currentDirInfo = new DirectoryInfo(folderPath);
                    var tempParent = currentDirInfo.Parent;
                    while (tempParent != null)
                    {
                        historyPaths.Insert(0, tempParent.FullName);
                        tempParent = tempParent.Parent;
                    }
                }
                catch
                {
                    // Ignore path parsing errors
                }

                int activeRootLevel = historyPaths.Count;

                // Render parent history nodes with tight 6px indentation
                for (int i = 0; i < historyPaths.Count; i++)
                {
                    var path = historyPaths[i];
                    var di = new DirectoryInfo(path);
                    string displayName = (di.Parent != null) ? di.Name : path;

                    nodes.Add(new FastNode
                    {
                        Name = displayName,
                        Level = i,
                        X = i * historyIndent,
                        Y = currentY[0],
                        IsFile = false,
                        IsPlaceholder = false,
                        IsHistory = true
                    });
                    currentY[0] += rowHeight;
                }

                // Start recursive collection on the background thread
                string rootName = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(rootName)) rootName = folderPath;
                await BuildFastTreeAsync(folderPath, rootName, activeRootLevel, activeRootLevel, historyIndent, indentWidth, currentY, rowHeight, nodes, stopwatch, wasLimited, token);

                if (wasLimited[0])
                {
                    nodes.Add(new FastNode
                    {
                        Name = "... Preview limited (10,000 folders limit reached) ...",
                        Level = activeRootLevel + 1,
                        X = (activeRootLevel + 1) * indentWidth,
                        Y = currentY[0],
                        IsFile = false,
                        IsPlaceholder = true
                    });
                    currentY[0] += rowHeight;
                }

                int activeFileCount = 0;
                try
                {
                    if (DirectoryViewModel.ShowHidden)
                    {
                        activeFileCount = Directory.GetFiles(folderPath).Length;
                    }
                    else
                    {
                        var dirInfo = new DirectoryInfo(folderPath);
                        foreach (var file in dirInfo.EnumerateFiles())
                        {
                            if (token.IsCancellationRequested) return;
                            if ((file.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) == 0)
                            {
                                activeFileCount++;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore access permissions
                }

                if (token.IsCancellationRequested) return;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;

                    EmptyStateBorder.Visibility = Visibility.Collapsed;
                    FolderScrollViewer.Visibility = Visibility.Visible;
                    DepthSelectorPanel.Visibility = Visibility.Visible;
                    if (ScrollPromptTextBlock != null)
                    {
                        ScrollPromptTextBlock.Visibility = Visibility.Visible;
                    }
                    UpdateDepthButtonHighlights();

                    _currentRenderedNodes = nodes;
                    FolderTreeVisualHost.RenderTree(nodes, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
                    FolderTreeVisualHost.Height = currentY[0] + 50; // Set explicit size for ScrollViewer bounds
                    FolderTreeVisualHost.Width = 800; 

                    PathTextBlock.Text = folderPath;
                    EncodingTextBlock.Text = $"Directory Tree ({_previewDepth} Level{(_previewDepth == 1 ? "" : "s")})";
                    
                    int folderCount = nodes.Count - activeRootLevel;
                    SizeTextBlock.Text = $"{folderCount} folder{(folderCount == 1 ? "" : "s")} • {activeFileCount} file{(activeFileCount == 1 ? "" : "s")}";
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowEmptyState("Error reading directory", ex.Message);
                    PathTextBlock.Text = folderPath;
                }));
            }
        }

        private async Task BuildFastTreeAsync(string dirPath, string dirName, int level, int activeRootLevel, double historyIndent, double indent, double[] currentY, double rowHeight, List<FastNode> list, System.Diagnostics.Stopwatch stopwatch, bool[] wasLimited, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            // Guardrail limit of 10,000 folders
            if (list.Count >= 10000)
            {
                wasLimited[0] = true;
                return;
            }

            // Calculate X coordinate based on whether it is history or active tree
            double xCoord = (activeRootLevel * historyIndent) + (level - activeRootLevel) * indent;

            // Add the directory node
            list.Add(new FastNode
            {
                Name = dirName,
                FullPath = dirPath,
                Level = level,
                X = xCoord,
                Y = currentY[0],
                IsFile = false,
                IsPlaceholder = false,
                IsHistory = false,
                FileCount = -1
            });
            currentY[0] += rowHeight;

            // Progressive batch sizes to keep UI thread responsive while avoiding sudden jumps
            int batchSize = 200;
            if (list.Count > 5000) batchSize = 2000;
            else if (list.Count > 1000) batchSize = 1000;

            if (list.Count % batchSize == 0)
            {
                var currentSnapshot = new List<FastNode>(list);
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                        _currentRenderedNodes = currentSnapshot;
                        FolderTreeVisualHost.RenderTree(currentSnapshot, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
                    }));

                // Delay rate: 100ms (10 batches/sec) for all levels
                await Task.Delay(100, token);
            }

            // Depth limit check (explore exactly _previewDepth levels under the active folder)
            if (level >= activeRootLevel + _previewDepth) return;

            try
            {
                var allSubDirs = GetSubDirectoriesWin32(dirPath);
                var subDirsList = new List<SimpleDirectoryInfo>();

                // Pre-filter hidden/system directories if ShowHidden is false
                foreach (var sd in allSubDirs)
                {
                    if (!DirectoryViewModel.ShowHidden)
                    {
                        if ((sd.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                        {
                            continue;
                        }
                    }
                    subDirsList.Add(sd);
                }

                for (int i = 0; i < subDirsList.Count; i++)
                {
                    if (token.IsCancellationRequested) return;

                    var subDir = subDirsList[i];
                    await BuildFastTreeAsync(subDir.FullPath, subDir.Name, level + 1, activeRootLevel, historyIndent, indent, currentY, rowHeight, list, stopwatch, wasLimited, token);
                }
            }
            catch (Exception)
            {
                // Bypass folder read restrictions
            }
        }





        private void LoadTextFileAsync(string filePath, CancellationToken token)
        {
            try
            {
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

                if (token.IsCancellationRequested) return;

                if (isTruncated)
                {
                    content += "\r\n\r\n[... Preview truncated due to large file size ...]";
                }

                string sizeStr = GetFormattedFileSize(filePath);
                if (isTruncated) sizeStr += " (Truncated)";
                string encodingStr = GetEncodingName(encoding);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;

                    RecyclePreview();
                    EmptyStateBorder.Visibility = Visibility.Collapsed;
                    ContentTextBox.Visibility = Visibility.Visible;
                    ContentTextBox.Text = content;

                    PathTextBlock.Text = filePath;
                    EncodingTextBlock.Text = encodingStr;
                    SizeTextBlock.Text = sizeStr;
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowEmptyState("Error reading text file", ex.Message);
                    PathTextBlock.Text = filePath;
                }));
            }
        }

        private void LoadImageFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                GetOriginalImageDimensions(filePath, out int originalWidth, out int originalHeight, out Rotation rotation);

                if (token.IsCancellationRequested) return;

                // Load with low-resolution decoding (800px wide) optimized for inline preview
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 800; // Aggressive downsampling!
                bitmap.UriSource = new Uri(filePath);
                bitmap.EndInit();

                // Wait for download if necessary (local files load instantly)
                if (bitmap.IsDownloading)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler completedHandler = (s, e) => tcs.TrySetResult(true);
                    EventHandler<ExceptionEventArgs> failedHandler = (s, e) => tcs.TrySetException(e.ErrorException);

                    bitmap.DownloadCompleted += completedHandler;
                    bitmap.DownloadFailed += failedHandler;

                    tcs.Task.Wait(token);

                    bitmap.DownloadCompleted -= completedHandler;
                    bitmap.DownloadFailed -= failedHandler;
                }

                if (token.IsCancellationRequested) return;

                bitmap.Freeze(); // Freezing allows cross-thread access!

                double initialRotation = 0;
                switch (rotation)
                {
                    case Rotation.Rotate90: initialRotation = 90; break;
                    case Rotation.Rotate180: initialRotation = 180; break;
                    case Rotation.Rotate270: initialRotation = 270; break;
                }

                string sizeStr = GetFormattedFileSize(filePath);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;

                    RecyclePreview();
                    EmptyStateBorder.Visibility = Visibility.Collapsed;
                    ImageScrollViewer.Visibility = Visibility.Visible;

                    ContentImage.Width = originalWidth > 0 ? originalWidth : 800;
                    ContentImage.Height = originalHeight > 0 ? originalHeight : 600;

                    _rotationAngle = initialRotation;
                    ImageRotation.Angle = _rotationAngle;

                    ContentImage.Source = bitmap;

                    PathTextBlock.Text = filePath;
                    EncodingTextBlock.Text = originalWidth > 0 && originalHeight > 0 ? $"{originalWidth} × {originalHeight} px" : "Image";
                    SizeTextBlock.Text = sizeStr;

                    // Fit to viewport
                    FitImageToWindow();
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowEmptyState("Error loading image", ex.Message);
                    PathTextBlock.Text = filePath;
                }));
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
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        width = frame.PixelWidth;
                        height = frame.PixelHeight;

                        var metadata = frame.Metadata as BitmapMetadata;
                        if (metadata != null)
                        {
                            object val = null;
                            if (metadata.ContainsQuery("/System/Photo/Orientation"))
                                val = metadata.GetQuery("/System/Photo/Orientation");
                            else if (metadata.ContainsQuery("System.Photo.Orientation"))
                                val = metadata.GetQuery("System.Photo.Orientation");
                            else if (metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                                val = metadata.GetQuery("/app1/ifd/{ushort=274}");

                            if (val != null)
                            {
                                ushort orientation = Convert.ToUInt16(val);
                                switch (orientation)
                                {
                                    case 3:
                                        rotation = Rotation.Rotate180;
                                        break;
                                    case 6:
                                        rotation = Rotation.Rotate90;
                                        int temp = width;
                                        width = height;
                                        height = temp;
                                        break;
                                    case 8:
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
                // Fallback leaves them as 0/Rotate0
            }
        }

        private void RotateImage()
        {
            _rotationAngle = (_rotationAngle + 90) % 360;
            ImageRotation.Angle = _rotationAngle;

            GetOriginalImageDimensions(PathTextBlock.Text, out int w, out int h, out Rotation r);
            if (_rotationAngle == 90 || _rotationAngle == 270)
            {
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
        }

        private void FitImageToWindow()
        {
            if (ContentImage.Source != null && ImageScrollViewer.ActualWidth > 0 && ImageScrollViewer.ActualHeight > 0)
            {
                double viewportWidth = ImageScrollViewer.ViewportWidth > 0 ? ImageScrollViewer.ViewportWidth : ImageScrollViewer.ActualWidth;
                double viewportHeight = ImageScrollViewer.ViewportHeight > 0 ? ImageScrollViewer.ViewportHeight : ImageScrollViewer.ActualHeight;

                double margin = 16;
                viewportWidth = Math.Max(100, viewportWidth - margin);
                viewportHeight = Math.Max(100, viewportHeight - margin);

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
                        ContentTextBox.FontSize += 1;
                }
                else if (e.Delta < 0)
                {
                    if (ContentTextBox.FontSize > 6)
                        ContentTextBox.FontSize -= 1;
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

        private void DisposePdf()
        {
            _scrollCts?.Cancel();
            _scrollCts = null;
            _pdfPages = null;
            _activePdfPath = null;
            _isPdfActive = false;
        }

        private void LoadPdfFile(string filePath)
        {
            _activePdfPath = filePath;
            _isPdfActive = false;

            EmptyStateBorder.Visibility = Visibility.Collapsed;
            PdfViewerGrid.Visibility = Visibility.Visible;
            PdfSkimGrid.Visibility = Visibility.Visible;
            PdfActiveViewer.Visibility = Visibility.Collapsed;
            PdfSkimGrid.Focus();

            PathTextBlock.Text = filePath;
            SizeTextBlock.Text = GetFormattedFileSize(filePath);

            var cached = PdfSkimCache.Get(filePath);
            if (cached != null)
            {
                PdfSkimImage0.Source = cached.Length > 0 ? cached[0] : null;
                PdfSkimImage1.Source = cached.Length > 1 ? cached[1] : null;
                PdfSkimImage2.Source = cached.Length > 2 ? cached[2] : null;
                PdfSkimImage3.Source = cached.Length > 3 ? cached[3] : null;
                return;
            }

            var token = _cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file);

                    uint pageCount = pdfDoc.PageCount;
                    int count = Math.Min(4, (int)pageCount);
                    var images = new BitmapSource[count];

                    for (int i = 0; i < count; i++)
                    {
                        if (token.IsCancellationRequested) return;

                        using (PdfPage page = pdfDoc.GetPage((uint)i))
                        {
                            var originalSize = page.Size;

                            double scale = Math.Min(300.0 / originalSize.Width, 300.0 / originalSize.Height);
                            uint thumbWidth = (uint)Math.Max(1, originalSize.Width * scale);
                            uint thumbHeight = (uint)Math.Max(1, originalSize.Height * scale);

                            var renderOptions = new PdfPageRenderOptions();
                            renderOptions.DestinationWidth = thumbWidth;
                            renderOptions.DestinationHeight = thumbHeight;

                            using (var stream = new InMemoryRandomAccessStream())
                            {
                                await page.RenderToStreamAsync(stream, renderOptions);
                                if (token.IsCancellationRequested) return;

                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = stream.AsStream();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                bitmap.Freeze();

                                images[i] = bitmap;
                            }
                        }
                    }

                    if (token.IsCancellationRequested) return;

                    PdfSkimCache.Add(filePath, images);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath && !token.IsCancellationRequested && !_isPdfActive)
                        {
                            PdfSkimImage0.Source = images.Length > 0 ? images[0] : null;
                            PdfSkimImage1.Source = images.Length > 1 ? images[1] : null;
                            PdfSkimImage2.Source = images.Length > 2 ? images[2] : null;
                            PdfSkimImage3.Source = images.Length > 3 ? images[3] : null;
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath)
                        {
                            ShowEmptyState("Error reading PDF file.", ex.Message);
                        }
                    }));
                }
            }, token);
        }

        private void ActivatePdfReader()
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
                    StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                    PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file);

                    uint pageCount = pdfDoc.PageCount;
                    var pages = new List<PdfPageViewModel>();

                    for (uint i = 0; i < pageCount; i++)
                    {
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
                            ShowEmptyState("Error opening PDF viewer.", ex.Message);
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
            double displayWidth = Math.Max(100, availableWidth - 48);

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

            _scrollCts?.Cancel();
            _scrollCts = new CancellationTokenSource();
            var scrollToken = _scrollCts.Token;

            var filePath = _activePdfPath;

            foreach (var page in _pdfPages)
            {
                if (visiblePageIndices.Contains(page.PageIndex))
                {
                    if (page.Image == null)
                    {
                        var pageIndex = page.PageIndex;
                        var targetWidth = (uint)page.DisplayWidth;
                        var targetHeight = (uint)page.DisplayHeight;

                        Task.Run(async () =>
                        {
                            if (scrollToken.IsCancellationRequested || _cts.IsCancellationRequested) return;

                            try
                            {
                                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                                PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(file);

                                using (PdfPage pdfPage = pdfDoc.GetPage((uint)pageIndex))
                                {
                                    var renderOptions = new PdfPageRenderOptions();
                                    renderOptions.DestinationWidth = targetWidth;
                                    renderOptions.DestinationHeight = targetHeight;

                                    using (var stream = new InMemoryRandomAccessStream())
                                    {
                                        await pdfPage.RenderToStreamAsync(stream, renderOptions);
                                        if (scrollToken.IsCancellationRequested || _cts.IsCancellationRequested) return;

                                        var bitmap = new BitmapImage();
                                        bitmap.BeginInit();
                                        bitmap.StreamSource = stream.AsStream();
                                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                        bitmap.EndInit();
                                        bitmap.Freeze();

                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            if (!scrollToken.IsCancellationRequested && !_cts.IsCancellationRequested && page.DisplayWidth == targetWidth)
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

        private void PdfSkimGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            ActivatePdfReader();
        }

        private void PdfSkimGrid_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ActivatePdfReader();
        }

        private void PdfSkimGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ActivatePdfReader();
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

        private void PopOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

            var previewWindow = new PreviewWindow(_currentFilePath)
            {
                Owner = Window.GetWindow(this)
            };

            if (_isPdfActive && _pdfPages != null && PdfActiveViewer != null)
            {
                double scrollOffset = PdfActiveViewer.VerticalOffset;
                double currentY = 0;
                const double pageMargin = 24.0;
                int activeIndex = 0;
                
                foreach (var page in _pdfPages)
                {
                    double pageHeight = page.DisplayHeight;
                    if (scrollOffset >= currentY && scrollOffset <= currentY + pageHeight + pageMargin)
                    {
                        activeIndex = page.PageIndex;
                        break;
                    }
                    currentY += pageHeight + pageMargin;
                }
                
                previewWindow.InitialPdfPageIndex = activeIndex;
            }

            previewWindow.Show();
        }
    }

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

        public double DisplayHeight => DisplayWidth * AspectRatio;
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
}
