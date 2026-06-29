using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Filey
{
    public static class MarkdownRenderer
    {
        public static void OpenInBrowser(string markdownFilePath)
        {
            string markdown = File.ReadAllText(markdownFilePath);
            var blocks = MarkdownParser.Parse(markdown);
            string html = RenderToHtml(blocks);

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"filey_md_{Math.Abs(markdownFilePath.GetHashCode())}.html");

            File.WriteAllText(tempPath, html, new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
        }

        public static string RenderToHtml(IReadOnlyList<Block> blocks)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<title>Preview</title>\n<style>\n");
            sb.Append(Css);
            sb.Append("\n</style>\n</head>\n<body>\n");
            RenderBlocks(blocks, sb);
            sb.Append("\n</body>\n</html>");
            return sb.ToString();
        }

        private static void RenderBlocks(IReadOnlyList<Block> blocks, StringBuilder sb)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case HeadingBlock h:
                        sb.Append($"<h{h.Level}>");
                        RenderInlines(h.Inlines, sb);
                        sb.Append($"</h{h.Level}>\n");
                        break;
                    case ParagraphBlock p:
                        sb.Append("<p>");
                        RenderInlines(p.Inlines, sb);
                        sb.Append("</p>\n");
                        break;
                    case FencedCodeBlock fc:
                        sb.Append("<pre><code>");
                        sb.Append(Escape(fc.Text));
                        sb.Append("</code></pre>\n");
                        break;
                    case IndentedCodeBlock ic:
                        sb.Append("<pre><code>");
                        sb.Append(Escape(ic.Text));
                        sb.Append("</code></pre>\n");
                        break;
                    case BlockquoteBlock bq:
                        sb.Append("<blockquote>\n");
                        RenderBlocks(bq.Children, sb);
                        sb.Append("</blockquote>\n");
                        break;
                    case BulletListBlock ul:
                        sb.Append("<ul>\n");
                        RenderListItems(ul.Items, sb);
                        sb.Append("</ul>\n");
                        break;
                    case OrderedListBlock ol:
                        sb.Append(ol.Start != 1 ? $"<ol start=\"{ol.Start}\">\n" : "<ol>\n");
                        RenderListItems(ol.Items, sb);
                        sb.Append("</ol>\n");
                        break;
                    case HorizontalRule _:
                        sb.Append("<hr>\n");
                        break;
                    case TableBlock t:
                        RenderTable(t, sb);
                        break;
                }
            }
        }

        private static void RenderListItems(List<ListItemBlock> items, StringBuilder sb)
        {
            foreach (var item in items)
            {
                sb.Append("<li>");
                if (item.IsChecked.HasValue)
                {
                    sb.Append(item.IsChecked.Value
                        ? "<input type=\"checkbox\" checked disabled> "
                        : "<input type=\"checkbox\" disabled> ");
                }
                RenderListItemChildren(item.Children, sb);
                sb.Append("</li>\n");
            }
        }

        // A single tight paragraph inside a list item renders inline (no <p> wrapper);
        // anything richer (nested lists, multiple blocks) renders as full blocks.
        private static void RenderListItemChildren(List<Block> children, StringBuilder sb)
        {
            if (children.Count == 1 && children[0] is ParagraphBlock only)
            {
                RenderInlines(only.Inlines, sb);
                return;
            }
            RenderBlocks(children, sb);
        }

        private static void RenderTable(TableBlock t, StringBuilder sb)
        {
            sb.Append("<table>\n<thead>\n<tr>");
            for (int c = 0; c < t.Headers.Count; c++)
            {
                sb.Append($"<th{AlignStyle(t, c)}>");
                RenderInlines(t.Headers[c], sb);
                sb.Append("</th>");
            }
            sb.Append("</tr>\n</thead>\n<tbody>\n");
            foreach (var row in t.Rows)
            {
                sb.Append("<tr>");
                for (int c = 0; c < row.Count; c++)
                {
                    sb.Append($"<td{AlignStyle(t, c)}>");
                    RenderInlines(row[c], sb);
                    sb.Append("</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }

        private static string AlignStyle(TableBlock t, int col)
        {
            if (col >= t.Alignments.Count) return "";
            switch (t.Alignments[col])
            {
                case ColumnAlignment.Left: return " style=\"text-align:left\"";
                case ColumnAlignment.Center: return " style=\"text-align:center\"";
                case ColumnAlignment.Right: return " style=\"text-align:right\"";
                default: return "";
            }
        }

        private static void RenderInlines(IReadOnlyList<Inline> inlines, StringBuilder sb)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextRun t:
                        sb.Append(Escape(t.Text));
                        break;
                    case CodeSpan cs:
                        sb.Append("<code>");
                        sb.Append(Escape(cs.Code));
                        sb.Append("</code>");
                        break;
                    case BoldItalic bi:
                        sb.Append("<strong><em>");
                        RenderInlines(bi.Children, sb);
                        sb.Append("</em></strong>");
                        break;
                    case Bold b:
                        sb.Append("<strong>");
                        RenderInlines(b.Children, sb);
                        sb.Append("</strong>");
                        break;
                    case Italic em:
                        sb.Append("<em>");
                        RenderInlines(em.Children, sb);
                        sb.Append("</em>");
                        break;
                    case Strikethrough s:
                        sb.Append("<del>");
                        RenderInlines(s.Children, sb);
                        sb.Append("</del>");
                        break;
                    case LinkInline l:
                        sb.Append($"<a href=\"{EscapeAttr(l.Url)}\">");
                        sb.Append(Escape(l.Text));
                        sb.Append("</a>");
                        break;
                    case ImageInline img:
                        sb.Append($"<img src=\"{EscapeAttr(img.Url)}\" alt=\"{EscapeAttr(img.Alt)}\">");
                        break;
                    case HardBreak _:
                        sb.Append("<br>\n");
                        break;
                }
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
        }

        private static string EscapeAttr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Escape(s).Replace("\"", "&quot;");
        }

        private const string Css = @"*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
html { font-size: 15px; }
body {
    background: #1E1E1E; color: #E0E0E0;
    font-family: 'Segoe UI', system-ui, sans-serif;
    line-height: 1.7; padding: 28px 36px;
    max-width: 880px; margin: 0 auto;
}
h1,h2,h3,h4,h5,h6 { font-weight:600; line-height:1.25; margin-top:1.5em; margin-bottom:.5em; color:#F0F0F0; }
h1 { font-size:2em;   border-bottom:1px solid #333; padding-bottom:.3em; }
h2 { font-size:1.5em; border-bottom:1px solid #2A2A2A; padding-bottom:.25em; }
h3 { font-size:1.25em; } h4 { font-size:1.1em; }
h5 { font-size:.95em; color:#C8C8C8; } h6 { font-size:.85em; color:#A0A0A0; }
p  { margin-bottom:1em; }
strong { font-weight:700; color:#F5F5F5; } em { font-style:italic; }
del { color:#888; }
code {
    font-family: 'Cascadia Code', Consolas, 'Courier New', monospace;
    font-size:.88em; background:#2A2A2A; color:#CE9178;
    padding:.15em .4em; border-radius:3px; border:1px solid #333;
}
pre {
    background:#141414; border:1px solid #2D2D2D;
    border-left:3px solid #EAB308; border-radius:4px;
    padding:14px 18px; margin-bottom:1.2em; overflow-x:auto;
}
pre code { background:transparent; border:none; padding:0; color:#D4D4D4; font-size:.9em; line-height:1.6; }
blockquote {
    margin:1em 0; padding:.6em 1em .6em 1.2em;
    border-left:4px solid #EAB308; background:#242424;
    border-radius:0 4px 4px 0; color:#B8B8B8;
}
blockquote p:last-child { margin-bottom:0; }
ul, ol { margin:.5em 0 1em 1.5em; padding:0; }
li { margin-bottom:.3em; }
li > ul, li > ol { margin-top:.3em; }
input[type=""checkbox""] { accent-color:#EAB308; margin-right:.4em; vertical-align:middle; }
hr { border:none; border-top:1px solid #333; margin:1.5em 0; }
table { border-collapse:collapse; width:100%; margin-bottom:1.2em; font-size:.93em; }
th { background:#252525; color:#EAB308; font-weight:600; text-align:left; padding:8px 12px; border:1px solid #333; }
td { padding:7px 12px; border:1px solid #2D2D2D; }
tr:nth-child(even) td { background:#222; }
tr:hover td { background:#2A2A2A; }
a { color:#60A5FA; text-decoration:none; border-bottom:1px solid transparent; }
a:hover { border-bottom-color:#60A5FA; }
img { max-width:100%; height:auto; border-radius:4px; border:1px solid #2D2D2D; }";
    }
}
