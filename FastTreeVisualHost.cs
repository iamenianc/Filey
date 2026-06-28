using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Filey
{
    public class FastNode
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public int Level { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public bool IsFile { get; set; }
        public bool IsPlaceholder { get; set; }
        public bool IsHistory { get; set; }
        public int FileCount { get; set; } = -1; // Default to -1 (means no count shown)
    }

    public class FastTreeVisualHost : FrameworkElement
    {
        private readonly VisualCollection _children;
        private readonly DrawingVisual _drawingVisual;

        public FastTreeVisualHost()
        {
            _children = new VisualCollection(this);
            _drawingVisual = new DrawingVisual();
            _children.Add(_drawingVisual);
        }

        public void RenderTree(List<FastNode> nodes)
        {
            using (DrawingContext dc = _drawingVisual.RenderOpen())
            {
                if (nodes == null || nodes.Count == 0) return;

                // Premium colors for lines and text in dark theme
                var linePen = new Pen(new SolidColorBrush(Color.FromRgb(71, 85, 105)), 1); // Slate 600
                linePen.Freeze();

                var folderBrush = new SolidColorBrush(Color.FromRgb(234, 179, 8)); // Premium amber-yellow folder color
                folderBrush.Freeze();

                var rootTextBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252)); // Slate 50 (Off-white)
                rootTextBrush.Freeze();

                var folderTextBrush = new SolidColorBrush(Color.FromRgb(241, 245, 249)); // Slate 100
                folderTextBrush.Freeze();

                var fileTextBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate 400 (Dimmed for history)
                fileTextBrush.Freeze();

                var placeholderTextBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Slate 500 (Muted/Subtle for file counts)
                placeholderTextBrush.Freeze();

                var font = new Typeface("Segoe UI");
                var italicFont = new Typeface(font.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
                var boldFont = new Typeface(font.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

                double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                // Determine active root level dynamically
                int activeRootLevel = nodes.FindIndex(n => !n.IsHistory);
                if (activeRootLevel < 0) activeRootLevel = 0;

                foreach (var node in nodes)
                {
                    Brush textBrush;
                    Typeface nodeFont;
                    
                    if (node.IsPlaceholder)
                    {
                        textBrush = placeholderTextBrush;
                        nodeFont = italicFont;
                    }
                    else if (node.IsHistory)
                    {
                        textBrush = fileTextBrush;
                        nodeFont = font;
                    }
                    else // Active folders
                    {
                        textBrush = (node.Level == activeRootLevel) ? rootTextBrush : folderTextBrush;
                        nodeFont = (node.Level == activeRootLevel) ? boldFont : font;
                        DrawFolderIcon(dc, node.X, node.Y, folderBrush);
                    }

                    var formattedText = new FormattedText(
                        node.Name,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        nodeFont,
                        12,
                        textBrush,
                        dpiScale);

                    // Offset text: history nodes and placeholders don't have folder icons.
                    double textXOffset = (node.IsHistory || node.IsPlaceholder) ? 0 : 20;
                    dc.DrawText(formattedText, new Point(node.X + textXOffset, node.Y));

                    // If active folder node has a file count, draw it with a subtle muted color next to the name
                    if (!node.IsHistory && !node.IsPlaceholder && node.FileCount >= 0)
                    {
                        string countText = $" ({node.FileCount} file{(node.FileCount == 1 ? "" : "s")})";
                        var formattedCount = new FormattedText(
                            countText,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            font,
                            12,
                            placeholderTextBrush,
                            dpiScale);

                        dc.DrawText(formattedCount, new Point(node.X + textXOffset + formattedText.Width, node.Y));
                    }

                    // 3. Draw simple structural connector lines back to the parent level (L-shaped)
                    if (node.Level > 0)
                    {
                        if (node.IsHistory)
                        {
                            // History L-connector
                            dc.DrawLine(linePen, new Point(node.X - 5, node.Y + 10), new Point(node.X - 1, node.Y + 10));
                            dc.DrawLine(linePen, new Point(node.X - 5, node.Y - 10), new Point(node.X - 5, node.Y + 10));
                        }
                        else
                        {
                            if (node.Level == activeRootLevel)
                            {
                                // Active root connects back to parent history node
                                dc.DrawLine(linePen, new Point(node.X - 5, node.Y + 10), new Point(node.X - 1, node.Y + 10));
                                dc.DrawLine(linePen, new Point(node.X - 5, node.Y - 10), new Point(node.X - 5, node.Y + 10));
                            }
                            else // Active folder subdirectories
                            {
                                // Subdirectory L-connector
                                dc.DrawLine(linePen, new Point(node.X - 15, node.Y + 10), new Point(node.X - 2, node.Y + 10));
                                dc.DrawLine(linePen, new Point(node.X - 15, node.Y - 10), new Point(node.X - 15, node.Y + 10));
                            }
                        }
                    }
                }
            }
        }

        private void DrawFolderIcon(DrawingContext dc, double x, double y, Brush brush)
        {
            // Folder dimensions: 14x10, starts slightly down
            var rect = new Rect(x, y + 4, 14, 8);
            var tabGeometry = new StreamGeometry();
            using (var context = tabGeometry.Open())
            {
                context.BeginFigure(new Point(x, y + 4), true, true);
                context.LineTo(new Point(x, y + 2), true, false);
                context.LineTo(new Point(x + 5, y + 2), true, false);
                context.LineTo(new Point(x + 7, y + 4), true, false);
            }
            tabGeometry.Freeze();

            dc.DrawGeometry(brush, null, tabGeometry);
            dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
        }

        protected override int VisualChildrenCount => _children.Count;
        protected override Visual GetVisualChild(int index) => _children[index];
    }
}
