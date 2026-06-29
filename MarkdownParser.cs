using System;
using System.Collections.Generic;
using System.Text;

namespace Filey
{
    public abstract class Block { }

    public sealed class HeadingBlock : Block
    {
        public int Level;
        public List<Inline> Inlines = new List<Inline>();
    }

    public sealed class ParagraphBlock : Block
    {
        public List<Inline> Inlines = new List<Inline>();
    }

    public sealed class FencedCodeBlock : Block
    {
        public string Language = "";
        public string Text = "";
    }

    public sealed class IndentedCodeBlock : Block
    {
        public string Text = "";
    }

    public sealed class BlockquoteBlock : Block
    {
        public List<Block> Children = new List<Block>();
    }

    public sealed class ListItemBlock
    {
        public List<Block> Children = new List<Block>();
        public bool? IsChecked; // null = not a task item
    }

    public sealed class BulletListBlock : Block
    {
        public List<ListItemBlock> Items = new List<ListItemBlock>();
    }

    public sealed class OrderedListBlock : Block
    {
        public int Start = 1;
        public List<ListItemBlock> Items = new List<ListItemBlock>();
    }

    public sealed class HorizontalRule : Block { }

    public enum ColumnAlignment { None, Left, Center, Right }

    public sealed class TableBlock : Block
    {
        public List<List<Inline>> Headers = new List<List<Inline>>();
        public List<ColumnAlignment> Alignments = new List<ColumnAlignment>();
        public List<List<List<Inline>>> Rows = new List<List<List<Inline>>>();
    }

    public abstract class Inline { }

    public sealed class TextRun : Inline { public string Text = ""; }
    public sealed class CodeSpan : Inline { public string Code = ""; }
    public sealed class BoldItalic : Inline { public List<Inline> Children = new List<Inline>(); }
    public sealed class Bold : Inline { public List<Inline> Children = new List<Inline>(); }
    public sealed class Italic : Inline { public List<Inline> Children = new List<Inline>(); }
    public sealed class Strikethrough : Inline { public List<Inline> Children = new List<Inline>(); }
    public sealed class LinkInline : Inline { public string Text = ""; public string Url = ""; }
    public sealed class ImageInline : Inline { public string Alt = ""; public string Url = ""; }
    public sealed class HardBreak : Inline { }

    public static class MarkdownParser
    {
        public static List<Block> Parse(string markdown)
        {
            markdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = markdown.Split('\n');
            return ParseBlocks(lines, 0, lines.Length);
        }

        private static List<Block> ParseBlocks(string[] lines, int start, int end)
        {
            var blocks = new List<Block>();
            int i = start;

            while (i < end)
            {
                string line = lines[i];

                if (line.Trim().Length == 0) { i++; continue; }

                if (IsFenceStart(line, out string fenceChar, out int fenceLen, out string lang))
                {
                    i = ParseFencedCode(lines, i, end, fenceChar, fenceLen, lang, blocks);
                    continue;
                }

                if (IsThematicBreak(line))
                {
                    blocks.Add(new HorizontalRule());
                    i++;
                    continue;
                }

                if (TryGetHeading(line, out int level, out string headingText))
                {
                    blocks.Add(new HeadingBlock { Level = level, Inlines = ParseInlines(headingText) });
                    i++;
                    continue;
                }

                if (IsBlockquote(line))
                {
                    i = ParseBlockquote(lines, i, end, blocks);
                    continue;
                }

                if (IsListItem(line, out _, out _, out _, out _))
                {
                    i = ParseList(lines, i, end, blocks);
                    continue;
                }

                if (IsTableStart(lines, i, end))
                {
                    i = ParseTable(lines, i, end, blocks);
                    continue;
                }

                if (IsIndentedCode(line))
                {
                    i = ParseIndentedCode(lines, i, end, blocks);
                    continue;
                }

                i = ParseParagraph(lines, i, end, blocks);
            }

            return blocks;
        }

        private static bool IsFenceStart(string line, out string fenceChar, out int fenceLen, out string lang)
        {
            fenceChar = ""; fenceLen = 0; lang = "";
            string trimmed = line.TrimStart();
            if (trimmed.Length < 3) return false;
            char c = trimmed[0];
            if (c != '`' && c != '~') return false;
            int count = 0;
            while (count < trimmed.Length && trimmed[count] == c) count++;
            if (count < 3) return false;
            fenceChar = c.ToString();
            fenceLen = count;
            lang = trimmed.Substring(count).Trim();
            // Backtick info strings may not contain backticks
            if (c == '`' && lang.Contains("`")) return false;
            return true;
        }

