using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;

namespace Filey
{
    public static class DocxToFlowDocumentConverter
    {
        /// <summary>
        /// Converts a .docx file to a FlowDocument.
        /// Runs on a background thread; UI thread marshaling is the caller's responsibility.
        /// </summary>
        public static async Task<FlowDocument> ConvertAsync(string docxPath, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                var flowDoc = new FlowDocument();
                using (var wordDoc = WordprocessingDocument.Open(docxPath, false))
                {
                    var body = wordDoc.MainDocumentPart.Document.Body;
                    var styleDefs = wordDoc.MainDocumentPart.StyleDefinitionsPart?.Styles ?? new Styles();
                    foreach (var element in body.Elements())
                    {
                        token.ThrowIfCancellationRequested();
                        switch (element)
                        {
                            case Paragraph p:
                                flowDoc.Blocks.Add(ParseParagraph(p, styleDefs));
                                break;
                            case Table t:
                                flowDoc.Blocks.Add(ParseTable(t, styleDefs));
                                break;
                        }
                    }
                }
                return flowDoc;
            }, token);
        }

        private static Paragraph ParseParagraph(Paragraph p, Styles styles)
        {
            var para = new Paragraph();
            if (p.ParagraphProperties?.Justification?.Val != null)
            {
                switch (p.ParagraphProperties.Justification.Val.Value)
                {
                    case JustificationValues.Center:
                        para.TextAlignment = TextAlignment.Center; break;
                    case JustificationValues.Right:
                        para.TextAlignment = TextAlignment.Right; break;
                    case JustificationValues.Both:
                        para.TextAlignment = TextAlignment.Justify; break;
                    default:
                        para.TextAlignment = TextAlignment.Left; break;
                }
            }
            foreach (var run in p.Elements<Run>())
            {
                para.Inlines.Add(ParseRun(run, styles));
            }
            foreach (var br in p.Elements<Break>())
            {
                para.Inlines.Add(new LineBreak());
            }
            return para;
        }

        private static Inline ParseRun(Run run, Styles styles)
        {
            var text = run.GetFirstChild<Text>()?.Text ?? string.Empty;
            var runProps = run.RunProperties;
            var inline = new Run(text);
            if (runProps != null)
            {
                if (runProps.Bold != null) inline.FontWeight = FontWeights.Bold;
                if (runProps.Italic != null) inline.FontStyle = FontStyles.Italic;
                if (runProps.Underline != null) inline.TextDecorations = TextDecorations.Underline;
                if (runProps.Strike != null) inline.TextDecorations = TextDecorations.Strikethrough;
                if (runProps.FontSize != null && double.TryParse(runProps.FontSize.Val, out var sizeHalfPts))
                {
                    inline.FontSize = sizeHalfPts * 2.0 / 3.0;
                }
                if (runProps.Color != null && !string.IsNullOrEmpty(runProps.Color.Val))
                {
                    inline.Foreground = new SolidColorBrush(ParseColor(runProps.Color.Val));
                }
            }
            return inline;
        }

        private static Block ParseTable(Table table, Styles styles)
        {
            var flowTable = new Table();
            var grid = table.GetFirstChild<TableGrid>();
            if (grid != null)
            {
                foreach (var col in grid.Elements<GridColumn>())
                {
                    double widthPx = 0;
                    if (int.TryParse(col.Width?.Value, out var dxa))
                    {
                        widthPx = dxa / 15.0;
                    }
                    flowTable.Columns.Add(new TableColumn { Width = new GridLength(widthPx) });
                }
            }
            var rowGroup = new TableRowGroup();
            var vMergeMap = new Dictionary<int, TableCell>();
            foreach (var row in table.Elements<TableRow>())
            {
                var flowRow = new TableRow();
                int colIdx = 0;
                foreach (var cell in row.Elements<TableCell>())
                {
                    var vMerge = cell.TableCellProperties?.VerticalMerge;
                    if (vMerge != null && vMerge.Val != null && vMerge.Val.Value == MergedCellValues.Restart)
                    {
                        var newCell = CreateTableCell(cell, styles);
                        vMergeMap[colIdx] = newCell;
                        flowRow.Cells.Add(newCell);
                    }
                    else if (vMerge != null && (vMerge.Val == null || vMerge.Val.Value == MergedCellValues.Continue))
                    {
                        if (vMergeMap.TryGetValue(colIdx, out var existingCell))
                        {
                            var innerCell = CreateTableCell(cell, styles);
                            foreach (var blk in innerCell.Blocks)
                                existingCell.Blocks.Add(blk);
                        }
                    }
                    else
                    {
                        flowRow.Cells.Add(CreateTableCell(cell, styles));
                    }
                    var gridSpan = cell.TableCellProperties?.GridSpan?.Val;
                    if (gridSpan != null && int.TryParse(gridSpan, out var span))
                    {
                        flowRow.Cells[flowRow.Cells.Count - 1].ColumnSpan = span;
                    }
                    colIdx++;
                }
                rowGroup.Rows.Add(flowRow);
            }
            flowTable.RowGroups.Add(rowGroup);
            return flowTable;
        }

        private static TableCell CreateTableCell(TableCell cell, Styles styles)
        {
            var flowCell = new TableCell();
            var shading = cell.TableCellProperties?.Shading;
            if (shading != null && !string.IsNullOrEmpty(shading.Fill))
            {
                flowCell.Background = new SolidColorBrush(ParseColor(shading.Fill));
            }
            var borders = cell.TableCellProperties?.TableCellBorders;
            if (borders != null)
            {
                flowCell.BorderBrush = Brushes.Gray;
                flowCell.BorderThickness = new Thickness(1);
            }
            foreach (var block in cell.Elements())
            {
                switch (block)
                {
                    case Paragraph p:
                        flowCell.Blocks.Add(ParseParagraph(p, styles));
                        break;
                    case Table t:
                        flowCell.Blocks.Add(ParseTable(t, styles));
                        break;
                }
            }
            return flowCell;
        }

        private static Color ParseColor(string hex)
        {
            if (string.Equals(hex, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(hex))
                return Colors.Transparent;
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromRgb(r, g, b);
            }
            return Colors.Black;
        }
    }
}
