using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Filey
{
    /// <summary>
    /// Simple converter that transforms a .doc or .docx Word document into Markdown.
    /// Supports basic text, headings, bold, italics, block quotes (as lines starting with '>'),
    /// and tables (converted to Markdown table syntax). This is a lightweight implementation
    /// using OpenXML SDK without any external dependencies.
    /// </summary>
    public static class WordToMarkdownConverter
    {
        public static void Convert(string sourcePath, string markdownPath)
        {
            if (string.IsNullOrEmpty(sourcePath)) throw new ArgumentException("sourcePath is required", nameof(sourcePath));
            if (string.IsNullOrEmpty(markdownPath)) throw new ArgumentException("markdownPath is required", nameof(markdownPath));

            using (var mdWriter = new StreamWriter(markdownPath, false, System.Text.Encoding.UTF8))
            {
                using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(sourcePath, false))
                {
                    var body = wordDoc.MainDocumentPart.Document.Body;
                    foreach (var element in body.Elements())
                    {
                        switch (element)
                        {
                            case Paragraph p:
                                WriteParagraph(p, mdWriter);
                                break;
                            case Table t:
                                WriteTable(t, mdWriter);
                                break;
                            default:
                                // Ignore other elements for now
                                break;
                        }
                    }
                }
            }
        }

        private static void WriteParagraph(Paragraph paragraph, StreamWriter writer)
        {
            var pPr = paragraph.ParagraphProperties;
            int? outlineLevel = null;
            if (pPr?.OutlineLevel != null)
            {
                outlineLevel = pPr.OutlineLevel.Val.Value;
            }

            bool isQuote = false;
            var runs = paragraph.Elements<Run>().ToList();
            // Detect line breaks within the paragraph
            bool hasLineBreak = paragraph.Descendants<Break>().Any();

            if (runs.Count > 0)
            {
                var firstText = runs.First().GetFirstChild<Text>()?.Text ?? string.Empty;
                if (firstText.StartsWith(">")) isQuote = true;
            }
            else if (!hasLineBreak)
            {
                // Empty paragraph without explicit line break – treat as a blank line separator
                writer.WriteLine();
                writer.WriteLine();
                return;
            }

            string line = string.Empty;
            if (outlineLevel.HasValue)
            {
                int headingLevel = Math.Min(outlineLevel.Value + 1, 6);
                line = new string('#', headingLevel) + " ";
            }
            else if (isQuote)
            {
                line = "> ";
            }

            var textSegments = new List<string>();
            foreach (var run in runs)
            {
                var runProps = run.RunProperties;
                bool isBold = runProps?.Bold != null;
                bool isItalic = runProps?.Italic != null;
                var txt = run.GetFirstChild<Text>()?.Text ?? string.Empty;
                if (isBold && isItalic)
                {
                    txt = "***" + txt + "***";
                }
                else if (isBold)
                {
                    txt = "**" + txt + "**";
                }
                else if (isItalic)
                {
                    txt = "*" + txt + "*";
                }
                textSegments.Add(txt);
            }
            line += string.Concat(textSegments);

            // Append markdown line break if the original Word paragraph contains explicit line breaks
            if (hasLineBreak)
            {
                // Two spaces followed by newline is markdown soft break
                writer.WriteLine(line + "  ");
            }
            else
            {
                writer.WriteLine(line);
            }
            writer.WriteLine();
        }

        private static void WriteTable(Table table, StreamWriter writer)
        {
            var rows = table.Elements<TableRow>().ToList();
            if (rows.Count == 0) return;

            var headerCells = rows[0].Elements<TableCell>().ToList();
            var headerTexts = headerCells.Select(c => GetCellText(c)).ToArray();
            writer.WriteLine("| " + string.Join(" | ", headerTexts) + " |");
            writer.WriteLine("| " + string.Join(" | ", headerTexts.Select(_ => "---")) + " |");

            foreach (var row in rows.Skip(1))
            {
                var cells = row.Elements<TableCell>().ToList();
                var cellTexts = cells.Select(c => GetCellText(c)).ToArray();
                writer.WriteLine("| " + string.Join(" | ", cellTexts) + " |");
            }
            writer.WriteLine();
        }

        private static string GetCellText(TableCell cell)
        {
            var texts = cell.Descendants<Text>().Select(t => t.Text.Trim());
            return string.Join(" ", texts);
        }
    }
}