        private static int ParseFencedCode(string[] lines, int i, int end, string fenceChar, int fenceLen, string lang, List<Block> blocks)
        {
            var sb = new StringBuilder();
            i++; // skip opening fence
            while (i < end)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.Length >= fenceLen && IsAllSameChar(trimmed, fenceChar[0], fenceLen))
                {
                    i++; // consume closing fence
                    break;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                i++;
            }
            blocks.Add(new FencedCodeBlock { Language = lang, Text = sb.ToString() });
            return i;
        }

        private static bool IsAllSameChar(string s, char c, int minLen)
        {
            int count = 0;
            foreach (char ch in s)
            {
                if (ch == c) count++;
                else if (char.IsWhiteSpace(ch)) break;
                else return false;
            }
            return count >= minLen;
        }

        private static bool IsThematicBreak(string line)
        {
            string t = line.Trim();
            if (t.Length < 3) return false;
            char c = t[0];
            if (c != '-' && c != '*' && c != '_') return false;
            int count = 0;
            foreach (char ch in t)
            {
                if (ch == c) count++;
                else if (ch != ' ') return false;
            }
            return count >= 3;
        }

        private static bool TryGetHeading(string line, out int level, out string text)
        {
            level = 0; text = "";
            int idx = 0;
            while (idx < line.Length && line[idx] == ' ' && idx < 3) idx++;
            int hashStart = idx;
            while (idx < line.Length && line[idx] == '#') idx++;
            level = idx - hashStart;
            if (level < 1 || level > 6) return false;
            if (idx < line.Length && line[idx] != ' ') return false;
            text = line.Substring(idx).Trim();
            // strip optional trailing #'s
            text = text.TrimEnd();
            while (text.Length > 0 && text[text.Length - 1] == '#') text = text.Substring(0, text.Length - 1);
            text = text.TrimEnd();
            return true;
        }

        private static bool IsBlockquote(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith(">");
        }

        private static int ParseBlockquote(string[] lines, int i, int end, List<Block> blocks)
        {
            var inner = new List<string>();
            while (i < end)
            {
                string line = lines[i];
                string t = line.TrimStart();
                if (!t.StartsWith(">"))
                {
                    if (t.Length == 0) break;
                    break;
                }
                string content = t.Substring(1);
                if (content.StartsWith(" ")) content = content.Substring(1);
                inner.Add(content);
                i++;
            }
            var innerArr = inner.ToArray();
            blocks.Add(new BlockquoteBlock { Children = ParseBlocks(innerArr, 0, innerArr.Length) });
            return i;
        }

        private static int CountIndent(string line)
        {
            int indent = 0;
            foreach (char c in line)
            {
                if (c == ' ') indent++;
                else if (c == '\t') indent += 4;
                else break;
            }
            return indent;
        }

        private static bool IsListItem(string line, out bool ordered, out int start, out string content, out bool? isChecked)
        {
            ordered = false; start = 1; content = ""; isChecked = null;
            string t = line.TrimStart();
            if (t.Length == 0) return false;

            // bullet
            if ((t[0] == '-' || t[0] == '*' || t[0] == '+') && t.Length >= 2 && t[1] == ' ')
            {
                content = t.Substring(2);
                isChecked = TryGetTaskMarker(ref content);
                return true;
            }

            // ordered: digits followed by '.' or ')'
            int d = 0;
            while (d < t.Length && char.IsDigit(t[d])) d++;
            if (d > 0 && d < t.Length && (t[d] == '.' || t[d] == ')') && d + 1 < t.Length && t[d + 1] == ' ')
            {
                ordered = true;
                int.TryParse(t.Substring(0, d), out start);
                content = t.Substring(d + 2);
                return true;
            }
            return false;
        }

        private static bool? TryGetTaskMarker(ref string content)
        {
            if (content.Length >= 3 && content[0] == '[' && content[2] == ']')
            {
                char mark = content[1];
                if (mark == ' ' || mark == 'x' || mark == 'X')
                {
                    bool isChecked = (mark == 'x' || mark == 'X');
                    string rest = content.Substring(3);
                    if (rest.StartsWith(" ")) rest = rest.Substring(1);
                    content = rest;
                    return isChecked;
                }
            }
            return null;
        }

