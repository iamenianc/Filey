using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

namespace Filey.Previews
{
    public partial class TextPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // unused but required by IPreviewControl

        public TextPreview()
        {
            InitializeComponent();
        }

        public void Preview(string filePath, CancellationToken token)
        {
            Task.Run(() => LoadTextFileAsync(filePath, token), token);
        }

        public void Unload()
        {
            ContentTextBox.Text = string.Empty;
            ContentTextBox.ScrollToHome();
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

                    ContentTextBox.Text = content;
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(encodingStr, sizeStr));
                }));
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ContentTextBox.Text = $"Error reading text file:\n{ex.Message}";
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs(string.Empty, string.Empty));
                }));
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
    }
}
