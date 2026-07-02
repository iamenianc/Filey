using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WParagraph = System.Windows.Documents.Paragraph;
using WTable = System.Windows.Documents.Table;
using WTableCell = System.Windows.Documents.TableCell;
using WTableColumn = System.Windows.Documents.TableColumn;
using WTableRow = System.Windows.Documents.TableRow;
using WTextAlignment = System.Windows.TextAlignment;
using WColor = System.Windows.Media.Color;
using WInline = System.Windows.Documents.Inline;
using WRun = System.Windows.Documents.Run;
using WSpan = System.Windows.Documents.Span;
using WHyperlink = System.Windows.Documents.Hyperlink;

namespace Filey
{
    /// <summary>
    /// Converts a Word .docx document into a WPF FlowDocument for preview.
    /// Runs on a background thread and produces frozen resources that can be displayed
    /// on the UI thread.
    /// </summary>
    public static class DocxToFlowDocumentConverter
    {
        private const double TwipsPerPixel = 15.0; // 1440 twips per inch / 96 dpi
        private const double HalfPointsPerPixel = 1.5; // 72 pt / 96 dpi => 0.75, inverse is 1.333, but existing code used 2/3. Let's use consistent: half-point * 2/3 = px.
        private const double HalfPointsToPixelsFactor = 2.0 / 3.0;

        /// <summary>
        /// Converts a .docx file to a FlowDocument.
        /// Parsing runs on a background thread; the FlowDocument tree is built on the
        /// calling (UI) thread because WPF FlowDocument elements have thread affinity.
        /// </summary>
        public static async Task<FlowDocument> ConvertAsync(string docxPath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(docxPath))
                throw new ArgumentException("Document path is required.", nameof(docxPath));

            if (!File.Exists(docxPath))
                throw new FileNotFoundException("Document not found.", docxPath);

            var data = await Task.Run(() => OpenAndRead(docxPath, token), token);
            try
            {
                return BuildFlowDocument(data, token);
            }
            finally
            {
                data?.WordDoc?.Dispose();
            }
        }

        private class DocumentData
        {
            public WordprocessingDocument WordDoc;
            public W.Body Body;
            public Dictionary<string, W.Style> StyleMap;
        }

        private static DocumentData OpenAndRead(string docxPath, CancellationToken token)
        {
            WordprocessingDocument wordDoc = null;
            try
            {
                wordDoc = WordprocessingDocument.Open(docxPath, false);
                var mainPart = wordDoc.MainDocumentPart;
                if (mainPart == null)
                    throw new InvalidOperationException("The document does not contain a main document part.");

                var body = mainPart.Document.Body;
                if (body == null)
                    throw new InvalidOperationException("The document does not contain a body.");

                var styleMap = BuildStyleMap(mainPart.StyleDefinitionsPart?.Styles);

                return new DocumentData
                {
                    WordDoc = wordDoc,
                    Body = body,
                    StyleMap = styleMap
                };
            }
            catch (OpenXmlPackageException ex)
            {
                wordDoc?.Dispose();
                throw new InvalidOperationException("The file is not a valid Word document. It may be corrupt or password-protected.", ex);
            }
            catch
            {
                wordDoc?.Dispose();
                throw;
            }
        }

        private static FlowDocument BuildFlowDocument(DocumentData data, CancellationToken token)
        {
            var flowDoc = new System.Windows.Documents.FlowDocument
            {
                FontFamily = new FontFamily("Calibri"),
                FontSize = 15.0, // 11 pt default
                PagePadding = new Thickness(0),
                Background = Brushes.Transparent
            };

            foreach (var element in data.Body.Elements())
            {
                token.ThrowIfCancellationRequested();

                switch (element)
                {
                    case W.Paragraph p:
                        flowDoc.Blocks.Add(ParseParagraph(p, data.StyleMap, data.WordDoc, token));
                        break;
                    case W.Table t:
                        flowDoc.Blocks.Add(ParseTable(t, data.StyleMap, data.WordDoc, token));
                        break;
                    case W.SectionProperties _:
                        // Section properties do not map to a preview document element.
                        break;
                }
            }

            return flowDoc;
        }

