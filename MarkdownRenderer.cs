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
            string html = RenderToHtml(blocks, ThemeService.IsDark);

            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"filey_md_{Math.Abs(markdownFilePath.GetHashCode())}.html");

            File.WriteAllText(tempPath, html, new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
        }

        public static string RenderToHtml(IReadOnlyList<Block> blocks, bool dark = true)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<title>Preview</title>\n<style>\n");
            sb.Append(BuildCss(dark));
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

        // Theme-specific CSS custom properties. Only this :root block differs between Light and
        // Dark; CssRules below references everything via var(...). The values mirror the WPF
        // palette in Themes/Colors.*.xaml so the markdown preview matches the rest of the app.
        private const string DarkRoot = @":root{
    --bg:#1E1E1E; --text:#E0E0E0; --heading:#F0F0F0; --border:#333333; --border2:#2A2A2A;
    --h5:#C8C8C8; --h6:#A0A0A0; --strong:#F5F5F5; --del:#888888;
    --code-bg:#2A2A2A; --code-text:#CE9178; --code-border:#333333;
    --pre-bg:#141414; --pre-border:#2D2D2D; --accent:#EAB308; --pre-code:#D4D4D4;
    --quote-bg:#242424; --quote-text:#B8B8B8;
    --th-bg:#252525; --td-border:#2D2D2D; --tr-even:#222222; --tr-hover:#2A2A2A;
    --link:#60A5FA; --img-border:#2D2D2D;
}";

        private const string LightRoot = @":root{
    --bg:#FFFFFF; --text:#1A1A1A; --heading:#111111; --border:#DDDDDD; --border2:#E5E5E5;
    --h5:#333333; --h6:#6A6A6A; --strong:#000000; --del:#888888;
    --code-bg:#F0F0F0; --code-text:#A3370F; --code-border:#DDDDDD;
    --pre-bg:#F6F6F6; --pre-border:#DDDDDD; --accent:#B8860B; --pre-code:#222222;
    --quote-bg:#F3F3F3; --quote-text:#555555;
    --th-bg:#F0F0F0; --td-border:#DDDDDD; --tr-even:#F7F7F7; --tr-hover:#EFEFEF;
    --link:#0067C0; --img-border:#DDDDDD;
}";

        private const string CssRules = @"*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
html { font-size: 15px; }
body {
    background: var(--bg); color: var(--text);
    font-family: 'Segoe UI', system-ui, sans-serif;
    line-height: 1.7; padding: 28px 36px;
    max-width: 880px; margin: 0 auto;
}
h1,h2,h3,h4,h5,h6 { font-weight:600; line-height:1.25; margin-top:1.5em; margin-bottom:.5em; color:var(--heading); }
h1 { font-size:2em;   border-bottom:1px solid var(--border); padding-bottom:.3em; }
h2 { font-size:1.5em; border-bottom:1px solid var(--border2); padding-bottom:.25em; }
h3 { font-size:1.25em; } h4 { font-size:1.1em; }
h5 { font-size:.95em; color:var(--h5); } h6 { font-size:.85em; color:var(--h6); }
p  { margin-bottom:1em; }
strong { font-weight:700; color:var(--strong); } em { font-style:italic; }
del { color:var(--del); }
code {
    font-family: 'Cascadia Code', Consolas, 'Courier New', monospace;
    font-size:.88em; background:var(--code-bg); color:var(--code-text);
    padding:.15em .4em; border-radius:3px; border:1px solid var(--code-border);
}
pre {
    background:var(--pre-bg); border:1px solid var(--pre-border);
    border-left:3px solid var(--accent); border-radius:4px;
    padding:14px 18px; margin-bottom:1.2em; overflow-x:auto;
}
pre code { background:transparent; border:none; padding:0; color:var(--pre-code); font-size:.9em; line-height:1.6; }
blockquote {
    margin:1em 0; padding:.6em 1em .6em 1.2em;
    border-left:4px solid var(--accent); background:var(--quote-bg);
    border-radius:0 4px 4px 0; color:var(--quote-text);
}
blockquote p:last-child { margin-bottom:0; }
ul, ol { margin:.5em 0 1em 1.5em; padding:0; }
li { margin-bottom:.3em; }
li > ul, li > ol { margin-top:.3em; }
input[type=""checkbox""] { accent-color:var(--accent); margin-right:.4em; vertical-align:middle; }
hr { border:none; border-top:1px solid var(--border); margin:1.5em 0; }
table { border-collapse:collapse; width:100%; margin-bottom:1.2em; font-size:.93em; }
th { background:var(--th-bg); color:var(--accent); font-weight:600; text-align:left; padding:8px 12px; border:1px solid var(--border); }
td { padding:7px 12px; border:1px solid var(--td-border); }
tr:nth-child(even) td { background:var(--tr-even); }
tr:hover td { background:var(--tr-hover); }
a { color:var(--link); text-decoration:none; border-bottom:1px solid transparent; }
a:hover { border-bottom-color:var(--link); }
img { max-width:100%; height:auto; border-radius:4px; border:1px solid var(--img-border); }";

        private static string BuildCss(bool dark) => (dark ? DarkRoot : LightRoot) + "\n" + CssRules;
    }
}
