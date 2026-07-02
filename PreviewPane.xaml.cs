using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Filey.Previews;

namespace Filey
{
    public partial class PreviewPane : UserControl
    {
        private string _currentFilePath;
        private CancellationTokenSource _cts;
        private IPreviewControl _activeControl;

        public event EventHandler<string> DirectoryClicked;

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

            TextPreviewControl.StatusUpdated += OnControlStatusUpdated;
            ImagePreviewControl.StatusUpdated += OnControlStatusUpdated;
            FolderPreviewControl.StatusUpdated += OnControlStatusUpdated;
            FolderPreviewControl.DirectoryClicked += OnDirectoryClicked;
            PdfPreviewControl.StatusUpdated += OnControlStatusUpdated;
            MarkdownPreviewControl.StatusUpdated += OnControlStatusUpdated;
            ExcelPreviewControl.StatusUpdated += OnControlStatusUpdated;
        }

        private void OnControlStatusUpdated(object sender, PreviewStatusEventArgs e)
        {
            EncodingTextBlock.Text = e.Encoding;
            SizeTextBlock.Text = e.Size;
        }

        private void OnDirectoryClicked(object sender, string path)
        {
            DirectoryClicked?.Invoke(this, path);
        }

        public void PreviewFile(string filePath)
        {
            _cts?.Cancel();
            _cts = null;

            _currentFilePath = filePath;

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
                FolderPreviewControl.Visibility = Visibility.Visible;
                DepthSelectorPanel.Visibility = Visibility.Visible;
                if (ScrollPromptTextBlock != null)
                {
                    ScrollPromptTextBlock.Visibility = Visibility.Visible;
                }
                UpdateDepthButtonHighlights(FolderPreviewControl.GetDepth());

                PathTextBlock.Text = filePath;
                _activeControl = FolderPreviewControl;
                FolderPreviewControl.Preview(filePath, dirToken);
                return;
            }

            if (!File.Exists(filePath))
            {
                ShowEmptyState("Select a file to preview", "Support for text, code files, and images");
                return;
            }

            string ext = Path.GetExtension(filePath).ToLower();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            EmptyStateBorder.Visibility = Visibility.Collapsed;
            PathTextBlock.Text = filePath;

            if (ext == ".md")
            {
                MarkdownPreviewControl.Visibility = Visibility.Visible;
                _activeControl = MarkdownPreviewControl;
                MarkdownPreviewControl.Preview(filePath, token);
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

            if (isText)
            {
                TextPreviewControl.Visibility = Visibility.Visible;
                _activeControl = TextPreviewControl;
                TextPreviewControl.Preview(filePath, token);
            }
            else if (isImage)
            {
                ImagePreviewControl.Visibility = Visibility.Visible;
                _activeControl = ImagePreviewControl;
                ImagePreviewControl.Preview(filePath, token);
            }
            else if (isPdf)
            {
                PdfPreviewControl.Visibility = Visibility.Visible;
                _activeControl = PdfPreviewControl;
                PdfPreviewControl.Preview(filePath, token);
            }
            else if (isExcel)
            {
                ExcelPreviewControl.Visibility = Visibility.Visible;
                _activeControl = ExcelPreviewControl;
                ExcelPreviewControl.Preview(filePath, token);
            }
        }

        private void RecyclePreview()
        {
            _activeControl = null;

            TextPreviewControl.Visibility = Visibility.Collapsed;
            TextPreviewControl.Unload();

            ImagePreviewControl.Visibility = Visibility.Collapsed;
            ImagePreviewControl.Unload();

            FolderPreviewControl.Visibility = Visibility.Collapsed;
            FolderPreviewControl.Unload();

            PdfPreviewControl.Visibility = Visibility.Collapsed;
            PdfPreviewControl.Unload();

            MarkdownPreviewControl.Visibility = Visibility.Collapsed;
            MarkdownPreviewControl.Unload();

            ExcelPreviewControl.Visibility = Visibility.Collapsed;
            ExcelPreviewControl.Unload();

            if (DepthSelectorPanel != null)
            {
                DepthSelectorPanel.Visibility = Visibility.Collapsed;
            }

            if (ScrollPromptTextBlock != null)
            {
                ScrollPromptTextBlock.Visibility = Visibility.Collapsed;
            }

            PathTextBlock.Text = string.Empty;
            EncodingTextBlock.Text = string.Empty;
            SizeTextBlock.Text = string.Empty;

            Task.Run(() => MemoryManager.ReleaseUnusedMemory());
        }

        private void ShowEmptyState(string mainText, string subText)
        {
            RecyclePreview();
            EmptyStateMainText.Text = mainText;
            EmptyStateSubText.Text = subText;
            EmptyStateBorder.Visibility = Visibility.Visible;
        }

        private void DepthButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int targetDepth))
            {
                FolderPreviewControl.SetDepth(targetDepth);
                UpdateDepthButtonHighlights(targetDepth);
            }
        }

        private void UpdateDepthButtonHighlights(int depth)
        {
            var activeBrush = ThemeService.Brush("AppGoldAccentBrush");
            var inactiveBrush = ThemeService.Brush("AppTextMutedBrush");

            if (DepthButton1 != null) DepthButton1.Foreground = (depth == 1) ? activeBrush : inactiveBrush;
            if (DepthButton2 != null) DepthButton2.Foreground = (depth == 2) ? activeBrush : inactiveBrush;
            if (DepthButton3 != null) DepthButton3.Foreground = (depth == 3) ? activeBrush : inactiveBrush;
            if (DepthButton4 != null) DepthButton4.Foreground = (depth == 4) ? activeBrush : inactiveBrush;
            if (DepthButton5 != null) DepthButton5.Foreground = (depth == 5) ? activeBrush : inactiveBrush;
            if (DepthButton6 != null) DepthButton6.Foreground = (depth == 6) ? activeBrush : inactiveBrush;
        }

        private void PopOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

            var previewWindow = new PreviewWindow(_currentFilePath)
            {
                Owner = Window.GetWindow(this)
            };

            if (PdfPreviewControl.Visibility == Visibility.Visible && PdfPreviewControl.IsPdfActive && PdfPreviewControl.PdfPages != null)
            {
                var pageHeights = PdfPreviewControl.PdfPages.ConvertAll(p => p.DisplayHeight);
                previewWindow.InitialPdfPageIndex = PdfLayout.PageIndexAtOffset(
                    PdfPreviewControl.VerticalOffset, pageHeights, 24.0);
            }

            previewWindow.Show();
        }

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);
    }
}
