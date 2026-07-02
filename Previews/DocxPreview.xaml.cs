using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Filey.Previews
{
    /// <summary>
    /// Preview control that renders Word documents (.docx) as WPF FlowDocuments.
    /// </summary>
    public partial class DocxPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // Required by IPreviewControl; unused for documents

        private CancellationTokenSource _cts;
        private string _currentFilePath;
        private double _baseZoom = 1.0;

        public DocxPreview()
        {
            InitializeComponent();
            DocViewer.Zoom = 100;
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _currentFilePath = filePath;
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken linkedToken = _cts.Token;

            string sizeStr = GetFormattedFileSize(filePath);
            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("DOCX", sizeStr));

            _baseZoom = 1.0;
            DocViewer.Zoom = 100;

            Task.Run(() => LoadDocumentAsync(filePath, linkedToken), linkedToken);
        }

        public void Unload()
        {
            _cts?.Cancel();
            _cts = null;
            _currentFilePath = null;
            _baseZoom = 1.0;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                DocViewer.Document = null;
                DocViewer.Zoom = 100;
            }));
        }

        private async Task LoadDocumentAsync(string filePath, CancellationToken token)
        {
            try
            {
                FlowDocument doc = await DocxToFlowDocumentConverter.ConvertAsync(filePath, token);

                if (token.IsCancellationRequested) return;

                string sizeStr = GetFormattedFileSize(filePath);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested || _currentFilePath != filePath) return;

                    DocViewer.Document = doc;
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("DOCX", sizeStr));
                }));
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation; Unload/RecyclePreview handles cleanup.
            }
            catch (FileNotFoundException)
            {
                ReportError(filePath, "File not found.");
            }
            catch (UnauthorizedAccessException)
            {
                ReportError(filePath, "Access denied.");
            }
            catch (IOException ex)
            {
                ReportError(filePath, $"Unable to read file: {ex.Message}");
            }
            catch (Exception ex)
            {
                ReportError(filePath, $"Error rendering document: {ex.Message}");
            }
        }

        private void ReportError(string filePath, string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentFilePath != filePath) return;

                DocViewer.Document = null;
                StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error", message));
            }));
        }

        private void DocViewerHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                double step = e.Delta > 0 ? 0.1 : -0.1;
                double newZoom = _baseZoom + step;
                newZoom = Math.Max(0.5, Math.Min(3.0, newZoom));
                _baseZoom = newZoom;
                DocViewer.Zoom = (int)Math.Round(newZoom * 100);
                e.Handled = true;
            }
        }

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);
    }
}
