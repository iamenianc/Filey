using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Filey;
using Filey.Previews;

namespace Filey.Previews
{
    /// <summary>
    /// Interaction logic for DocxPreview.xaml
    /// </summary>
    public partial class DocxPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // Not used for docx but required by interface

        public DocxPreview()
        {
            InitializeComponent();
        }

        public async void Preview(string filePath, CancellationToken token)
        {
            try
            {
                // Convert the .docx to a FlowDocument on a background thread
                FlowDocument doc = await DocxToFlowDocumentConverter.ConvertAsync(filePath, token);
                // Update UI on UI thread
                Dispatcher.Invoke(() =>
                {
                    DocViewer.Document = doc;
                    // Raise status update (Word files are Unicode, use UTF‑8 for display)
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("UTF-8", ImageView.GetFormattedFileSize(filePath)));
                });
            }
            catch (OperationCanceledException)
            {
                // Swallow cancellation; Unload will be called separately
                Unload();
            }
            catch (Exception)
            {
                // On error, clear the viewer
                Dispatcher.Invoke(() => DocViewer.Document = null);
                // Optionally log the exception
            }
        }

        public void Unload()
        {
            Dispatcher.Invoke(() => DocViewer.Document = null);
        }
    }
}