        private static int ParseList(string[] lines, int i, int end, List<Block> blocks)
        {
            IsListItem(lines[i], out bool ordered, out int start, out _, out _);
            var items = new List<ListItemBlock>();
            int baseIndent = CountIndent(lines[i]);

            while (i < end)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // peek: blank then continued list keeps the list; otherwise terminate
                    if (i + 1 < end && IsListItem(lines[i + 1], out bool no, out _, out _, out _)
                        && CountIndent(lines[i + 1]) <= baseIndent && no == ordered)
                    {
                        i++;
                        continue;
                    }
                    break;
                }

                int indent = CountIndent(line);
                if (indent < baseIndent) break;

                if (indent == baseIndent && IsListItem(line, out bool thisOrdered, out _, out string content, out bool? isChecked))
                {
                    if (thisOrdered != ordered) break;

                    var itemLines = new List<string> { content };
                    i++;

                    // Gather continuation/nested lines (indented deeper than the marker)
                    int markerWidth = line.Length - line.TrimStart().Length;
                    string trimmedStart = line.TrimStart();
                    int contentIndent = baseIndent + (trimmedStart.Length - content.Length - CountTrailingTaskOffset(trimmedStart, content));
                    // Fallback: treat anything indented past baseIndent as belonging to the item
                    while (i < end)
                    {
                        string next = lines[i];
                        if (next.Trim().Length == 0)
                        {
                            if (i + 1 < end && CountIndent(lines[i + 1]) > baseIndent)
                            {
                                itemLines.Add("");
                                i++;
                                continue;
                            }
                            break;
                        }
                        int nextIndent = CountIndent(next);
                        if (nextIndent > baseIndent)
                        {
                            itemLines.Add(StripIndent(next, baseIndent + 2));
                            i++;
                            continue;
                        }
                        break;
                    }

                    var itemArr = itemLines.ToArray();
                    var children = ParseBlocks(itemArr, 0, itemArr.Length);
                    items.Add(new ListItemBlock { Children = children, IsChecked = isChecked });
                }
                else
                {
                    break;
                }
            }

            if (ordered)
                blocks.Add(new OrderedListBlock { Start = start, Items = items });
            else
                blocks.Add(new BulletListBlock { Items = items });
            return i;
        }

        private static int CountTrailingTaskOffset(string trimmedStart, string content)
        {
            return 0;
        }

        private static string StripIndent(string line, int amount)
        {
            int removed = 0;
            int idx = 0;
            while (idx < line.Length && removed < amount)
            {
                if (line[idx] == ' ') { removed++; idx++; }
                else if (line[idx] == '\t') { removed += 4; idx++; }
                else break;
            }
            return line.Substring(idx);
        }

        private static bool IsIndentedCode(string line)
        {
            return CountIndent(line) >= 4 && line.Trim().Length > 0;
        }

        private static int ParseIndentedCode(string[] lines, int i, int end, List<Block> blocks)
        {
            var sb = new StringBuilder();
            while (i < end)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // allow blank lines inside if followed by more indented code
                    if (i + 1 < end && IsIndentedCode(lines[i + 1]))
                    {
                        sb.Append('\n');
                        i++;
                        continue;
                    }
                    break;
                }
                if (!IsIndentedCode(line)) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(StripIndent(line, 4));
                i++;
            }
            blocks.Add(new IndentedCodeBlock { Text = sb.ToString() });
            return i;
        }

        private static bool IsTableStart(string[] lines, int i, int end)
        {
            if (i + 1 >= end) return false;
            if (!lines[i].Contains("|")) return false;
            return IsTableSeparator(lines[i + 1]);
        }

        private static bool IsTableSeparator(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || !t.Contains("-")) return false;
            foreach (char c in t)
            {
                if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
            }
            return t.Contains("-");
        }

        private static int ParseTable(string[] lines, int i, int end, List<Block> blocks)
        {
            var table = new TableBlock();
            var headerCells = SplitTableRow(lines[i]);
            foreach (var cell in headerCells) table.Headers.Add(ParseInlines(cell));
            i++;

            var sepCells = SplitTableRow(lines[i]);
            foreach (var cell in sepCells)
            {
                string c = cell.Trim();
                bool left = c.StartsWith(":");
                bool right = c.EndsWith(":");
                if (left && right) table.Alignments.Add(ColumnAlignment.Center);
                else if (right) table.Alignments.Add(ColumnAlignment.Right);
                else if (left) table.Alignments.Add(ColumnAlignment.Left);
                else table.Alignments.Add(ColumnAlignment.None);
            }
            i++;

            while (i < end && lines[i].Contains("|") && lines[i].Trim().Length > 0)
            {
                var cells = SplitTableRow(lines[i]);
                var row = new List<List<Inline>>();
                foreach (var cell in cells) row.Add(ParseInlines(cell));
                table.Rows.Add(row);
                i++;
            }

            blocks.Add(table);
            return i;
        }

        // Splits a table row on unescaped '|', honoring backslash-escaped pipes (\|).
        private static List<string> SplitTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !(t.Length >= 2 && t[t.Length - 2] == '\\')) t = t.Substring(0, t.Length - 1);

            var cells = new List<string>();
            var sb = new StringBuilder();
            for (int j = 0; j < t.Length; j++)
            {
                char c = t[j];
                if (c == '\\' && j + 1 < t.Length && t[j + 1] == '|')
                {
                    sb.Append('|');
                    j++;
                }
                else if (c == '|')
                {
                    cells.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        private static int ParseParagraph(string[] lines, int i, int end, List<Block> blocks)
        {
            var sb = new StringBuilder();
            while (i < end)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) break;
                if (TryGetHeading(line, out _, out _)) break;
                if (IsThematicBreak(line)) break;
                if (IsFenceStart(line, out _, out _, out _)) break;
                if (IsBlockquote(line)) break;
                if (IsListItem(line, out _, out _, out _, out _)) break;
                if (IsTableStart(lines, i, end)) break;

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                i++;
            }
            blocks.Add(new ParagraphBlock { Inlines = ParseInlines(sb.ToString()) });
            return i;
        }

        // ----- Inline parsing -----

        public static List<Inline> ParseInlines(string text)
        {
            var result = new List<Inline>();
            var buffer = new StringBuilder();
            int i = 0;

            void FlushText()
            {
                if (buffer.Length > 0)
                {
                    result.Add(new TextRun { Text = buffer.ToString() });
                    buffer.Clear();
                }
            }

            while (i < text.Length)
            {
                char c = text[i];

                // Backslash escape: next char is literal (or hard break at line end)
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == '\n')
                    {
                        FlushText();
                        result.Add(new HardBreak());
                        i += 2;
                        continue;
                    }
                    if (IsEscapable(next))
                    {
                        buffer.Append(next);
                        i += 2;
                        continue;
                    }
                }

                // Hard break: two+ trailing spaces before newline
                if (c == '\n')
                {
                    int trailingSpaces = 0;
                    int k = buffer.Length - 1;
                    while (k >= 0 && buffer[k] == ' ') { trailingSpaces++; k--; }
                    if (trailingSpaces >= 2)
                    {
                        buffer.Length -= trailingSpaces;
                        FlushText();
                        result.Add(new HardBreak());
                    }
                    else
                    {
                        buffer.Append(' ');
                    }
                    i++;
                    continue;
                }

                // Inline code span with variable-length backtick runs
                if (c == '`')
                {
                    int run = 0;
                    while (i + run < text.Length && text[i + run] == '`') run++;
                    int close = FindClosingBacktickRun(text, i + run, run);
                    if (close >= 0)
                    {
                        FlushText();
                        string code = text.Substring(i + run, close - (i + run));
                        code = code.Trim('\n', ' ').Length == 0 ? code : TrimCodeSpan(code);
                        result.Add(new CodeSpan { Code = code });
                        i = close + run;
                        continue;
                    }
                    buffer.Append(c);
                    i++;
                    continue;
                }

                // Image: ![alt](url)
                if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    if (TryParseLinkLike(text, i + 1, out string alt, out string url, out int consumed))
                    {
                        FlushText();
                        result.Add(new ImageInline { Alt = alt, Url = url });
                        i = i + 1 + consumed;
                        continue;
                    }
                }

                // Link: [text](url)
                if (c == '[')
                {
                    if (TryParseLinkLike(text, i, out string linkText, out string url, out int consumed))
                    {
                        FlushText();
                        result.Add(new LinkInline { Text = linkText, Url = url });
                        i += consumed;
                        continue;
                    }
                }

                // Emphasis / strong / strikethrough
                if (c == '*' || c == '_' || c == '~')
                {
                    int run = 0;
                    while (i + run < text.Length && text[i + run] == c) run++;

                    if (c == '~')
                    {
                        if (run >= 2 && TryParseDelimited(text, i, "~~", out string inner, out int consumed))
                        {
                            FlushText();
                            result.Add(new Strikethrough { Children = ParseInlines(inner) });
                            i += consumed;
                            continue;
                        }
                    }
                    else
                    {
                        if (run >= 3 && TryParseDelimited(text, i, new string(c, 3), out string inner3, out int consumed3))
                        {
                            FlushText();
                            result.Add(new BoldItalic { Children = ParseInlines(inner3) });
                            i += consumed3;
                            continue;
                        }
                        if (run >= 2 && TryParseDelimited(text, i, new string(c, 2), out string inner2, out int consumed2))
                        {
                            FlushText();
                            result.Add(new Bold { Children = ParseInlines(inner2) });
                            i += consumed2;
                            continue;
                        }
                        if (TryParseDelimited(text, i, c.ToString(), out string inner1, out int consumed1))
                        {
                            FlushText();
                            result.Add(new Italic { Children = ParseInlines(inner1) });
                            i += consumed1;
                            continue;
                        }
                    }
                }

                buffer.Append(c);
                i++;
            }

            FlushText();
            return result;
        }

        private static bool IsEscapable(char c)
        {
            return "\\`*_{}[]()#+-.!|~>\"".IndexOf(c) >= 0;
        }

        private static string TrimCodeSpan(string code)
        {
            // CommonMark: strip one leading and trailing space if both present and content is non-space
            if (code.Length >= 2 && code[0] == ' ' && code[code.Length - 1] == ' ' && code.Trim().Length > 0)
                return code.Substring(1, code.Length - 2);
            return code;
        }

        private static int FindClosingBacktickRun(string text, int from, int runLen)
        {
            int i = from;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    int run = 0;
                    while (i + run < text.Length && text[i + run] == '`') run++;
                    if (run == runLen) return i;
                    i += run;
                }
                else
                {
                    i++;
                }
            }
            return -1;
        }

        private static bool TryParseDelimited(string text, int start, string delim, out string inner, out int consumed)
        {
            inner = ""; consumed = 0;
            int contentStart = start + delim.Length;
            if (contentStart >= text.Length) return false;
            if (text[contentStart] == ' ' || text[contentStart] == '\n') return false; // no leading whitespace after open

            int search = contentStart;
            while (search < text.Length)
            {
                int idx = text.IndexOf(delim, search, StringComparison.Ordinal);
                if (idx < 0) return false;
                // closing delimiter must not be preceded by whitespace and not be escaped
                if (idx > contentStart && text[idx - 1] != ' ' && text[idx - 1] != '\n' && !IsEscaped(text, idx))
                {
                    // for single-char delimiters, make sure we don't match part of a longer run wrongly
                    inner = text.Substring(contentStart, idx - contentStart);
                    consumed = (idx + delim.Length) - start;
                    return true;
                }
                search = idx + 1;
            }
            return false;
        }

        private static bool IsEscaped(string text, int idx)
        {
            int backslashes = 0;
            int k = idx - 1;
            while (k >= 0 && text[k] == '\\') { backslashes++; k--; }
            return (backslashes % 2) == 1;
        }

        private static bool TryParseLinkLike(string text, int start, out string label, out string url, out int consumed)
        {
            label = ""; url = ""; consumed = 0;
            if (start >= text.Length || text[start] != '[') return false;

            int depth = 0;
            int i = start;
            int labelEnd = -1;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { labelEnd = i; break; }
                }
            }
            if (labelEnd < 0) return false;
            if (labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(') return false;

            int urlStart = labelEnd + 2;
            int parenDepth = 1;
            int urlEnd = -1;
            for (int j = urlStart; j < text.Length; j++)
            {
                char c = text[j];
                if (c == '\\') { j++; continue; }
                if (c == '(') parenDepth++;
                else if (c == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0) { urlEnd = j; break; }
                }
            }
            if (urlEnd < 0) return false;

            label = text.Substring(start + 1, labelEnd - (start + 1));
            string rawUrl = text.Substring(urlStart, urlEnd - urlStart).Trim();
            // strip optional "title"
            int sp = rawUrl.IndexOf(' ');
            if (sp >= 0) rawUrl = rawUrl.Substring(0, sp);
            url = rawUrl;
            consumed = (urlEnd + 1) - start;
            return true;
        }
    }
}