        #region Styles

        private static Dictionary<string, W.Style> BuildStyleMap(W.Styles styles)
        {
            var map = new Dictionary<string, W.Style>(StringComparer.OrdinalIgnoreCase);
            if (styles == null) return map;

            foreach (var style in styles.Elements<W.Style>())
            {
                if (!string.IsNullOrEmpty(style.StyleId?.Value))
                    map[style.StyleId.Value] = style;
            }
            return map;
        }

        private static IEnumerable<W.Style> GetStyleChain(string styleId, Dictionary<string, W.Style> styleMap)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(styleId) && styleMap.TryGetValue(styleId, out var style) && visited.Add(styleId))
            {
                yield return style;
                styleId = style.BasedOn?.Val?.Value;
            }
        }

        private static ParagraphFormat ResolveParagraphFormat(W.Paragraph paragraph, Dictionary<string, W.Style> styleMap)
        {
            var format = new ParagraphFormat();

            string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (string.IsNullOrEmpty(styleId))
                styleId = "Normal";

            foreach (var style in GetStyleChain(styleId, styleMap).Reverse())
            {
                ApplyParagraphProperties(paragraph.ParagraphProperties, format, direct: false);
                // Note: style paragraph properties are applied first (base), then direct overrides below.
            }

            ApplyParagraphProperties(paragraph.ParagraphProperties, format, direct: true);
            return format;
        }

        private static void ApplyParagraphProperties(W.ParagraphProperties props, ParagraphFormat format, bool direct)
        {
            if (props == null) return;

            if (direct || format.Alignment == null)
            {
                if (props.Justification?.Val != null)
                {
                    format.Alignment = ConvertJustification(props.Justification.Val.Value);
                }
            }

            if (props.SpacingBetweenLines != null)
            {
                var spacing = props.SpacingBetweenLines;
                if (direct || format.SpaceBeforePx == null)
                    format.SpaceBeforePx = TwipsToPixels(ParseTwips(spacing.Before?.Value));
                if (direct || format.SpaceAfterPx == null)
                    format.SpaceAfterPx = TwipsToPixels(ParseTwips(spacing.After?.Value));
                if (direct || format.LineSpacingPx == null)
                    format.LineSpacingPx = ParseLineSpacing(spacing);
            }

            if (props.Indentation != null)
            {
                var indent = props.Indentation;
                if (direct || format.LeftIndentPx == null)
                    format.LeftIndentPx = TwipsToPixels(ParseTwips(indent.Left?.Value));
                if (direct || format.RightIndentPx == null)
                    format.RightIndentPx = TwipsToPixels(ParseTwips(indent.Right?.Value));
                if (direct || format.FirstLineIndentPx == null)
                {
                    double first = TwipsToPixels(ParseTwips(indent.FirstLine?.Value));
                    double hanging = TwipsToPixels(ParseTwips(indent.Hanging?.Value));
                    format.FirstLineIndentPx = first != 0 ? first : -hanging;
                }
            }

            if (props.OutlineLevel?.Val != null && (direct || format.OutlineLevel == null))
                format.OutlineLevel = props.OutlineLevel.Val.Value;

            if (props.NumberingProperties != null && (direct || format.IsList == null))
                format.IsList = true;

            if (props.PageBreakBefore != null && (direct || format.PageBreakBefore == null))
                format.PageBreakBefore = props.PageBreakBefore.Val?.Value == true;

            if (props.KeepLines != null && (direct || format.KeepTogether == null))
                format.KeepTogether = props.KeepLines.Val?.Value == true;

            if (props.KeepNext != null && (direct || format.KeepNext == null))
                format.KeepNext = props.KeepNext.Val?.Value == true;
        }

        private static RunFormat ResolveRunFormat(W.Run run, W.Paragraph paragraph, Dictionary<string, W.Style> styleMap)
        {
            var format = new RunFormat();

            // 1. Paragraph style default run properties.
            string paraStyleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
            foreach (var style in GetStyleChain(paraStyleId, styleMap).Reverse())
            {
                ApplyRunPropertiesFromStyle(style, format);
            }

            // 2. Linked/character style applied to the run.
            string runStyleId = run.RunProperties?.RunStyle?.Val?.Value;
            if (!string.IsNullOrEmpty(runStyleId))
            {
                foreach (var style in GetStyleChain(runStyleId, styleMap).Reverse())
                {
                    ApplyRunPropertiesFromStyle(style, format);
                }
            }

            // 3. Direct run properties.
            ApplyRunProperties(run.RunProperties, format, direct: true);

            return format;
        }

        private static void ApplyRunProperties(W.RunProperties props, RunFormat format, bool direct)
        {
            if (props == null) return;

            if (props.Bold != null && (direct || format.Bold == null))
                format.Bold = props.Bold.Val?.Value != false;

            if (props.Italic != null && (direct || format.Italic == null))
                format.Italic = props.Italic.Val?.Value != false;

            if (props.Underline != null && (direct || format.Underline == null))
                format.Underline = props.Underline.Val?.Value != W.UnderlineValues.None;

            if (props.Strike != null && (direct || format.Strikethrough == null))
                format.Strikethrough = props.Strike.Val?.Value != false;

            if (props.DoubleStrike != null && (direct || format.DoubleStrikethrough == null))
                format.DoubleStrikethrough = props.DoubleStrike.Val?.Value != false;

            if (props.Caps != null && (direct || format.Caps == null))
                format.Caps = props.Caps.Val?.Value != false;

            if (props.SmallCaps != null && (direct || format.SmallCaps == null))
                format.SmallCaps = props.SmallCaps.Val?.Value != false;

            if (props.VerticalTextAlignment != null && (direct || format.Vertical == null))
            {
                var v = props.VerticalTextAlignment.Val?.Value;
                if (v == W.VerticalPositionValues.Subscript) format.Vertical = VerticalPosition.Subscript;
                else if (v == W.VerticalPositionValues.Superscript) format.Vertical = VerticalPosition.Superscript;
            }

            if (props.FontSize != null && (direct || format.FontSizePx == null))
            {
                if (double.TryParse(props.FontSize.Val?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var halfPts))
                    format.FontSizePx = halfPts * HalfPointsToPixelsFactor;
            }

            if (props.RunFonts != null && (direct || format.FontFamily == null))
            {
                var ascii = props.RunFonts.Ascii?.Value;
                if (!string.IsNullOrEmpty(ascii))
                    format.FontFamily = ascii;
            }

            if (props.Color != null && (direct || format.Foreground == null))
            {
                format.Foreground = ParseColor(props.Color.Val?.Value);
            }

            if (props.Highlight != null && (direct || format.Highlight == null))
            {
                format.Highlight = ParseHighlight(props.Highlight.Val?.Value.ToString());
            }
        }

        private static void ApplyRunPropertiesFromStyle(W.Style style, RunFormat format)
        {
            if (style?.StyleRunProperties == null) return;

            var srp = style.StyleRunProperties;

            if (srp.Bold != null && format.Bold == null)
                format.Bold = srp.Bold.Val?.Value != false;

            if (srp.Italic != null && format.Italic == null)
                format.Italic = srp.Italic.Val?.Value != false;

            if (srp.Underline != null && format.Underline == null)
                format.Underline = srp.Underline.Val?.Value != W.UnderlineValues.None;

            if (srp.Strike != null && format.Strikethrough == null)
                format.Strikethrough = srp.Strike.Val?.Value != false;

            if (srp.DoubleStrike != null && format.DoubleStrikethrough == null)
                format.DoubleStrikethrough = srp.DoubleStrike.Val?.Value != false;

            if (srp.Caps != null && format.Caps == null)
                format.Caps = srp.Caps.Val?.Value != false;

            if (srp.SmallCaps != null && format.SmallCaps == null)
                format.SmallCaps = srp.SmallCaps.Val?.Value != false;

            if (srp.VerticalTextAlignment != null && format.Vertical == null)
            {
                var v = srp.VerticalTextAlignment.Val?.Value;
                if (v == W.VerticalPositionValues.Subscript) format.Vertical = VerticalPosition.Subscript;
                else if (v == W.VerticalPositionValues.Superscript) format.Vertical = VerticalPosition.Superscript;
            }

            if (srp.FontSize != null && format.FontSizePx == null)
            {
                if (double.TryParse(srp.FontSize.Val?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var halfPts))
                    format.FontSizePx = halfPts * HalfPointsToPixelsFactor;
            }

            if (srp.RunFonts != null && format.FontFamily == null)
            {
                var ascii = srp.RunFonts.Ascii?.Value;
                if (!string.IsNullOrEmpty(ascii))
                    format.FontFamily = ascii;
            }

            if (srp.Color != null && format.Foreground == null)
                format.Foreground = ParseColor(srp.Color.Val?.Value);


        }

        #endregion

        #region Paragraphs

        private static System.Windows.Documents.Paragraph ParseParagraph(W.Paragraph p, Dictionary<string, W.Style> styleMap, WordprocessingDocument doc, CancellationToken token)
        {
            var format = ResolveParagraphFormat(p, styleMap);
            var para = new System.Windows.Documents.Paragraph();
            ApplyParagraphFormat(para, format);

            if (format.IsList == true)
            {
                para.Inlines.Add(new WRun("\u2022\u00A0") { FontFamily = new FontFamily("Segoe UI Symbol") });
            }

            foreach (var child in p.Elements())
            {
                token.ThrowIfCancellationRequested();

                switch (child)
                {
                    case W.Run r:
                        para.Inlines.Add(ParseRun(r, p, format, styleMap, doc));
                        break;
                    case W.Hyperlink h:
                        para.Inlines.Add(ParseHyperlink(h, p, format, styleMap, doc));
                        break;
                    case W.BookmarkStart _:
                    case W.BookmarkEnd _:
                    case W.ProofError _:
                        // Ignore bookmark/proofing boundaries for preview.
                        break;
                    case W.SdtRun sdt:
                        foreach (var sdtRun in sdt.Descendants<W.Run>())
                            para.Inlines.Add(ParseRun(sdtRun, p, format, styleMap, doc));
                        break;
                }
            }

            return para;
        }

        private static WInline ParseRun(W.Run run, W.Paragraph paragraph, ParagraphFormat paraFormat, Dictionary<string, W.Style> styleMap, WordprocessingDocument doc)
        {
            var format = ResolveRunFormat(run, paragraph, styleMap);
            var container = new WSpan();
            ApplyRunFormat(container, format);

            bool hasMultiple = run.Elements<OpenXmlElement>().Count() > 1 || run.Descendants<W.Drawing>().Any() || run.Descendants<W.Break>().Any() || run.Descendants<W.TabChar>().Any();

            if (!hasMultiple)
            {
                var text = run.GetFirstChild<W.Text>()?.Text ?? string.Empty;
                if (format.Caps == true) text = text.ToUpperInvariant();
                var simpleRun = new WRun(text);
                ApplyRunFormat(simpleRun, format);
                return simpleRun;
            }

            foreach (var child in run.Elements())
            {
                switch (child)
                {
                    case W.Text t:
                        string txt = t.Text ?? string.Empty;
                        if (format.Caps == true) txt = txt.ToUpperInvariant();
                        var textRun = new WRun(txt);
                        ApplyRunFormat(textRun, format);
                        container.Inlines.Add(textRun);
                        break;

                    case W.TabChar _:
                        container.Inlines.Add(new WRun("\t") { FontFamily = new FontFamily("Consolas") });
                        break;

                    case W.Break _:
                    case W.CarriageReturn _:
                        container.Inlines.Add(new LineBreak());
                        break;

                    case W.Drawing d:
                        var img = ParseDrawing(d, doc);
                        if (img != null)
                            container.Inlines.Add(img);
                        break;

                    case W.FootnoteReference _:
                    case W.EndnoteReference _:
                        // Footnotes are not rendered in the preview.
                        break;
                }
            }

            return container;
        }

        private static WInline ParseHyperlink(W.Hyperlink hyperlink, W.Paragraph paragraph, ParagraphFormat paraFormat, Dictionary<string, W.Style> styleMap, WordprocessingDocument doc)
        {
            string uriString = null;
            if (!string.IsNullOrEmpty(hyperlink.Id?.Value))
            {
                try
                {
                    var rel = doc.MainDocumentPart.GetExternalRelationship(hyperlink.Id.Value);
                    uriString = rel?.Uri?.OriginalString;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(uriString))
                uriString = hyperlink.Anchor?.Value;

            var link = new WHyperlink();
            if (!string.IsNullOrEmpty(uriString))
            {
                if (Uri.TryCreate(uriString, UriKind.RelativeOrAbsolute, out var uri))
                    link.NavigateUri = uri;
            }
            link.Foreground = new SolidColorBrush(WColor.FromRgb(0x05, 0x63, 0xC1));
            link.TextDecorations = TextDecorations.Underline;

            foreach (var child in hyperlink.Elements())
            {
                switch (child)
                {
                    case W.Run r:
                        link.Inlines.Add(ParseRun(r, paragraph, paraFormat, styleMap, doc));
                        break;
                    case W.Hyperlink h:
                        link.Inlines.Add(ParseHyperlink(h, paragraph, paraFormat, styleMap, doc));
                        break;
                }
            }

            return link;
        }

        private static void ApplyParagraphFormat(System.Windows.Documents.Paragraph para, ParagraphFormat format)
        {
            if (format.Alignment.HasValue)
                para.TextAlignment = (WTextAlignment)format.Alignment.Value;

            if (format.SpaceBeforePx.HasValue)
                para.Margin = new Thickness(para.Margin.Left, format.SpaceBeforePx.Value, para.Margin.Right, para.Margin.Bottom);

            if (format.SpaceAfterPx.HasValue)
                para.Margin = new Thickness(para.Margin.Left, para.Margin.Top, para.Margin.Right, format.SpaceAfterPx.Value);

            if (format.LineSpacingPx.HasValue)
                para.LineHeight = format.LineSpacingPx.Value;

            if (format.LeftIndentPx.HasValue || format.RightIndentPx.HasValue || format.FirstLineIndentPx.HasValue)
            {
                double left = format.LeftIndentPx ?? 0;
                double right = format.RightIndentPx ?? 0;
                double first = format.FirstLineIndentPx ?? 0;
                para.TextIndent = first;
                para.Margin = new Thickness(left, para.Margin.Top, right, para.Margin.Bottom);
            }

            if (format.KeepTogether == true)
                para.KeepTogether = true;
            if (format.KeepNext == true)
                para.KeepWithNext = true;

            if (format.PageBreakBefore == true)
                para.BreakPageBefore = true;

            // Headings: scale font size if this is an outline-level paragraph and no explicit size was inherited.
            if (format.OutlineLevel.HasValue)
            {
                para.FontWeight = FontWeights.Bold;
                if (format.OutlineLevel.Value >= 0 && format.OutlineLevel.Value <= 8)
                {
                    double[] sizes = { 26, 22, 18, 16, 14, 13, 12, 11, 11 };
                    para.FontSize = sizes[Math.Min(format.OutlineLevel.Value, sizes.Length - 1)];
                }
            }
        }

        private static void ApplyRunFormat(TextElement element, RunFormat format)
        {
            if (format.Bold == true)
                element.FontWeight = FontWeights.Bold;
            if (format.Italic == true)
                element.FontStyle = FontStyles.Italic;
            if (format.FontSizePx.HasValue)
                element.FontSize = format.FontSizePx.Value;
            if (!string.IsNullOrEmpty(format.FontFamily))
                element.FontFamily = new FontFamily(format.FontFamily);
            if (format.Foreground.HasValue)
                element.Foreground = new SolidColorBrush(format.Foreground.Value);
            if (format.Highlight.HasValue)
                element.Background = new SolidColorBrush(format.Highlight.Value);

            if (format.Underline == true || format.Strikethrough == true || format.DoubleStrikethrough == true)
            {
                var decorations = new TextDecorationCollection();
                if (format.Underline == true)
                {
                    foreach (var d in TextDecorations.Underline)
                        decorations.Add(d);
                }
                if (format.Strikethrough == true || format.DoubleStrikethrough == true)
                {
                    foreach (var d in TextDecorations.Strikethrough)
                        decorations.Add(d);
                }
                var inline = element as WInline;
                if (inline != null)
                    inline.TextDecorations = decorations;
            }

            if (format.SmallCaps == true)
                element.Typography.Capitals = FontCapitals.SmallCaps;

            if (format.Vertical.HasValue)
            {
                switch (format.Vertical.Value)
                {
                    case VerticalPosition.Subscript:
                        element.Typography.Variants = FontVariants.Subscript;
                        break;
                    case VerticalPosition.Superscript:
                        element.Typography.Variants = FontVariants.Superscript;
                        break;
                }
            }
        }

        #endregion

        #region Tables

        private static System.Windows.Documents.Table ParseTable(W.Table table, Dictionary<string, W.Style> styleMap, WordprocessingDocument doc, CancellationToken token)
        {
            var flowTable = new WTable
            {
                CellSpacing = 0,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            var grid = table.GetFirstChild<W.TableGrid>();
            if (grid != null)
            {
                foreach (var col in grid.Elements<W.GridColumn>())
                {
                    double widthPx = 0;
                    if (int.TryParse(col.Width?.Value, out var dxa))
                        widthPx = dxa / TwipsPerPixel;
                    flowTable.Columns.Add(new WTableColumn
                    {
                        Width = widthPx > 0 ? new GridLength(widthPx) : GridLength.Auto
                    });
                }
            }

            var rowGroup = new System.Windows.Documents.TableRowGroup();
            var activeMerges = new Dictionary<int, ActiveVerticalMerge>();
            int rowIndex = 0;

            foreach (var row in table.Elements<W.TableRow>())
            {
                token.ThrowIfCancellationRequested();
                var flowRow = new WTableRow();
                int colIdx = 0;

                foreach (var cell in row.Elements<W.TableCell>())
                {
                    var cellProps = cell.TableCellProperties;
                    var vMerge = cellProps?.VerticalMerge;
                    int gridSpan = 1;
                    if (cellProps?.GridSpan?.Val != null)
                        gridSpan = cellProps.GridSpan.Val.Value;

                    // Skip columns occupied by active vertical merges.
                    while (activeMerges.ContainsKey(colIdx))
                        colIdx++;

                    if (vMerge != null && (vMerge.Val == null || vMerge.Val.Value == W.MergedCellValues.Continue))
                    {
                        if (activeMerges.TryGetValue(colIdx, out var merge))
                        {
                            merge.Cell.RowSpan = rowIndex - merge.StartRowIndex + 1;
                        }
                        colIdx += gridSpan;
                        continue;
                    }

                    var flowCell = CreateTableCell(cell, styleMap, doc, token);
                    if (vMerge != null && vMerge.Val?.Value == W.MergedCellValues.Restart)
                    {
                        activeMerges[colIdx] = new ActiveVerticalMerge
                        {
                            StartRowIndex = rowIndex,
                            Cell = flowCell
                        };
                    }

                    if (gridSpan > 1)
                        flowCell.ColumnSpan = gridSpan;

                    ApplyTableCellBorders(flowCell, cellProps?.TableCellBorders);

                    flowRow.Cells.Add(flowCell);
                    colIdx += gridSpan;
                }

                // Clear merges that did not continue (Word normally requires explicit continue, but be tolerant).
                var keys = activeMerges.Keys.ToList();
                foreach (var key in keys)
                {
                    if (key >= colIdx)
                        activeMerges.Remove(key);
                }

                rowGroup.Rows.Add(flowRow);
                rowIndex++;
            }

            flowTable.RowGroups.Add(rowGroup);
            return flowTable;
        }

        private static WTableCell CreateTableCell(W.TableCell cell, Dictionary<string, W.Style> styleMap, WordprocessingDocument doc, CancellationToken token)
        {
            var flowCell = new WTableCell();
            var cellProps = cell.TableCellProperties;

            if (cellProps?.Shading != null && !string.IsNullOrEmpty(cellProps.Shading.Fill?.Value))
            {
                flowCell.Background = new SolidColorBrush(ParseColor(cellProps.Shading.Fill.Value));
            }

            double marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0;
            if (cellProps?.TableCellMargin != null)
            {
                marginLeft = TwipsToPixels(ParseTwips(cellProps.TableCellMargin.LeftMargin?.Width?.Value));
                marginRight = TwipsToPixels(ParseTwips(cellProps.TableCellMargin.RightMargin?.Width?.Value));
                marginTop = TwipsToPixels(ParseTwips(cellProps.TableCellMargin.TopMargin?.Width?.Value));
                marginBottom = TwipsToPixels(ParseTwips(cellProps.TableCellMargin.BottomMargin?.Width?.Value));
            }
            flowCell.Padding = new Thickness(marginLeft, marginTop, marginRight, marginBottom);

            foreach (var block in cell.Elements())
            {
                token.ThrowIfCancellationRequested();
                switch (block)
                {
                    case W.Paragraph p:
                        flowCell.Blocks.Add(ParseParagraph(p, styleMap, doc, token));
                        break;
                    case W.Table t:
                        flowCell.Blocks.Add(ParseTable(t, styleMap, doc, token));
                        break;
                }
            }

            return flowCell;
        }

        private static void ApplyTableCellBorders(WTableCell cell, W.TableCellBorders borders)
        {
            if (borders == null) return;

            bool hasAny = borders.TopBorder?.Val?.Value != W.BorderValues.Nil ||
                          borders.BottomBorder?.Val?.Value != W.BorderValues.Nil ||
                          borders.LeftBorder?.Val?.Value != W.BorderValues.Nil ||
                          borders.RightBorder?.Val?.Value != W.BorderValues.Nil;

            if (!hasAny) return;

            cell.BorderBrush = Brushes.Gray;
            cell.BorderThickness = new Thickness(
                borders.LeftBorder?.Val?.Value != W.BorderValues.Nil ? 0.5 : 0,
                borders.TopBorder?.Val?.Value != W.BorderValues.Nil ? 0.5 : 0,
                borders.RightBorder?.Val?.Value != W.BorderValues.Nil ? 0.5 : 0,
                borders.BottomBorder?.Val?.Value != W.BorderValues.Nil ? 0.5 : 0);
        }

        private class ActiveVerticalMerge
        {
            public int StartRowIndex { get; set; }
            public WTableCell Cell { get; set; }
        }

        #endregion

        #region Images

        private static InlineUIContainer ParseDrawing(W.Drawing drawing, WordprocessingDocument doc)
        {
            try
            {
                var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
                if (blip?.Embed == null) return null;

                var imagePart = doc.MainDocumentPart.GetPartById(blip.Embed.Value) as ImagePart;
                if (imagePart == null) return null;

                byte[] imageData;
                using (var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    imageData = ms.ToArray();
                }

                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(imageData))
                {
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                }
                bitmap.Freeze();

                var extents = drawing.Descendants<A.Extents>().FirstOrDefault();
                double widthPx = 0, heightPx = 0;
                if (extents != null)
                {
                    widthPx = EmusToPixels(extents.Cx);
                    heightPx = EmusToPixels(extents.Cy);
                }

                var image = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform
                };

                if (widthPx > 0 && heightPx > 0)
                {
                    image.Width = widthPx;
                    image.Height = heightPx;
                }

                return new InlineUIContainer(image);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Helpers

        private static WTextAlignment ConvertJustification(W.JustificationValues value)
        {
            switch (value)
            {
                case W.JustificationValues.Center: return WTextAlignment.Center;
                case W.JustificationValues.Right: return WTextAlignment.Right;
                case W.JustificationValues.Both: return WTextAlignment.Justify;
                default: return WTextAlignment.Left;
            }
        }

        private static double ParseTwips(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                return result;
            return 0;
        }

        private static double TwipsToPixels(double twips)
        {
            return twips / TwipsPerPixel;
        }

        private static double ParseLineSpacing(W.SpacingBetweenLines spacing)
        {
            if (spacing == null) return 0;

            var lineValue = spacing.Line?.Value;
            var lineRule = spacing.LineRule?.Value;

            if (string.IsNullOrEmpty(lineValue)) return 0;

            if (lineRule == W.LineSpacingRuleValues.Auto && double.TryParse(lineValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var autoLines))
            {
                // Auto line spacing: value is 240ths of a line for the default font size.
                // Approximate to a pixel value using a default 11 pt line.
                return autoLines / 240.0 * 15.0;
            }

            if (double.TryParse(lineValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var twips))
            {
                return TwipsToPixels(twips);
            }

            return 0;
        }

        private static double EmusToPixels(long? emus)
        {
            if (!emus.HasValue) return 0;
            return emus.Value / 914400.0 * 96.0;
        }

        private static WColor ParseColor(string hex)
        {
            if (string.Equals(hex, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(hex))
                return Colors.Transparent;
            if (hex.Length == 6 && TryParseHexColor(hex, out var color))
                return color;
            return Colors.Black;
        }

        private static bool TryParseHexColor(string hex, out WColor color)
        {
            color = Colors.Black;
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                color = WColor.FromRgb(r, g, b);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static WColor? ParseHighlight(string highlight)
        {
            if (string.IsNullOrWhiteSpace(highlight)) return null;
            switch (highlight.ToLowerInvariant())
            {
                case "yellow": return Color.FromRgb(0xFF, 0xFF, 0x00);
                case "green": return Color.FromRgb(0x00, 0xFF, 0x00);
                case "cyan": return Color.FromRgb(0x00, 0xFF, 0xFF);
                case "magenta": return Color.FromRgb(0xFF, 0x00, 0xFF);
                case "blue": return Color.FromRgb(0x00, 0x00, 0xFF);
                case "red": return Color.FromRgb(0xFF, 0x00, 0x00);
                case "darkblue": return Color.FromRgb(0x00, 0x00, 0x8B);
                case "darkcyan": return Color.FromRgb(0x00, 0x8B, 0x8B);
                case "darkgreen": return Color.FromRgb(0x00, 0x64, 0x00);
                case "darkmagenta": return Color.FromRgb(0x8B, 0x00, 0x8B);
                case "darkred": return Color.FromRgb(0x8B, 0x00, 0x00);
                case "darkyellow": return Color.FromRgb(0x80, 0x80, 0x00);
                case "lightgray": return Color.FromRgb(0xD3, 0xD3, 0xD3);
                case "gray": return Color.FromRgb(0x80, 0x80, 0x80);
                case "darkgray": return Color.FromRgb(0xA9, 0xA9, 0xA9);
                case "black": return Color.FromRgb(0x00, 0x00, 0x00);
                case "white": return Color.FromRgb(0xFF, 0xFF, 0xFF);
                default: return Color.FromRgb(0xFF, 0xFF, 0x00); // default to yellow
            }
        }

        private class ParagraphFormat
        {
            public WTextAlignment? Alignment { get; set; }
            public double? SpaceBeforePx { get; set; }
            public double? SpaceAfterPx { get; set; }
            public double? LineSpacingPx { get; set; }
            public double? LeftIndentPx { get; set; }
            public double? RightIndentPx { get; set; }
            public double? FirstLineIndentPx { get; set; }
            public int? OutlineLevel { get; set; }
            public bool? IsList { get; set; }
            public bool? PageBreakBefore { get; set; }
            public bool? KeepTogether { get; set; }
            public bool? KeepNext { get; set; }
        }

        private class RunFormat
        {
            public bool? Bold { get; set; }
            public bool? Italic { get; set; }
            public bool? Underline { get; set; }
            public bool? Strikethrough { get; set; }
            public bool? DoubleStrikethrough { get; set; }
            public bool? Caps { get; set; }
            public bool? SmallCaps { get; set; }
            public VerticalPosition? Vertical { get; set; }
            public double? FontSizePx { get; set; }
            public string FontFamily { get; set; }
            public WColor? Foreground { get; set; }
            public WColor? Highlight { get; set; }
        }

        private enum VerticalPosition
        {
            Subscript,
            Superscript
        }

        #endregion
    }
}
