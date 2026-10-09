// Markdown -> RTF renderer.
//
// The preview is a native RichEdit control, so "rendering" here means emitting
// RTF. No layout engine, no DOM, no scripting - just string building, which is
// why a repaint costs about a millisecond on a normal document.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

struct Style
{
    public bool Bold, Italic, Strike, Mono, Under;
    public int Color;      // colour-table index, 0 = inherit
    public int Back;       // highlight index, 0 = none
    public string Link;
}

class LinkSpan
{
    public int Start, Length;
    public string Url;
}

class Palette
{
    public Color Text, Muted, Accent, CodeBg, Border, MarkBg;

    public static Palette Dark()
    {
        Palette p = new Palette();
        p.Text = Color.FromArgb(0xD6, 0xDA, 0xE1);
        p.Muted = Color.FromArgb(0x85, 0x8D, 0x9B);
        p.Accent = Color.FromArgb(0x6A, 0xA3, 0xFF);
        p.CodeBg = Color.FromArgb(0x1E, 0x23, 0x2C);
        p.Border = Color.FromArgb(0x39, 0x40, 0x4E);
        p.MarkBg = Color.FromArgb(0x5A, 0x4A, 0x12);
        return p;
    }

    public static Palette Light()
    {
        Palette p = new Palette();
        p.Text = Color.FromArgb(0x1B, 0x1F, 0x24);
        p.Muted = Color.FromArgb(0x6B, 0x72, 0x80);
        p.Accent = Color.FromArgb(0x25, 0x63, 0xEB);
        p.CodeBg = Color.FromArgb(0xF0, 0xF1, 0xF4);
        p.Border = Color.FromArgb(0xC9, 0xCE, 0xD6);
        p.MarkBg = Color.FromArgb(0xFD, 0xE6, 0x8A);
        return p;
    }
}

static class Md
{
    // colour-table slots
    const int CText = 1, CMuted = 2, CAccent = 3, CCodeBg = 4, CBorder = 5, CMarkBg = 6;

