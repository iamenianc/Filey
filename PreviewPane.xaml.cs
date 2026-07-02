using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using System.Data;
using ExcelDataReader;

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

        public event EventHandler<string> DirectoryClicked;

        private List<PdfPageViewModel> _pdfPages;
        private string _activePdfPath;
        private PdfRenderer _pdfRenderer;
        private bool _isPdfActive;
        private readonly Dictionary<int, (CancellationTokenSource Cts, uint Width)> _activePageRenders = new Dictionary<int, (CancellationTokenSource Cts, uint Width)>();

        private const double PdfPageMargin = 24.0;

        private WebView2 _webView;
        private string _pendingHtml;
        private static readonly string[] TextExtensions = new[]
        {
            ".txt", ".ini", ".sql", ".cs", ".json", ".xml", ".log", ".py",
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

            FolderTreeVisualHost.MouseLeftButtonUp += FolderTreeVisualHost_MouseLeftButtonUp;
            FolderTreeVisualHost.MouseMove += FolderTreeVisualHost_MouseMove;

            this.Loaded += (s, e) =>
            {
                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    parentWindow.KeyDown += ParentWindow_KeyDown;
                }
                ThemeService.ThemeChanged += OnThemeChanged;
            };

            this.Unloaded += (s, e) =>
            {
                _cts?.Cancel();
                DisposePdf();
                DisposeWebView();
                ThemeService.ThemeChanged -= OnThemeChanged;

                var parentWindow = Window.GetWindow(this);
                if (parentWindow != null)
                {
                    parentWindow.KeyDown -= ParentWindow_KeyDown;
                }
            };
        }

        /// <summary>When the theme changes while a markdown file is being previewed, re-render it so
        /// the WebView2 content matches the new Light/Dark palette.</summary>
        private void OnThemeChanged()
        {
            if (WebViewHost.Visibility == Visibility.Visible
                && !string.IsNullOrEmpty(_currentFilePath)
                && Path.GetExtension(_currentFilePath).ToLower() == ".md")
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                Task.Run(() => LoadMarkdownAsync(_currentFilePath, token), token);
            }
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

            if (ext == ".md")
            {
                _cts = new CancellationTokenSource();
                var mdToken = _cts.Token;
                PathTextBlock.Text = filePath;
                SizeTextBlock.Text = GetFormattedFileSize(filePath);
                EmptyStateBorder.Visibility = Visibility.Collapsed;
                Task.Run(() => LoadMarkdownAsync(filePath, mdToken), mdToken);
                return;
            }

            bool isText = Array.Exists(TextExtensions, x => x == ext);
            bool isImage = Array.Exists(ImageExtensions, x => x == ext);
            bool isPdf = ext == ".pdf";
            bool isExcel = ext == ".xlsx" || ext == ".xls" || ext == ".xlsm" || ext == ".xlsb";

            if (!isText && !isImage && !isPdf && !isExcel)
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
            else if (isExcel)
            {
                Task.Run(() => LoadExcelFileAsync(filePath, token), token);
            }
        }

        private async Task LoadMarkdownAsync(string filePath, CancellationToken token)
        {
            string html;
            try
            {
                string markdown = await Task.Run(() => File.ReadAllText(filePath), token);
                if (token.IsCancellationRequested) return;
                var blocks = MarkdownParser.Parse(markdown);
                bool dark = ThemeService.IsDark;
                html = MarkdownRenderer.RenderToHtml(blocks, dark);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                    ShowEmptyState("Error rendering markdown", ex.Message)));
                return;
            }

            if (token.IsCancellationRequested) return;

            await Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (token.IsCancellationRequested || _currentFilePath != filePath) return;
                await ShowHtmlInWebViewAsync(html);
            }));
        }

        private async Task ShowHtmlInWebViewAsync(string html)
        {
            WebViewHost.Visibility = Visibility.Visible;

            if (_webView == null)
            {
                _webView = new WebView2();
                GC.SuppressFinalize(_webView);
                WebViewHost.Children.Add(_webView);
                _pendingHtml = html;
                try
                {
                    await _webView.EnsureCoreWebView2Async(null);
                }
                catch (Exception ex)
                {
                    ShowEmptyState("WebView2 unavailable", ex.Message);
                    return;
                }

                if (_webView == null || _webView.CoreWebView2 == null) return;

                if (_pendingHtml != null)
                {
                    try
                    {
                        _webView.CoreWebView2.NavigateToString(_pendingHtml);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
                    {
                    }
                    _pendingHtml = null;
                }
                return;
            }

            if (_webView.CoreWebView2 == null)
            {
                _pendingHtml = html;
                return;
            }

            try
            {
                _webView.CoreWebView2.NavigateToString(html);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            {
            }
        }

        private void DisposeWebView()
        {
            _pendingHtml = null;
            if (_webView != null)
            {
                WebViewHost.Children.Remove(_webView);
                _webView.Dispose();
                _webView = null;
            }
            if (WebViewHost != null)
            {
                WebViewHost.Visibility = Visibility.Collapsed;
            }
        }

        private void RecyclePreview()
        {
            DisposePdf();
            DisposeWebView();

            if (SpreadsheetViewerGrid != null)
            {
                SpreadsheetViewerGrid.Visibility = Visibility.Collapsed;
                SpreadsheetTabControl.Items.Clear();
                SpreadsheetPaneHost.Content = null;
            }

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

            System.Threading.Tasks.Task.Run(() => MemoryManager.ReleaseUnusedMemory());
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
                    ChangeDepth(Math.Min(6, _previewDepth + 1));
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
            var activeBrush = ThemeService.Brush("AppGoldAccentBrush");
            var inactiveBrush = ThemeService.Brush("AppTextMutedBrush");

            if (DepthButton1 != null) DepthButton1.Foreground = (_previewDepth == 1) ? activeBrush : inactiveBrush;
            if (DepthButton2 != null) DepthButton2.Foreground = (_previewDepth == 2) ? activeBrush : inactiveBrush;
            if (DepthButton3 != null) DepthButton3.Foreground = (_previewDepth == 3) ? activeBrush : inactiveBrush;
            if (DepthButton4 != null) DepthButton4.Foreground = (_previewDepth == 4) ? activeBrush : inactiveBrush;
            if (DepthButton5 != null) DepthButton5.Foreground = (_previewDepth == 5) ? activeBrush : inactiveBrush;
            if (DepthButton6 != null) DepthButton6.Foreground = (_previewDepth == 6) ? activeBrush : inactiveBrush;
        }

        private async void LoadDirectoryPreviewAsync(string folderPath, CancellationToken token)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return;

                var nodes = new List<FastNode>();
                double[] currentY = new double[] { 10.0 };
                double rowHeight        = 18.50; // Explicit spacing
                double indentWidth      = 30;
                double historyIndent    = 15;

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
                        FullPath = path,
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
                using var semaphore = new SemaphoreSlim(8);
                await BuildFastTreeAsync(folderPath, rootName, activeRootLevel, activeRootLevel, historyIndent, indentWidth, currentY, rowHeight, nodes, stopwatch, wasLimited, token, semaphore);

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

                int immediateChildFolderCount = 0;
                int childFileCount = 0;
                try
                {
                    var immediateSubDirs = NativeDirectoryEnumerator.GetSubDirectories(folderPath);
                    foreach (var sd in immediateSubDirs)
                    {
                        if (token.IsCancellationRequested) return;
                        if (!DirectoryViewModel.ShowHidden &&
                            (sd.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) != 0)
                        {
                            continue;
                        }
                        immediateChildFolderCount++;
                        childFileCount += NativeDirectoryEnumerator.GetFileCount(sd.FullPath, DirectoryViewModel.ShowHidden, token);
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

                    int totalFileCount = activeFileCount + childFileCount;
                    SizeTextBlock.Text = $"{immediateChildFolderCount} folder{(immediateChildFolderCount == 1 ? "" : "s")} • {totalFileCount} file{(totalFileCount == 1 ? "" : "s")}";
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

        private async Task BuildFastTreeAsync(string dirPath, string dirName, int level, int activeRootLevel, double historyIndent, double indent, double[] currentY, double rowHeight, List<FastNode> list, System.Diagnostics.Stopwatch stopwatch, bool[] wasLimited, CancellationToken token, SemaphoreSlim semaphore, (List<SimpleDirectoryInfo> SubDirs, int FileCount)? preEnumeratedChildren = null)
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
            var node = new FastNode
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
            };
            list.Add(node);
            currentY[0] += rowHeight;

            // Progressive batch sizes to keep UI thread responsive while avoiding sudden jumps
            int batchSize = 200;
            if (list.Count > 5000) batchSize = 2000;
            else if (list.Count > 1000) batchSize = 1000;

            if (list.Count % batchSize == 0)
            {
                var currentSnapshot = new List<FastNode>(list);
                // Capture the height accumulated so far so the ScrollViewer bounds grow with the
                // partial tree. Without this the explicit Height is only set once the whole crawl
                // completes, leaving the user unable to scroll to already-rendered rows for the
                // several seconds a deep (depth >= 2) crawl takes.
                double snapshotHeight = currentY[0] + 50;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                        _currentRenderedNodes = currentSnapshot;
                        FolderTreeVisualHost.Height = snapshotHeight;
                        FolderTreeVisualHost.Width = 800;
                        FolderTreeVisualHost.RenderTree(currentSnapshot, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
                    }));

                // Delay rate: 100ms (10 batches/sec) for all levels
                await Task.Delay(100, token);
            }

            try
            {
                List<SimpleDirectoryInfo> allSubDirs;
                if (preEnumeratedChildren.HasValue)
                {
                    allSubDirs = preEnumeratedChildren.Value.SubDirs;
                    node.FileCount = preEnumeratedChildren.Value.FileCount;
                }
                else
                {
                    allSubDirs = NativeDirectoryEnumerator.GetSubDirectoriesAndFileCount(
                        dirPath, DirectoryViewModel.ShowHidden, token, out int fc);
                    node.FileCount = fc;
                }

                // Depth limit check (explore exactly _previewDepth levels under the active folder).
                // The node's own file count above doesn't require descending further, so it's
                // computed unconditionally before this check.
                if (level >= activeRootLevel + _previewDepth) return;

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

                if (subDirsList.Count == 0)
                {
                    return;
                }

                // Kick off all siblings' directory enumerations concurrently (bounded by semaphore).
                // Recursion below is sequential to preserve DFS display order; only the I/O is parallelised.
                var prefetchTasks = subDirsList.ConvertAll(sd =>
                    NativeDirectoryEnumerator.FetchSubDirectoriesAndFileCountAsync(sd.FullPath, DirectoryViewModel.ShowHidden, semaphore, token));

                for (int i = 0; i < subDirsList.Count; i++)
                {
                    if (token.IsCancellationRequested) return;

                    var childPrefetch = await prefetchTasks[i];
                    await BuildFastTreeAsync(subDirsList[i].FullPath, subDirsList[i].Name, level + 1, activeRootLevel, historyIndent, indent, currentY, rowHeight, list, stopwatch, wasLimited, token, semaphore, childPrefetch);
                }
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation rather than treating it as an access error
                throw;
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
            var info = ImageView.ReadOrientation(filePath);
            width = info.Width;
            height = info.Height;
            rotation = info.Rotation;
        }

        private void RotateImage()
        {
            _rotationAngle = ImageView.RotateStep(_rotationAngle);
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
        }

        private void ApplyPdfSkimLayout(int pageCount)
        {
            if (pageCount < 4)
            {
                // 1x1 Grid layout
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
                // 2x2 Grid layout
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

        private void LoadPdfFile(string filePath)
        {
            _activePdfPath = filePath;
            _pdfRenderer = null;
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
                ApplyPdfSkimLayout(cached.Length);
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
                    var renderer = new PdfRenderer();
                    if (!await renderer.LoadAsync(filePath, token)) return;

                    int count = renderer.PageCount >= 4 ? 4 : (renderer.PageCount > 0 ? 1 : 0);
                    var images = new BitmapSource[count];

                    for (int i = 0; i < count; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        images[i] = await renderer.RenderPageToBoxAsync(i, 600.0, token);
                        if (token.IsCancellationRequested) return;
                    }

                    PdfSkimCache.Add(filePath, images);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_activePdfPath == filePath && !token.IsCancellationRequested && !_isPdfActive)
                        {
                            ApplyPdfSkimLayout(images.Length);
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
                PopOutButton_Click(this, null);
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

        private void PopOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

            var previewWindow = new PreviewWindow(_currentFilePath)
            {
                Owner = Window.GetWindow(this)
            };

            if (_isPdfActive && _pdfPages != null && PdfActiveViewer != null)
            {
                var pageHeights = _pdfPages.ConvertAll(p => p.DisplayHeight);
                previewWindow.InitialPdfPageIndex = PdfLayout.PageIndexAtOffset(
                    PdfActiveViewer.VerticalOffset, pageHeights, PdfPageMargin);
            }

            previewWindow.Show();
        }

        private void FolderTreeVisualHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var nodes = _currentRenderedNodes;
            if (nodes == null || nodes.Count == 0) return;

            var pos = e.GetPosition(FolderTreeVisualHost);
            double rowHeight = 18.50;

            foreach (var node in nodes)
            {
                if (pos.Y >= node.Y && pos.Y < node.Y + rowHeight)
                {
                    if (pos.X >= node.X && !node.IsPlaceholder && !string.IsNullOrEmpty(node.FullPath))
                    {
                        DirectoryClicked?.Invoke(this, node.FullPath);
                    }
                    break;
                }
            }
        }

        private void FolderTreeVisualHost_MouseMove(object sender, MouseEventArgs e)
        {
            var nodes = _currentRenderedNodes;
            if (nodes == null || nodes.Count == 0)
            {
                FolderTreeVisualHost.Cursor = null;
                return;
            }

            var pos = e.GetPosition(FolderTreeVisualHost);
            double rowHeight = 18.50;
            bool found = false;

            foreach (var node in nodes)
            {
                if (pos.Y >= node.Y && pos.Y < node.Y + rowHeight)
                {
                    if (pos.X >= node.X && !node.IsPlaceholder && !string.IsNullOrEmpty(node.FullPath))
                    {
                        FolderTreeVisualHost.Cursor = Cursors.Hand;
                        found = true;
                    }
                    break;
                }
            }

            if (!found)
            {
                FolderTreeVisualHost.Cursor = null;
            }
        }

        private async Task LoadExcelFileAsync(string filePath, CancellationToken token)
        {
            try
            {
                byte[] decryptedBytes = null;
                if (IsEncryptedExcel(filePath))
                {
                    bool passwordCancelled = false;
                    string errorMessage = null;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            Window owner = Window.GetWindow(this);
                            decryptedBytes = TryDecryptExcelWithPrompt(filePath, owner);
                        }
                        catch (OperationCanceledException)
                        {
                            passwordCancelled = true;
                        }
                        catch (Exception ex)
                        {
                            errorMessage = ex.Message;
                        }
                    });

                    if (passwordCancelled)
                    {
                        await Dispatcher.InvokeAsync(() => ShowEmptyState("Decryption Cancelled", "Password is required to view this file."));
                        return;
                    }

                    if (errorMessage != null)
                    {
                        await Dispatcher.InvokeAsync(() => ShowEmptyState("Error Decrypting Spreadsheet", errorMessage));
                        return;
                    }
                }

                if (token.IsCancellationRequested) return;

                DataSet dataSet = await Task.Run(() => LoadExcelDataSet(filePath, decryptedBytes), token);

                if (token.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _currentFilePath != filePath) return;

                    bool isFullWindow = false;
                    Window parentWindow = Window.GetWindow(this);
                    if (parentWindow is PreviewWindow)
                    {
                        isFullWindow = true;
                    }
                    else
                    {
                        DependencyObject parent = VisualTreeHelper.GetParent(this);
                        while (parent != null)
                        {
                            if (parent is PreviewWindow)
                            {
                                isFullWindow = true;
                                break;
                            }
                            parent = VisualTreeHelper.GetParent(parent);
                        }
                    }

                    SpreadsheetMetadataLabel.Text = $"{Path.GetFileName(filePath)} (Safe, Non-Executable)";
                    SpreadsheetViewerGrid.Visibility = Visibility.Visible;

                    if (PaneStatusBar != null)
                    {
                        PaneStatusBar.Visibility = isFullWindow ? Visibility.Collapsed : Visibility.Visible;
                    }
                    if (SpreadsheetHeaderBorder != null)
                    {
                        SpreadsheetHeaderBorder.Visibility = isFullWindow ? Visibility.Collapsed : Visibility.Visible;
                    }

                    if (!isFullWindow)
                    {
                        SpreadsheetTabControl.Visibility = Visibility.Collapsed;
                        SpreadsheetPaneModeGrid.Visibility = Visibility.Visible;
                        OpenNewWindowButton.Visibility = Visibility.Visible;

                        if (dataSet.Tables.Count > 0)
                        {
                            DataTable firstTable = dataSet.Tables[0];
                            DataTable truncated = TruncateDataTable(firstTable, 60, 26);
                            DataGrid grid = CreateSpreadsheetDataGrid(truncated, false);
                            SpreadsheetPaneHost.Content = grid;
                        }
                    }
                    else
                    {
                        SpreadsheetPaneModeGrid.Visibility = Visibility.Collapsed;
                        SpreadsheetTabControl.Visibility = Visibility.Visible;
                        OpenNewWindowButton.Visibility = Visibility.Collapsed;
                        SpreadsheetTabControl.Items.Clear();

                        foreach (DataTable table in dataSet.Tables)
                        {
                            TabItem tabItem = new TabItem
                            {
                                Header = table.TableName
                            };
                            DataGrid grid = CreateSpreadsheetDataGrid(table, true);
                            tabItem.Content = grid;
                            SpreadsheetTabControl.Items.Add(tabItem);
                        }

                        if (SpreadsheetTabControl.Items.Count > 0)
                        {
                            SpreadsheetTabControl.SelectedIndex = 0;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() => ShowEmptyState("Error Loading Spreadsheet", ex.Message));
            }
        }

        private static bool IsEncryptedExcel(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] head = new byte[8];
                    int read = fs.Read(head, 0, 8);
                    if (read < 8) return false;
                    byte[] ole2Magic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
                    for (int i = 0; i < 8; i++)
                    {
                        if (head[i] != ole2Magic[i]) return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static byte[] TryDecryptExcelWithPrompt(string filePath, Window ownerWindow)
        {
            while (true)
            {
                string enteredPassword = null;
                bool cancelled = false;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dialog = new PasswordPromptDialog("Decrypt Spreadsheet", $"Enter password for \"{Path.GetFileName(filePath)}\":")
                    {
                        Owner = ownerWindow
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        enteredPassword = dialog.Password;
                    }
                    else
                    {
                        cancelled = true;
                    }
                });

                if (cancelled)
                {
                    throw new OperationCanceledException("Password entry cancelled by user.");
                }

                try
                {
                    return ExcelDecryptor.Decrypt(filePath, enteredPassword);
                }
                catch (ExcelDecryptException ex) when (ex.WrongPassword)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("Incorrect password. Please try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                catch (Exception ex)
                {
                    throw new Exception($"Decryption failed: {ex.Message}", ex);
                }
            }
        }

        private static DataSet LoadExcelDataSet(string filePath, byte[] decryptedBytes)
        {
            Stream stream = decryptedBytes != null
                ? (Stream)new MemoryStream(decryptedBytes)
                : new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using (stream)
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                return reader.AsDataSet(new ExcelDataSetConfiguration()
                {
                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                    {
                        UseHeaderRow = false
                    }
                });
            }
        }

        private static DataTable TruncateDataTable(DataTable originalTable, int maxRows, int maxCols)
        {
            var truncated = new DataTable(originalTable.TableName);
            int colsToKeep = Math.Min(originalTable.Columns.Count, maxCols);
            for (int i = 0; i < colsToKeep; i++)
            {
                truncated.Columns.Add(originalTable.Columns[i].ColumnName, originalTable.Columns[i].DataType);
            }

            int rowsToKeep = Math.Min(originalTable.Rows.Count, maxRows);
            for (int i = 0; i < rowsToKeep; i++)
            {
                DataRow newRow = truncated.NewRow();
                for (int j = 0; j < colsToKeep; j++)
                {
                    newRow[j] = originalTable.Rows[i][j];
                }
                truncated.Rows.Add(newRow);
            }
            return truncated;
        }

        private static string GetExcelColumnName(int columnIndex)
        {
            int dividend = columnIndex;
            string columnName = string.Empty;
            while (dividend > 0)
            {
                int modifier = (dividend - 1) % 26;
                columnName = Convert.ToChar(65 + modifier).ToString() + columnName;
                dividend = (dividend - modifier) / 26;
            }
            return columnName;
        }

        private DataGrid CreateSpreadsheetDataGrid(DataTable dataTable, bool isFullWindow)
        {
            int headerRowIndex = 0;
            int maxNonNullCount = -1;
            int maxRowsToScan = Math.Min(20, dataTable.Rows.Count);

            for (int r = 0; r < maxRowsToScan; r++)
            {
                DataRow row = dataTable.Rows[r];
                int nonNullCount = 0;
                for (int c = 0; c < dataTable.Columns.Count; c++)
                {
                    object val = row[c];
                    if (val != null && val != DBNull.Value && !string.IsNullOrWhiteSpace(val.ToString()))
                    {
                        nonNullCount++;
                    }
                }
                if (nonNullCount > maxNonNullCount)
                {
                    maxNonNullCount = nonNullCount;
                    headerRowIndex = r;
                }
            }

            var cleanedTable = new DataTable(dataTable.TableName);
            for (int c = 0; c < dataTable.Columns.Count; c++)
            {
                cleanedTable.Columns.Add("Col" + c, dataTable.Columns[c].DataType);
            }

            int startRow = dataTable.Rows.Count > headerRowIndex ? headerRowIndex : dataTable.Rows.Count;
            for (int r = startRow; r < dataTable.Rows.Count; r++)
            {
                DataRow newRow = cleanedTable.NewRow();
                for (int c = 0; c < dataTable.Columns.Count; c++)
                {
                    newRow[c] = dataTable.Rows[r][c];
                }
                cleanedTable.Rows.Add(newRow);
            }

            double initialScale = isFullWindow ? 1.0 : 0.8;
            var grid = new DataGrid
            {
                Style = TryFindResource("SpreadsheetDataGridStyle") as Style,
                HorizontalScrollBarVisibility = isFullWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = isFullWindow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                LayoutTransform = new ScaleTransform(initialScale, initialScale)
            };

            grid.AutoGeneratingColumn += (s, e) =>
            {
                if (s is DataGrid dg)
                {
                    int index = dg.Columns.Count;
                    e.Column.Header = GetExcelColumnName(index + 1);

                    if (e.Column is DataGridTextColumn textColumn && textColumn.Binding is System.Windows.Data.Binding binding)
                    {
                        binding.Converter = new ExcelDateConverter();
                    }
                }
            };

            grid.LoadingRow += (s, e) =>
            {
                int rowIndex = e.Row.GetIndex();
                e.Row.Header = (rowIndex + 1).ToString();
                if (rowIndex == 0)
                {
                    e.Row.FontWeight = FontWeights.Bold;
                    e.Row.Background = Brushes.Black;
                    e.Row.Foreground = Brushes.White;
                }
                else
                {
                    e.Row.ClearValue(Control.FontWeightProperty);
                    e.Row.ClearValue(Control.BackgroundProperty);
                    e.Row.ClearValue(Control.ForegroundProperty);
                }
            };

            grid.PreviewMouseWheel += DataGrid_PreviewMouseWheel;
            grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, DataGrid_CopyCommandExecuted));
            grid.ItemsSource = cleanedTable.DefaultView;
            return grid;
        }

        private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (sender is DataGrid grid)
                {
                    double currentScaleVal = 1.0;
                    if (grid.LayoutTransform is ScaleTransform scale)
                    {
                        currentScaleVal = scale.ScaleX;
                    }

                    double step = 0.10;
                    double newScale = currentScaleVal + (e.Delta > 0 ? step : -step);
                    newScale = Math.Max(0.5, Math.Min(4.0, newScale));

                    if (grid.LayoutTransform is ScaleTransform mutableScale && !mutableScale.IsFrozen)
                    {
                        mutableScale.ScaleX = newScale;
                        mutableScale.ScaleY = newScale;
                    }
                    else
                    {
                        grid.LayoutTransform = new ScaleTransform(newScale, newScale);
                    }
                }
            }
            else
            {
                if (sender is DataGrid grid && grid.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                {
                    e.Handled = true;
                }
            }
        }

        private void DataGrid_CopyCommandExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is DataGrid grid)
            {
                var selectedCells = grid.SelectedCells;
                if (selectedCells.Count == 0) return;

                var rowGroups = new Dictionary<object, List<DataGridCellInfo>>();
                foreach (var cell in selectedCells)
                {
                    if (cell.Item != null)
                    {
                        if (!rowGroups.TryGetValue(cell.Item, out var list))
                        {
                            list = new List<DataGridCellInfo>();
                            rowGroups[cell.Item] = list;
                        }
                        list.Add(cell);
                    }
                }

                var sortedRows = new List<object>(rowGroups.Keys);
                sortedRows.Sort((r1, r2) => grid.Items.IndexOf(r1).CompareTo(grid.Items.IndexOf(r2)));

                var hasTimeCache = new Dictionary<int, bool>();
                var sb = new StringBuilder();
                foreach (var rowItem in sortedRows)
                {
                    var cellsInRow = rowGroups[rowItem];
                    cellsInRow.Sort((c1, c2) => c1.Column.DisplayIndex.CompareTo(c2.Column.DisplayIndex));

                    var cellTexts = new List<string>();
                    foreach (var cellInfo in cellsInRow)
                    {
                        if (cellInfo.Item is DataRowView rowView && cellInfo.Column != null)
                        {
                            int colIndex = grid.Columns.IndexOf(cellInfo.Column);
                            if (colIndex >= 0 && colIndex < rowView.Row.ItemArray.Length)
                            {
                                var cellValue = rowView.Row.ItemArray[colIndex];
                                if (cellValue is DateTime dt)
                                {
                                    string format = (dt.TimeOfDay != TimeSpan.Zero) ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd";
                                    cellTexts.Add(dt.ToString(format));
                                }
                                else
                                {
                                    cellTexts.Add(cellValue?.ToString() ?? string.Empty);
                                }
                            }
                        }
                    }
                    sb.AppendLine(string.Join("\t", cellTexts));
                }

                try
                {
                    Clipboard.SetText(sb.ToString());
                }
                catch
                {
                }
            }
        }

        private void OpenNewWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

            try
            {
                var previewWindow = new PreviewWindow(_currentFilePath)
                {
                    Owner = Window.GetWindow(this)
                };
                previewWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open preview window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
