using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace Filey.Previews
{
    public partial class MarkdownPreview : UserControl, IPreviewControl
    {
        public event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        public event EventHandler<string> DirectoryClicked; // unused

        private WebView2 _webView;
        private string _pendingHtml;
        private string _currentFilePath;
        private CancellationTokenSource _cts;

        public MarkdownPreview()
        {
            InitializeComponent();

            ThemeService.ThemeChanged += OnThemeChanged;
            this.Unloaded += (s, e) =>
            {
                ThemeService.ThemeChanged -= OnThemeChanged;
                _cts?.Cancel();
                DisposeWebView();
            };
        }

        private void OnThemeChanged()
        {
            if (!string.IsNullOrEmpty(_currentFilePath) && Path.GetExtension(_currentFilePath).ToLower() == ".md")
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                Task.Run(() => LoadMarkdownAsync(_currentFilePath, token), token);
            }
        }

        public void Preview(string filePath, CancellationToken token)
        {
            _currentFilePath = filePath;
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var linkedToken = _cts.Token;

            string sizeStr = GetFormattedFileSize(filePath);
            StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Markdown", sizeStr));

            Task.Run(() => LoadMarkdownAsync(filePath, linkedToken), linkedToken);
        }

        public void Unload()
        {
            _cts?.Cancel();
            _cts = null;
            DisposeWebView();
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
                {
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("Error Rendering Markdown", ex.Message));
                }));
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
                    StatusUpdated?.Invoke(this, new PreviewStatusEventArgs("WebView2 unavailable", ex.Message));
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
        }

        private string GetFormattedFileSize(string filePath) => ImageView.GetFormattedFileSize(filePath);
    }
}
