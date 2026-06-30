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

        // Cached arguments of the last render so the tree can be redrawn when the theme changes.
        private List<FastNode> _lastNodes;
        private double _lastVerticalOffset;
        private double _lastViewportHeight;

        public FastTreeVisualHost()
        {
            _children = new VisualCollection(this);
            _drawingVisual = new DrawingVisual();
            _children.Add(_drawingVisual);

            // Re-render with the new palette when the theme is switched at runtime.
            Loaded += (s, e) => ThemeService.ThemeChanged += OnThemeChanged;
            Unloaded += (s, e) => ThemeService.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (_lastNodes != null)
                RenderTree(_lastNodes, _lastVerticalOffset, _lastViewportHeight);
        }

        /// <summary>Resolves a frozen brush from a palette Color resource, falling back to
        /// <paramref name="fallback"/> when the resource is unavailable (e.g. at design time).</summary>
        private static SolidColorBrush PaletteBrush(string colorKey, Color fallback)
        {
            object found = Application.Current?.TryFindResource(colorKey);
            var color = found is Color c ? c : fallback;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static FontFamily PaletteFontFamily()
        {
            return Application.Current?.TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        }

        public void RenderTree(List<FastNode> nodes, double verticalOffset = 0, double viewportHeight = 0)
        {
            _lastNodes = nodes;
            _lastVerticalOffset = verticalOffset;
            _lastViewportHeight = viewportHeight;


            using (DrawingContext dc = _drawingVisual.RenderOpen())
            {
                if (nodes == null || nodes.Count == 0) return;

                // Virtualization boundaries (offset by 300px buffer top/bottom for pre-caching)
                bool enableVirtualization = viewportHeight > 0;
                double minY = verticalOffset - 300;
                double maxY = verticalOffset + viewportHeight + 300;

                // Colors come from the active theme palette (see Themes/Colors.*.xaml). The tree is
                // redrawn on every theme change so these reflect the current Light/Dark values.
                var linePen = new Pen(PaletteBrush("TreeLineColor", Color.FromRgb(71, 85, 105)), 1);
                linePen.Freeze();

                var folderBrush = PaletteBrush("TreeFolderColor", Color.FromRgb(234, 179, 8));
                var rootTextBrush = PaletteBrush("TreeRootTextColor", Color.FromRgb(248, 250, 252));
                var folderTextBrush = PaletteBrush("TreeFolderTextColor", Color.FromRgb(241, 245, 249));
                var fileTextBrush = PaletteBrush("TreeDimTextColor", Color.FromRgb(148, 163, 184));
                var placeholderTextBrush = PaletteBrush("TreeMutedTextColor", Color.FromRgb(100, 116, 139));

                var font = new Typeface(PaletteFontFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var italicFont = new Typeface(font.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
                var boldFont = new Typeface(font.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

                double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                // Determine active root level dynamically
                int activeRootLevel = nodes.FindIndex(n => !n.IsHistory);
                if (activeRootLevel < 0) activeRootLevel = 0;

                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];

                    // 1. Draw simple structural connector lines back to the parent level (L-shaped) using parent coordinates
                    if (node.Level > 0)
                    {
                        FastNode parentNode = null;
                        for (int j = i - 1; j >= 0; j--)
                        {
                            if (nodes[j].Level == node.Level - 1)
                            {
                                parentNode = nodes[j];
                                break;
                            }
                        }

                        if (parentNode != null)
                        {
                            double lineYStart = parentNode.Y + (node.IsHistory ? 8 : (parentNode.IsHistory ? 8 : 12));
                            double lineYEnd = node.Y + 8;

                            // Only draw the lines if they intersect with the visible vertical viewport
                            if (!enableVirtualization || (lineYStart <= maxY && lineYEnd >= minY))
                            {
                                if (node.IsHistory)
                                {
                                    // History L-connector
                                    double lineX = parentNode.X + 10;
                                    dc.DrawLine(linePen, new Point(lineX, node.Y + 8), new Point(node.X - 1, node.Y + 8));
                                    dc.DrawLine(linePen, new Point(lineX, parentNode.Y + 8), new Point(lineX, node.Y + 8));
                                }
                                else
                                {
                                    if (node.Level == activeRootLevel)
                                    {
                                        // Active root connects back to parent history node
                                        double lineX = parentNode.X + 10;
                                        dc.DrawLine(linePen, new Point(lineX, node.Y + 8), new Point(node.X, node.Y + 8));
                                        dc.DrawLine(linePen, new Point(lineX, parentNode.Y + 8), new Point(lineX, node.Y + 8));
                                    }
                                    else // Active folder subdirectories
                                    {
                                        // Subdirectory L-connector, starting from the parent folder's bottom edge (parentNode.Y + 12)
                                        // at the horizontal center of the parent folder (parentNode.X + 7) to prevent overlapping the parent icon
                                        double lineX = parentNode.X + 7;
                                        dc.DrawLine(linePen, new Point(lineX, node.Y + 8), new Point(node.X, node.Y + 8));
                                        dc.DrawLine(linePen, new Point(lineX, parentNode.Y + 12), new Point(lineX, node.Y + 8));
                                    }
                                }
                            }
                        }
                    }

                    // Only draw text and folder icons if they are visible in the viewport
                    if (!enableVirtualization || (node.Y >= minY && node.Y <= maxY))
                    {
                        // 2. Draw text and folder icons on top of the lines
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

                        if (!node.IsHistory && !node.IsPlaceholder && node.FileCount >= 0)
                        {
                            string countLabel = node.FileCount == 1 ? " · 1 file" : $" · {node.FileCount} files";
                            var countText = new FormattedText(countLabel, CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, font, 11, placeholderTextBrush, dpiScale);
                            dc.DrawText(countText, new Point(node.X + textXOffset + formattedText.Width, node.Y + 0.5));
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
