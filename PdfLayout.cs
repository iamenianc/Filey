using System;
using System.Collections.Generic;

namespace Filey
{
    /// <summary>
    /// Pure scroll/zoom geometry for the continuous PDF viewers, shared by
    /// <see cref="PreviewPane"/> and <see cref="PreviewWindow"/>. Operates only on numbers
    /// (page heights, offsets, viewport sizes) so the maths that recent zoom/fit-page work
    /// kept rewriting in event handlers now has one home and is directly testable.
    /// </summary>
    public static class PdfLayout
    {
        /// <summary>Cumulative top offset of the page at <paramref name="pageIndex"/>, given
        /// each page's stacked display height and the inter-page margin.</summary>
        public static double PageOffset(int pageIndex, IReadOnlyList<double> pageHeights, double pageMargin)
        {
            if (pageHeights == null) return 0;
            double y = 0;
            for (int i = 0; i < pageIndex && i < pageHeights.Count; i++)
                y += pageHeights[i] + pageMargin;
            return y;
        }

        /// <summary>
        /// Indices of pages intersecting the viewport plus a buffer above and below, used to
        /// decide which pages to render and which to release. Pages stack vertically with
        /// <paramref name="pageMargin"/> between them.
        /// </summary>
        public static HashSet<int> VisiblePageIndices(double scrollOffset, double viewportHeight,
            IReadOnlyList<double> pageHeights, double pageMargin, double buffer)
        {
            var visible = new HashSet<int>();
            if (pageHeights == null) return visible;

            double topLimit = scrollOffset - buffer;
            double bottomLimit = scrollOffset + viewportHeight + buffer;

            double y = 0;
            for (int i = 0; i < pageHeights.Count; i++)
            {
                double top = y;
                double bottom = y + pageHeights[i];
                if (bottom >= topLimit && top <= bottomLimit)
                    visible.Add(i);
                y = bottom + pageMargin;
            }
            return visible;
        }

        /// <summary>
        /// Index of the page occupying a given scroll offset (the one currently "on screen"
        /// at the top), for keeping the page indicator in sync while scrolling.
        /// </summary>
        public static int PageIndexAtOffset(double scrollOffset, IReadOnlyList<double> pageHeights, double pageMargin)
        {
            if (pageHeights == null || pageHeights.Count == 0) return 0;
            double y = 0;
            for (int i = 0; i < pageHeights.Count; i++)
            {
                double height = pageHeights[i];
                if (scrollOffset >= y && scrollOffset < y + height + pageMargin)
                    return i;
                y += height + pageMargin;
            }
            return 0;
        }

        /// <summary>
        /// The zoom level (relative to a fit-width baseline width) that makes a page of the
        /// given aspect ratio fit the viewport height. Returns 1.0 for degenerate input.
        /// </summary>
        public static double FitPageZoom(double viewportHeight, double aspectRatio,
            double baselineWidth, double vPadding)
        {
            if (aspectRatio <= 0 || baselineWidth <= 0) return 1.0;
            double desiredWidth = Math.Max(100, (viewportHeight - vPadding) / aspectRatio);
            return desiredWidth / baselineWidth;
        }

        /// <summary>
        /// The fit-width page display width: the available width less padding, halved (less a
        /// gutter) for dual-page spreads, scaled by the current zoom level.
        /// </summary>
        public static double DisplayWidth(double availableWidth, double padding, bool dualPage,
            double dualGutter, double zoomLevel)
        {
            double baseline = dualPage
                ? Math.Max(100, (availableWidth - padding - dualGutter) / 2.0)
                : Math.Max(100, availableWidth - padding);
            return baseline * zoomLevel;
        }
    }
}