    static readonly Regex ReFence = new Regex(@"^ {0,3}(`{3,}|~{3,})\s*([^`\n]*)$", RegexOptions.Compiled);
    static readonly Regex ReFEnd = new Regex(@"^ {0,3}(`{3,}|~{3,})\s*$", RegexOptions.Compiled);
    static readonly Regex ReHead = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+([^\n]*?))?[ \t]*#*[ \t]*$", RegexOptions.Compiled);
    static readonly Regex ReHr = new Regex(@"^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$", RegexOptions.Compiled);
    static readonly Regex ReQuote = new Regex(@"^ {0,3}>", RegexOptions.Compiled);
    static readonly Regex ReUli = new Regex(@"^([ \t]*)([-*+])([ \t]+)(.*)$", RegexOptions.Compiled);
    static readonly Regex ReOli = new Regex(@"^([ \t]*)(\d{1,9})([.)])([ \t]+)(.*)$", RegexOptions.Compiled);
    static readonly Regex ReTSep = new Regex(@"^ {0,3}\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);
    static readonly Regex ReSetx = new Regex(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.Compiled);
    static readonly Regex ReInd = new Regex(@"^(?: {4}|\t)", RegexOptions.Compiled);
    static readonly Regex ReQStrip = new Regex(@"^ {0,3}> ?", RegexOptions.Compiled);

    // ------------------------------------------------------------------ entry

    public static string ToRtf(string src, Palette p, string bodyFont, string monoFont,
                               int bodyPt, out List<LinkSpan> links)
    {
        Ctx c = new Ctx();
        c.Pal = p;
        c.BodyHalf = bodyPt * 2;
        c.MonoHalf = (int)Math.Round(bodyPt * 2 * 0.92);

        c.Rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        c.Rtf.Append(@"{\fonttbl{\f0\fswiss ").Append(bodyFont).Append(";}{\\f1\\fmodern ")
             .Append(monoFont).Append(";}}");
        c.Rtf.Append(@"{\colortbl;");
        AppendColor(c.Rtf, p.Text);
        AppendColor(c.Rtf, p.Muted);
        AppendColor(c.Rtf, p.Accent);
        AppendColor(c.Rtf, p.CodeBg);
        AppendColor(c.Rtf, p.Border);
        AppendColor(c.Rtf, p.MarkBg);
        c.Rtf.Append("}");
        c.Rtf.Append(@"\viewkind4\f0\cf1\fs").Append(c.BodyHalf).Append(' ');

        List<string> blocks = SplitBlocks(src);
        RenderBlocks(c, blocks, 0);

        c.Rtf.Append('}');
        links = c.Links;
        return c.Rtf.ToString();
    }

    static void AppendColor(StringBuilder sb, Color c)
    {
        sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
    }

    class Ctx
    {
        public StringBuilder Rtf = new StringBuilder(1 << 16);
        public int PlainLen;                       // mirrors the RichEdit char index
        public List<LinkSpan> Links = new List<LinkSpan>();
        public Palette Pal;
        public int BodyHalf, MonoHalf;
        public int QuoteDepth;          // >0 means we are inside a blockquote
    }

    // ------------------------------------------------------------- block scan

    static int IndentOf(string l)
    {
        int n = 0;
        for (int i = 0; i < l.Length; i++)
        {
            if (l[i] == ' ') n++;
            else if (l[i] == '\t') n += 4;
            else break;
        }
        return n;
    }

    static string Dedent(string l, int n)
    {
        int i = 0, col = 0;
        while (i < l.Length && col < n)
        {
            if (l[i] == ' ') { col++; i++; }
            else if (l[i] == '\t') { col += 4; i++; }
            else break;
        }
        return l.Substring(i);
    }

    static bool IsList(string l) { return !ReHr.IsMatch(l) && (ReOli.IsMatch(l) || ReUli.IsMatch(l)); }

    static bool IsTable(IList<string> lines, int i)
    {
        return lines[i].IndexOf('|') >= 0 && i + 1 < lines.Count
               && ReTSep.IsMatch(lines[i + 1]) && lines[i + 1].IndexOf('-') >= 0;
    }

    public static List<string> SplitBlocks(string src)
    {
        string[] lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<string> outp = new List<string>();
        int n = lines.Length, i = 0;
        while (i < n)
        {
            if (lines[i].Trim().Length == 0) { i++; continue; }
            int start = i;
            string line = lines[i];
            Match m = ReFence.Match(line);
            if (m.Success)
            {
                char ch = m.Groups[1].Value[0];
                int len = m.Groups[1].Value.Length;
                i++;
                while (i < n)
                {
                    Match e = ReFEnd.Match(lines[i]);
                    i++;
                    if (e.Success && e.Groups[1].Value[0] == ch && e.Groups[1].Value.Length >= len) break;
                }
            }
            else if (ReHr.IsMatch(line) || ReHead.IsMatch(line))
            {
                i++;
            }
            else if (ReQuote.IsMatch(line))
            {
                i++;
                while (i < n && lines[i].Trim().Length > 0
                       && !ReFence.IsMatch(lines[i]) && !ReHr.IsMatch(lines[i])) i++;
            }
            else if (IsList(line))
            {
                bool ord0 = ReOli.IsMatch(line);
                int lbase = IndentOf(line);
                i++;
                while (i < n)
                {
                    string l = lines[i];
                    if (l.Trim().Length == 0)
                    {
                        int j = i;
                        while (j < n && lines[j].Trim().Length == 0) j++;
                        if (j >= n) break;
                        if (IndentOf(lines[j]) >= lbase + 2) { i = j; continue; }
                        if (IsList(lines[j]) && ReOli.IsMatch(lines[j]) == ord0) { i = j; continue; }
                        break;
                    }
                    if (ReHr.IsMatch(l) || ReHead.IsMatch(l)) break;
                    if (IsList(l) && IndentOf(l) <= lbase + 1 && ReOli.IsMatch(l) != ord0) break;
                    i++;
                }
            }
            else if (IsTable(lines, i))
            {
                i += 2;
                while (i < n && lines[i].Trim().Length > 0 && lines[i].IndexOf('|') >= 0) i++;
            }
            else if (ReInd.IsMatch(line))
            {
                i++;
                while (i < n && (ReInd.IsMatch(lines[i]) || lines[i].Trim().Length == 0)) i++;
                while (i > start + 1 && lines[i - 1].Trim().Length == 0) i--;
            }
            else
            {
                i++;
                while (i < n)
                {
                    string pl = lines[i];
                    if (pl.Trim().Length == 0) break;
                    if (ReSetx.IsMatch(pl)) { i++; break; }
                    if (ReFence.IsMatch(pl) || ReHead.IsMatch(pl) || ReHr.IsMatch(pl)
                        || ReQuote.IsMatch(pl) || IsList(pl) || IsTable(lines, i)) break;
                    i++;
                }
            }
            outp.Add(string.Join("\n", lines, start, i - start));
        }
        return outp;
    }

    // ----------------------------------------------------------- block render

    static void RenderBlocks(Ctx c, List<string> blocks, int depth)
    {
        for (int i = 0; i < blocks.Count; i++) RenderBlock(c, blocks[i], depth, 0);
    }

    static void RenderBlock(Ctx c, string raw, int depth, int indentTwips)
    {
        if (depth > 6) { Para(c, raw, indentTwips, new Style()); return; }
        string[] lines = raw.Split('\n');
        string first = lines[0];

        Match m = ReFence.Match(first);
        if (m.Success)
        {
            List<string> body = new List<string>(lines);
            body.RemoveAt(0);
            if (body.Count > 0 && ReFEnd.IsMatch(body[body.Count - 1])) body.RemoveAt(body.Count - 1);
            CodeBlock(c, body, indentTwips);
            return;
        }
        if (ReHr.IsMatch(first)) { Rule(c, indentTwips); return; }

        m = ReHead.Match(first);
        if (m.Success) { Heading(c, m.Groups[2].Value, m.Groups[1].Value.Length, indentTwips); return; }

        if (lines.Length > 1 && ReSetx.IsMatch(lines[lines.Length - 1]))
        {
            int lv = lines[lines.Length - 1].Trim()[0] == '=' ? 1 : 2;
            Heading(c, string.Join("\n", lines, 0, lines.Length - 1), lv, indentTwips);
            return;
        }
        if (ReQuote.IsMatch(first))
        {
            StringBuilder inner = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) inner.Append('\n');
                inner.Append(ReQStrip.Replace(lines[i], ""));
            }
            RenderQuote(c, inner.ToString(), depth, indentTwips + 320);
            return;
        }
        if (IsList(first)) { RenderList(c, lines, depth, indentTwips); return; }
        if (IsTable(lines, 0)) { RenderTable(c, lines, indentTwips); return; }
        if (ReInd.IsMatch(first))
        {
            List<string> body = new List<string>();
            foreach (string l in lines) body.Add(ReInd.Replace(l, ""));
            CodeBlock(c, body, indentTwips);
            return;
        }
        Para(c, raw, indentTwips, new Style());
    }

    // RichEdit ignores paragraph borders, so a quote reads as indent + muted
    // text, with a bar glyph opening the first line of each paragraph.
    static void RenderQuote(Ctx c, string inner, int depth, int indentTwips)
    {
        List<string> blocks = SplitBlocks(inner);
        c.QuoteDepth++;
        foreach (string b in blocks) RenderBlock(c, b, depth + 1, indentTwips);
        c.QuoteDepth--;
    }

    static void Heading(Ctx c, string text, int level, int indentTwips)
    {
        int[] sizes = { 0, 190, 155, 132, 118, 108, 100 };
        int half = (int)Math.Round(c.BodyHalf * sizes[level] / 100.0);
        c.Rtf.Append(@"\pard\li").Append(indentTwips)
             .Append(@"\sb").Append(level <= 2 ? 260 : 200).Append(@"\sa").Append(90);
        if (level <= 2)
            c.Rtf.Append(@"\brdrb\brdrs\brdrw10\brdrcf").Append(CBorder).Append(@"\brsp40");
        c.Rtf.Append(@"\fs").Append(half).Append(' ');
        Style st = new Style();
        st.Bold = true;
        if (level >= 6) st.Color = CMuted;
        Inline(c, text, st);
        EndPara(c);
        c.Rtf.Append(@"\fs").Append(c.BodyHalf).Append(' ');
    }

    static void Para(Ctx c, string text, int indentTwips, Style st)
    {
        if (c.QuoteDepth > 0 && st.Color == 0) st.Color = CMuted;
        c.Rtf.Append(@"\pard\li").Append(indentTwips).Append(@"\sa130 ");
        Inline(c, text, st);
        EndPara(c);
    }

    // RichEdit drops \brdrb, so the rule is drawn with box-drawing characters
    static void Rule(Ctx c, int indentTwips)
    {
        c.Rtf.Append(@"\pard\li").Append(indentTwips).Append(@"\sb170\sa170 ");
        Style st = new Style();
        st.Mono = true;
        st.Color = CBorder;
        Emit(c, new string('─', 46), st);
        EndPara(c);
    }

    static void CodeBlock(Ctx c, List<string> body, int indentTwips)
    {
        // pad every line to the block's widest so the highlight forms a slab
        int wide = 0;
        foreach (string l in body) if (l.Length > wide) wide = l.Length;
        wide += 1;
        c.Rtf.Append(@"\pard\li").Append(indentTwips + 200).Append(@"\sb120\sa140 ");
        Style st = new Style();
        st.Mono = true;
        st.Back = CCodeBg;
        for (int i = 0; i < body.Count; i++)
        {
            string line = body[i].Replace("\t", "    ");
            Emit(c, line.PadRight(wide), st);
            if (i < body.Count - 1) Break(c);
        }
        EndPara(c);
    }

    static void RenderTable(Ctx c, string[] lines, int indentTwips)
    {
        List<string[]> rows = new List<string[]>();
        string[] head = SplitRow(TrimPipes(lines[0]));
        string[] seps = SplitRow(TrimPipes(lines[1]));
        rows.Add(head);
        for (int r = 2; r < lines.Length; r++)
        {
            if (lines[r].Trim().Length == 0) continue;
            rows.Add(SplitRow(TrimPipes(lines[r])));
        }
        int cols = head.Length;
        int[] w = new int[cols];
        foreach (string[] row in rows)
            for (int i = 0; i < cols; i++)
            {
                string cell = i < row.Length ? StripInline(row[i]) : "";
                if (cell.Length > w[i]) w[i] = cell.Length;
            }
        bool[] right = new bool[cols];
        bool[] center = new bool[cols];
        for (int i = 0; i < cols && i < seps.Length; i++)
        {
            string s = seps[i].Trim();
            bool l = s.StartsWith(":");
            bool rr = s.EndsWith(":");
            right[i] = rr && !l;
            center[i] = rr && l;
        }

        c.Rtf.Append(@"\pard\li").Append(indentTwips + 200).Append(@"\sb120\sa140 ");
        Style st = new Style();
        st.Mono = true;
        for (int r = 0; r < rows.Count; r++)
        {
            StringBuilder line = new StringBuilder();
            for (int i = 0; i < cols; i++)
            {
                string cell = i < rows[r].Length ? StripInline(rows[r][i]) : "";
                int pad = w[i] - cell.Length;
                if (center[i]) cell = new string(' ', pad / 2) + cell + new string(' ', pad - pad / 2);
                else if (right[i]) cell = new string(' ', pad) + cell;
                else cell = cell + new string(' ', pad);
                if (i > 0) line.Append("  ");
                line.Append(cell);
            }
            Style rowStyle = st;
            rowStyle.Bold = (r == 0);
            Emit(c, line.ToString(), rowStyle);
            Break(c);
            if (r == 0)
            {
                StringBuilder sep = new StringBuilder();
                for (int i = 0; i < cols; i++)
                {
                    if (i > 0) sep.Append("  ");
                    sep.Append(new string('─', w[i]));
                }
                Style sepStyle = st;
                sepStyle.Color = CMuted;
                Emit(c, sep.ToString(), sepStyle);
                Break(c);
            }
        }
        EndPara(c);
    }

    static string TrimPipes(string l)
    {
        string s = l.Trim();
        if (s.StartsWith("|")) s = s.Substring(1);
        if (s.EndsWith("|") && !s.EndsWith("\\|")) s = s.Substring(0, s.Length - 1);
        return s;
    }

    static string[] SplitRow(string s)
    {
        List<string> outp = new List<string>();
        StringBuilder cur = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '\\' && i + 1 < s.Length && s[i + 1] == '|') { cur.Append('|'); i += 2; continue; }
            if (ch == '`')
            {
                int j = s.IndexOf('`', i + 1);
                if (j < 0) j = s.Length - 1;
                cur.Append(s, i, Math.Min(j + 1, s.Length) - i);
                i = j + 1;
                continue;
            }
            if (ch == '|') { outp.Add(cur.ToString().Trim()); cur.Length = 0; i++; continue; }
            cur.Append(ch);
            i++;
        }
        outp.Add(cur.ToString().Trim());
        return outp.ToArray();
    }

    // table cells are laid out in a fixed-width grid, so drop inline markers
    static string StripInline(string s)
    {
        s = Regex.Replace(s, @"\*\*\*(.+?)\*\*\*", "$1");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "$1");
        s = Regex.Replace(s, @"\*(.+?)\*", "$1");
        s = Regex.Replace(s, @"`(.+?)`", "$1");
        s = Regex.Replace(s, @"~~(.+?)~~", "$1");
        s = Regex.Replace(s, @"==(.+?)==", "$1");
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        return s.Replace("\\|", "|");
    }

    // ------------------------------------------------------------------ lists

    class Item
    {
        public List<string> Src = new List<string>();
        public int ContentIndent;
    }

    static void RenderList(Ctx c, string[] lines, int depth, int indentTwips)
    {
        Match om0 = ReOli.Match(lines[0]);
        bool ordered = om0.Success;
        int lbase = IndentOf(lines[0]);
        int number = ordered ? int.Parse(om0.Groups[2].Value, CultureInfo.InvariantCulture) : 1;

        List<Item> items = new List<Item>();
        Item cur = null;
        bool loose = false, blank = false;
        foreach (string l in lines)
        {
            if (l.Trim().Length == 0) { if (cur != null) blank = true; continue; }
            int ind = IndentOf(l);
            Match om = null, um = null;
            if (ind <= lbase + 1 && !ReHr.IsMatch(l))
            {
                om = ReOli.Match(l);
                if (!om.Success) { um = ReUli.Match(l); om = null; }
            }
            if (om != null || (um != null && um.Success))
            {
                if (cur != null && blank) loose = true;
                blank = false;
                cur = new Item();
                if (om != null)
                {
                    cur.Src.Add(om.Groups[5].Value);
                    cur.ContentIndent = ind + om.Groups[2].Value.Length + om.Groups[3].Value.Length
                                        + om.Groups[4].Value.Length;
                }
                else
                {
                    cur.Src.Add(um.Groups[4].Value);
                    cur.ContentIndent = ind + um.Groups[2].Value.Length + um.Groups[3].Value.Length;
                }
                items.Add(cur);
            }
            else if (cur != null)
            {
                if (blank) { cur.Src.Add(""); loose = true; blank = false; }
                cur.Src.Add(Dedent(l, cur.ContentIndent));
            }
        }

        // wide enough for the widest marker we emit - a checkbox glyph or "10."
        int li = indentTwips + 380;
        foreach (Item it in items)
        {
            string src = string.Join("\n", it.Src.ToArray());
            string marker;
            bool task = false, done = false;
            Match tm = Regex.Match(src, @"^\[([ xX])\][ \t]+");
            if (tm.Success)
            {
                task = true;
                done = tm.Groups[1].Value != " ";
                src = src.Substring(tm.Length);
            }
            if (task) marker = done ? "☑" : "☐";
            else if (ordered) marker = (number++).ToString(CultureInfo.InvariantCulture) + ".";
            else marker = "•";

            List<string> blocks = SplitBlocks(src);
            string firstPara = null;
            int startBlock = 0;
            if (blocks.Count > 0 && IsPlainPara(blocks[0]))
            {
                firstPara = blocks[0];
                startBlock = 1;
            }

            // hanging indent with an explicit tab stop, so the marker and the
            // text sit a fixed distance apart instead of at a default tab stop
            c.Rtf.Append(@"\pard\li").Append(li).Append(@"\fi-380\tx").Append(li)
                 .Append(@"\sa").Append(loose ? 130 : 40).Append(' ');
            Style ms = new Style();
            ms.Color = c.QuoteDepth > 0 ? CMuted : CText;
            if (task) ms.Color = done ? CMuted : CText;
            Emit(c, marker, ms);
            Emit(c, "\t", new Style());
            if (firstPara != null)
            {
                Style ps = new Style();
                if (c.QuoteDepth > 0) ps.Color = CMuted;
                if (task && done) { ps.Strike = true; ps.Color = CMuted; }
                Inline(c, firstPara, ps);
            }
            EndPara(c);

            for (int b = startBlock; b < blocks.Count; b++)
                RenderBlock(c, blocks[b], depth + 1, li);
        }
    }

    static bool IsPlainPara(string block)
    {
        string first = block.Split('\n')[0];
        if (ReFence.IsMatch(first) || ReHr.IsMatch(first) || ReHead.IsMatch(first)
            || ReQuote.IsMatch(first) || IsList(first) || ReInd.IsMatch(first)) return false;
        string[] ls = block.Split('\n');
        if (ls.Length > 1 && ReSetx.IsMatch(ls[ls.Length - 1])) return false;
        if (IsTable(ls, 0)) return false;
        return true;
    }

    // ----------------------------------------------------------------- inline

    static bool Flank(string s, int i) { return i < s.Length && !char.IsWhiteSpace(s[i]); }

    static int FindClose(string s, int from, string delim)
    {
        int i = from;
        while (i <= s.Length - delim.Length)
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (s[i] == '`')
            {
                int j = s.IndexOf('`', i + 1);
                if (j < 0) return -1;
                i = j + 1;
                continue;
            }
            if (string.CompareOrdinal(s, i, delim, 0, delim.Length) == 0
                && !char.IsWhiteSpace(s[i - 1])) return i;
            i++;
        }
        return -1;
    }

    static void Inline(Ctx c, string src, Style st)
    {
        string s = src;
        int i = 0, run = 0;
        while (i < s.Length)
        {
            char ch = s[i];

            if (ch == '\\' && i + 1 < s.Length && "\\`*_{}[]()#+-.!>~|=".IndexOf(s[i + 1]) >= 0)
            {
                Flush(c, s, run, i, st);
                Emit(c, s[i + 1].ToString(), st);
                i += 2; run = i; continue;
            }
            if (ch == '`')
            {
                int ticks = 0;
                while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                string fence = new string('`', ticks);
                int close = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Flush(c, s, run, i, st);
                    string code = s.Substring(i + ticks, close - i - ticks);
                    if (code.Length > 1 && code[0] == ' ' && code[code.Length - 1] == ' ')
                        code = code.Substring(1, code.Length - 2);
                    Style cs = st;
                    cs.Mono = true;
                    cs.Back = CCodeBg;
                    Emit(c, code, cs);
                    i = close + ticks; run = i; continue;
                }
            }
            if (ch == '!' && i + 1 < s.Length && s[i + 1] == '[')
            {
                Match im = Regex.Match(s.Substring(i), @"^!\[([^\]]*)\]\([ \t]*([^\s)]*)(?:[ \t]+""[^""]*"")?[ \t]*\)");
                if (im.Success)
                {
                    Flush(c, s, run, i, st);
                    Style ims = st;
                    ims.Italic = true;
                    ims.Color = CMuted;
                    string alt = im.Groups[1].Value;
                    Emit(c, "[image" + (alt.Length > 0 ? ": " + alt : "") + "]", ims);
                    i += im.Length; run = i; continue;
                }
            }
            if (ch == '[')
            {
                Match lm = Regex.Match(s.Substring(i), @"^\[([^\]]*)\]\([ \t]*([^\s)]*)(?:[ \t]+""[^""]*"")?[ \t]*\)");
                if (lm.Success)
                {
                    Flush(c, s, run, i, st);
                    Style ls = st;
                    ls.Color = CAccent;
                    ls.Under = true;
                    ls.Link = lm.Groups[2].Value;
                    int startChar = c.PlainLen;
                    Inline(c, lm.Groups[1].Value, ls);
                    AddLink(c, startChar, lm.Groups[2].Value);
                    i += lm.Length; run = i; continue;
                }
            }
            if (ch == '<')
            {
                Match am = Regex.Match(s.Substring(i), @"^<((?:https?|mailto):[^\s>]+)>");
                if (am.Success)
                {
                    Flush(c, s, run, i, st);
                    Style ls = st;
                    ls.Color = CAccent;
                    ls.Under = true;
                    int startChar = c.PlainLen;
                    Emit(c, am.Groups[1].Value, ls);
                    AddLink(c, startChar, am.Groups[1].Value);
                    i += am.Length; run = i; continue;
                }
            }
            if ((ch == 'h' || ch == 'H') && (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] == '('))
            {
                Match bm = Regex.Match(s.Substring(i), @"^https?://[^\s<>()""]+[^\s<>()"".,;:!?]");
                if (bm.Success)
                {
                    Flush(c, s, run, i, st);
                    Style ls = st;
                    ls.Color = CAccent;
                    ls.Under = true;
                    int startChar = c.PlainLen;
                    Emit(c, bm.Value, ls);
                    AddLink(c, startChar, bm.Value);
                    i += bm.Length; run = i; continue;
                }
            }
            if (ch == '*' || ch == '_')
            {
                string three = ch == '*' ? "***" : "___";
                string two = ch == '*' ? "**" : "__";
                string one = ch.ToString();
                if (Starts(s, i, three) && Flank(s, i + 3))
                {
                    int close = FindClose(s, i + 3, three);
                    if (close > 0)
                    {
                        Flush(c, s, run, i, st);
                        Style ns = st; ns.Bold = true; ns.Italic = true;
                        Inline(c, s.Substring(i + 3, close - i - 3), ns);
                        i = close + 3; run = i; continue;
                    }
                }
                if (Starts(s, i, two) && Flank(s, i + 2))
                {
                    int close = FindClose(s, i + 2, two);
                    if (close > 0)
                    {
                        Flush(c, s, run, i, st);
                        Style ns = st; ns.Bold = true;
                        Inline(c, s.Substring(i + 2, close - i - 2), ns);
                        i = close + 2; run = i; continue;
                    }
                }
                bool wordInner = ch == '_' && i > 0 && (char.IsLetterOrDigit(s[i - 1]));
                if (!wordInner && Flank(s, i + 1))
                {
                    int close = FindClose(s, i + 1, one);
                    if (close > 0)
                    {
                        Flush(c, s, run, i, st);
                        Style ns = st; ns.Italic = true;
                        Inline(c, s.Substring(i + 1, close - i - 1), ns);
                        i = close + 1; run = i; continue;
                    }
                }
            }
            if (ch == '~' && Starts(s, i, "~~") && Flank(s, i + 2))
            {
                int close = FindClose(s, i + 2, "~~");
                if (close > 0)
                {
                    Flush(c, s, run, i, st);
                    Style ns = st; ns.Strike = true;
                    Inline(c, s.Substring(i + 2, close - i - 2), ns);
                    i = close + 2; run = i; continue;
                }
            }
            if (ch == '=' && Starts(s, i, "==") && Flank(s, i + 2))
            {
                int close = FindClose(s, i + 2, "==");
                if (close > 0)
                {
                    Flush(c, s, run, i, st);
                    Style ns = st; ns.Back = CMarkBg;
                    Inline(c, s.Substring(i + 2, close - i - 2), ns);
                    i = close + 2; run = i; continue;
                }
            }
            if (ch == '\n')
            {
                // two trailing spaces = hard break; otherwise a soft wrap
                Flush(c, s, run, TrimTrailingSpaces(s, i), st);
                Break(c);
                i++; run = i; continue;
            }
            i++;
        }
        Flush(c, s, run, s.Length, st);
    }

    static int TrimTrailingSpaces(string s, int end)
    {
        int e = end;
        while (e > 0 && s[e - 1] == ' ') e--;
        return e;
    }

    static bool Starts(string s, int i, string tok)
    {
        return i + tok.Length <= s.Length && string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0;
    }

    static void Flush(Ctx c, string s, int from, int to, Style st)
    {
        if (to > from) Emit(c, s.Substring(from, to - from), st);
    }

    static void AddLink(Ctx c, int startChar, string url)
    {
        LinkSpan ls = new LinkSpan();
        ls.Start = startChar;
        ls.Length = c.PlainLen - startChar;
        ls.Url = url;
        if (ls.Length > 0) c.Links.Add(ls);
    }

    // ------------------------------------------------------------------ emit

    static void Emit(Ctx c, string text, Style st)
    {
        if (text.Length == 0) return;
        StringBuilder sb = c.Rtf;
        sb.Append('{');
        bool ctl = false;
        if (st.Mono) { sb.Append(@"\f1\fs").Append(c.MonoHalf); ctl = true; }
        if (st.Bold) { sb.Append(@"\b"); ctl = true; }
        if (st.Italic) { sb.Append(@"\i"); ctl = true; }
        if (st.Strike) { sb.Append(@"\strike"); ctl = true; }
        if (st.Under) { sb.Append(@"\ul"); ctl = true; }
        if (st.Color > 0) { sb.Append(@"\cf").Append(st.Color); ctl = true; }
        if (st.Back > 0) { sb.Append(@"\highlight").Append(st.Back); ctl = true; }
        // the delimiter space only belongs there if a control word precedes it,
        // otherwise it lands in the document as literal text
        if (ctl) sb.Append(' ');
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\') sb.Append(@"\\");
            else if (ch == '{') sb.Append(@"\{");
            else if (ch == '}') sb.Append(@"\}");
            else if (ch == '\t') sb.Append(@"\tab ");
            else if (ch == '\n') sb.Append(@"\line ");
            else if (ch < 128) sb.Append(ch);
            else sb.Append(@"\u").Append((int)(short)ch).Append('?');
        }
        sb.Append('}');
        c.PlainLen += text.Length;
    }

    static void Break(Ctx c)
    {
        c.Rtf.Append(@"\line ");
        c.PlainLen += 1;
    }

    static void EndPara(Ctx c)
    {
        c.Rtf.Append(@"\par ");
        c.PlainLen += 1;
    }
}
