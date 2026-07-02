using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Filey.Previews
{
    public partial class FolderPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked;

        private int _previewDepth = 1;
        private List<FastNode> _currentRenderedNodes;
        private CancellationTokenSource _scrollCts;
        private string _currentFilePath;
        private CancellationTokenSource _loadCts;

        public FolderPreview()
        {
            InitializeComponent();

            FolderTreeVisualHost.MouseLeftButtonUp += FolderTreeVisualHost_MouseLeftButtonUp;
            FolderTreeVisualHost.MouseMove += FolderTreeVisualHost_MouseMove;

            ThemeService.ThemeChanged += OnThemeChanged;
            this.Unloaded += (s, e) =>
            {
                ThemeService.ThemeChanged -= OnThemeChanged;
                _loadCts?.Cancel();
                _scrollCts?.Cancel();
            };
        }

        private void OnThemeChanged()
        {
            if (_currentRenderedNodes != null)
            {
                FolderTreeVisualHost.RenderTree(_currentRenderedNodes, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
            }
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _currentFilePath = filePath;
            _loadCts?.Cancel();
            _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _loadCts.Token;

            Task.Run(() => LoadDirectoryPreviewAsync(filePath, linkedToken), linkedToken);
        }

        public void Unload()
        {
            _loadCts?.Cancel();
            _loadCts = null;
            _scrollCts?.Cancel();
            _scrollCts = null;

            FolderScrollViewer.ScrollToTop();
            FolderScrollViewer.ScrollToHorizontalOffset(0);
            _currentRenderedNodes = null;
        }

        public void SetDepth(int depth)
        {
            if (_previewDepth == depth) return;
            _previewDepth = depth;

            if (!string.IsNullOrEmpty(_currentFilePath) && Directory.Exists(_currentFilePath))
            {
                Preview(_currentFilePath, CancellationToken.None);
            }
        }

        public int GetDepth() => _previewDepth;

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
                }

                int activeRootLevel = historyPaths.Count;

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
                }

                if (token.IsCancellationRequested) return;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;

                    _currentRenderedNodes = nodes;
                    FolderTreeVisualHost.RenderTree(nodes, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
                    FolderTreeVisualHost.Height = currentY[0] + 50;
                    FolderTreeVisualHost.Width = 800;

                    int totalFileCount = activeFileCount + childFileCount;
                    string encodingText = $"Directory Tree ({_previewDepth} Level{(_previewDepth == 1 ? "" : "s")})";
                    string sizeText = $"{immediateChildFolderCount} folder{(immediateChildFolderCount == 1 ? "" : "s")} • {totalFileCount} file{(totalFileCount == 1 ? "" : "s")}";

                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(encodingText, sizeText));
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error", ex.Message));
                }));
            }
        }

        private async Task BuildFastTreeAsync(string dirPath, string dirName, int level, int activeRootLevel, double historyIndent, double indent, double[] currentY, double rowHeight, List<FastNode> list, System.Diagnostics.Stopwatch stopwatch, bool[] wasLimited, CancellationToken token, SemaphoreSlim semaphore, (List<SimpleDirectoryInfo> SubDirs, int FileCount)? preEnumeratedChildren = null)
        {
            if (token.IsCancellationRequested) return;

            if (list.Count >= 10000)
            {
                wasLimited[0] = true;
                return;
            }

            double xCoord = (activeRootLevel * historyIndent) + (level - activeRootLevel) * indent;

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

            int batchSize = 200;
            if (list.Count > 5000) batchSize = 2000;
            else if (list.Count > 1000) batchSize = 1000;

            if (list.Count % batchSize == 0)
            {
                var currentSnapshot = new List<FastNode>(list);
                double snapshotHeight = currentY[0] + 50;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _currentRenderedNodes = currentSnapshot;
                    FolderTreeVisualHost.Height = snapshotHeight;
                    FolderTreeVisualHost.Width = 800;
                    FolderTreeVisualHost.RenderTree(currentSnapshot, FolderScrollViewer.VerticalOffset, FolderScrollViewer.ViewportHeight);
                }));

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

                if (level >= activeRootLevel + _previewDepth) return;

                var subDirsList = new List<SimpleDirectoryInfo>();

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
                throw;
            }
            catch (Exception)
            {
            }
        }

        private void FolderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (e.Delta > 0)
                {
                    SetDepth(Math.Min(6, _previewDepth + 1));
                }
                else if (e.Delta < 0)
                {
                    SetDepth(Math.Max(1, _previewDepth - 1));
                }
            }
            else
            {
                e.Handled = true;
                double scrollOffset = FolderScrollViewer.VerticalOffset - (e.Delta > 0 ? 120 : -120);
                FolderScrollViewer.ScrollToVerticalOffset(Math.Max(0, Math.Min(FolderScrollViewer.ScrollableHeight, scrollOffset)));
            }
        }

        private void FolderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_currentRenderedNodes != null && (e.VerticalChange != 0 || e.ViewportHeightChange != 0))
            {
                _scrollCts?.Cancel();
                _scrollCts = new CancellationTokenSource();
                var token = _scrollCts.Token;

                var verticalOffset = FolderScrollViewer.VerticalOffset;
                var viewportHeight = FolderScrollViewer.ViewportHeight;

                Task.Run(async () =>
                {
                    try
                    {
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
    }
}
