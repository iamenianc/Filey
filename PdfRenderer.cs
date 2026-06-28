using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Filey
{
    /// <summary>
    /// Wraps a loaded WinRT <see cref="PdfDocument"/> behind one seam so the preview
    /// surfaces stop re-implementing the
    /// load → GetPage → RenderToStreamAsync → BitmapImage dance (which previously
    /// appeared in four places with subtly different parameters). Holds the document for
    /// the lifetime of one open file; callers render pages and thumbnails through it.
    /// </summary>
    public sealed class PdfRenderer
    {
        private PdfDocument _document;

        public int PageCount => _document != null ? (int)_document.PageCount : 0;

        /// <summary>Loads the document at <paramref name="filePath"/>. Returns false if cancelled.</summary>
        public async Task<bool> LoadAsync(string filePath, CancellationToken token)
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);
            if (token.IsCancellationRequested) return false;
            _document = doc;
            return true;
        }

        /// <summary>
        /// Returns each page's aspect ratio (height / width), in page order. Used to size
        /// page placeholders before any pixels are rendered.
        /// </summary>
        public List<double> PageAspectRatios()
        {
            var ratios = new List<double>();
            if (_document == null) return ratios;
            for (uint i = 0; i < _document.PageCount; i++)
            {
                using (PdfPage page = _document.GetPage(i))
                {
                    ratios.Add(page.Size.Height / page.Size.Width);
                }
            }
            return ratios;
        }

        /// <summary>
        /// Renders one page to a frozen <see cref="BitmapSource"/> at the requested pixel
        /// size, safe to hand to the UI thread. Returns null if the document is unloaded or
        /// the token cancels mid-render.
        /// </summary>
        public async Task<BitmapSource> RenderPageAsync(int pageIndex, uint width, uint height, CancellationToken token)
        {
            var doc = _document;
            if (doc == null || pageIndex < 0 || pageIndex >= (int)doc.PageCount) return null;

            using (PdfPage pdfPage = doc.GetPage((uint)pageIndex))
            {
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = width,
                    DestinationHeight = height,
                };

                using (var stream = new InMemoryRandomAccessStream())
                {
                    await pdfPage.RenderToStreamAsync(stream, options);
                    if (token.IsCancellationRequested) return null;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream.AsStream();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
        }

        /// <summary>
        /// Renders a page scaled to fit within a square box of <paramref name="boxSize"/>
        /// pixels, preserving aspect ratio — used for the skim thumbnails.
        /// </summary>
        public async Task<BitmapSource> RenderPageToBoxAsync(int pageIndex, double boxSize, CancellationToken token)
        {
            var doc = _document;
            if (doc == null || pageIndex < 0 || pageIndex >= (int)doc.PageCount) return null;

            using (PdfPage page = doc.GetPage((uint)pageIndex))
            {
                var size = page.Size;
                double scale = Math.Min(boxSize / size.Width, boxSize / size.Height);
                uint w = (uint)Math.Max(1, size.Width * scale);
                uint h = (uint)Math.Max(1, size.Height * scale);
                return await RenderPageAsync(pageIndex, w, h, token);
            }
        }
    }
}
