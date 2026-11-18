using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>转换出来的一块内容（标题 / 段落 / 列表项 / 表格）。</summary>
internal enum ConversionKind
{
    /// <summary>标题（<see cref="ConversionBlock.Level"/> = 1..3）。</summary>
    Heading,

    /// <summary>普通段落。</summary>
    Paragraph,

    /// <summary>无序列表项。</summary>
    Bullet,

    /// <summary>有序列表项（编号按出现顺序重排）。</summary>
    Ordered,

    /// <summary>表格（<see cref="ConversionBlock.Rows"/>）。</summary>
    Table,
}

/// <summary>一块转换内容：预览与 Markdown / HTML 两份源码都由它派生。</summary>
internal sealed class ConversionBlock
{
    public ConversionKind Kind { get; init; }
    public int Level { get; init; } = 1;
    public string Text { get; init; } = "";
    public IReadOnlyList<string[]> Rows { get; init; } = Array.Empty<string[]>();
}

/// <summary>
/// 图片转 Markdown / HTML 的**本地派生**：把 OCR 行按版面聚成标题 / 段落 / 列表 / 表格，
/// 再生成两份源码（预览由同一份块列表渲染，见 <c>CaptureConversionView</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游这两页走 Snow Shot 私有云的视觉模型（`markdownRequested` / `htmlRequested`），
/// 云端模型不可复刻（见 <c>docs/CAPTURE_PARITY.md</c> §5）。这里用「本地派生」替代：
/// 行高相对中位数判标题层级、纵向间距判段落、行首记号判列表、<see cref="OcrTable"/> 的聚类结果当表格——
/// 形状与上游一致（标题 / 段落 / 列表 / 表格、可复制源码），但**没有语义理解**（不会替你总结 / 补全）。
/// </para>
/// <para>
/// 纯函数、不碰窗口、不读文件：<c>--selftest</c> 直接拿行框断言。
/// </para>
/// </remarks>
internal sealed class ConversionDocument
{
    public static readonly ConversionDocument Empty = new(Array.Empty<ConversionBlock>());

    private ConversionDocument(IReadOnlyList<ConversionBlock> blocks)
    {
        Blocks = blocks;
        Markdown = ToMarkdown(blocks);
        Html = ToHtml(blocks);
    }

    public IReadOnlyList<ConversionBlock> Blocks { get; }

    /// <summary>Markdown 源码（复制走它）。</summary>
    public string Markdown { get; }

    /// <summary>HTML 源码（复制走它）。</summary>
    public string Html { get; }

    public bool IsEmpty => Blocks.Count == 0;

    /// <summary>纯文本形式（复制 / 导出兜底：段落之间空一行，表格按制表符分行）。</summary>
    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (ConversionBlock block in Blocks)
            {
                if (sb.Length > 0) sb.Append("\r\n");

                if (block.Kind == ConversionKind.Table)
                {
                    foreach (string[] row in block.Rows)
                        sb.Append(string.Join("\t", row)).Append("\r\n");
                    continue;
                }

                if (block.Kind is ConversionKind.Bullet or ConversionKind.Ordered)
                    sb.Append("• ").Append(block.Text).Append("\r\n");
                else sb.Append(block.Text).Append("\r\n");
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }
    }

    /// <summary>正文字符数（不含 Markdown / HTML 语法；状态行报它）。</summary>
    public int Length
    {
        get
        {
            int total = 0;
            foreach (ConversionBlock block in Blocks)
            {
                if (block.Kind == ConversionKind.Table)
                {
                    foreach (string[] row in block.Rows)
                        foreach (string cell in row)
                            total += cell.Length;
                }
                else
                {
                    total += block.Text.Length;
                }
            }

            return total;
        }
    }

    /// <summary>标题层级上限（上游视觉模型也只给三级）。</summary>
    private const int MaxLevel = 3;

    // 行首记号：`•` / `·` / `-` / `*` / `–`（后面要跟空白，别把「-5 度」当列表）
    private static readonly Regex BulletMark =
        new(@"^[•·\-*–—]\s+", RegexOptions.CultureInvariant);

    // 有序记号：`1.` / `1、` / `1)` / `(1)` / `（1）`
    private static readonly Regex OrderedMark =
        new(@"^(?:[（(]?\d{1,3}[)）.、．]\s*|[（(]\d{1,3}[)）]\s*)",
            RegexOptions.CultureInvariant);

    // ============ 版面 → 块 ============

    /// <summary>把 OCR 行与近似表格聚成块列表。</summary>
    public static ConversionDocument Build(IReadOnlyList<OcrLineBox> lines, TableGrid table)
    {
        var body = new List<OcrLineBox>();
        if (lines != null)
        {
            foreach (OcrLineBox line in lines)
            {
                if (line == null || string.IsNullOrWhiteSpace(line.Text)) continue;
                if (line.W <= 0 || line.H <= 0) continue;
                body.Add(line);
            }
        }

        body.Sort(CompareReadingOrder);

        // 表格区域里的行已经由表格表达；再当段落输出会重复一遍
        Rectangle? tableArea = table is { HasTable: true } && lines != null ? OcrTable.TableArea(lines) : null;
        if (tableArea != null)
        {
            var outside = new List<OcrLineBox>(body.Count);
            foreach (OcrLineBox line in body)
                if (!InTableArea(line, tableArea.Value)) outside.Add(line);
            body = outside;
        }

        int median = MedianHeight(body);
        var blocks = new List<ConversionBlock>();

        // 表格放最后（上游视觉模型通常把表放在它出现的位置；OCR 只有行框，
        // 判断不了它夹在哪两段之间，放在末尾最不容易误导）
        int index = 0;
        while (index < body.Count)
        {
            OcrLineBox line = body[index];
            string text = line.Text.Trim();
            if (text.Length == 0)
            {
                index++;
                continue;
            }

            int level = HeadingLevel(line, median);
            if (level > 0)
            {
                blocks.Add(new ConversionBlock
                {
                    Kind = ConversionKind.Heading,
                    Level = level,
                    Text = StripListMark(text, out _),
                });
                index++;
                continue;
            }

            string item = StripListMark(text, out ConversionKind listKind);
            if (listKind != ConversionKind.Paragraph)
            {
                blocks.Add(new ConversionBlock { Kind = listKind, Text = item });
                index++;
                continue;
            }

            // 段落：把纵向贴着的普通行接起来（行高相近才算同一段）
            var paragraph = new StringBuilder(text);
            int height = line.H;
            int bottom = line.Y + line.H;
            index++;

            while (index < body.Count)
            {
                OcrLineBox next = body[index];
                int nextLevel = HeadingLevel(next, median);
                string nextItem = StripListMark(next.Text.Trim(), out ConversionKind nextKind);
                if (nextLevel > 0 || nextKind != ConversionKind.Paragraph) break;

                int gap = next.Y - bottom;
                if (gap > Math.Max(2, (int)Math.Round(median * 0.75))) break;
                if (next.H > height * 1.6 || next.H * 1.6 < height) break;

                Join(paragraph, next.Text.Trim());
                height = next.H;
                bottom = next.Y + next.H;
                index++;
            }

            blocks.Add(new ConversionBlock { Kind = ConversionKind.Paragraph, Text = paragraph.ToString() });
        }

        if (table is { HasTable: true })
        {
            var rows = TrimTableRows(table.Rows);
            if (rows.Count > 0) blocks.Add(new ConversionBlock { Kind = ConversionKind.Table, Rows = rows });
        }

        return blocks.Count == 0 ? Empty : new ConversionDocument(blocks);
    }

    /// <summary>
    /// 去掉表格首尾那些「只有一格有字」的行：OCR 聚类会把紧贴表格的普通段落也吸成一行
    /// （<see cref="OcrTable"/> 的既有行为），这里按「表格的行至少两格有字」把两头切掉；
    /// 中间的单格行保留（真实表格常有只填一格的空行）。
    /// </summary>
    private static List<string[]> TrimTableRows(IReadOnlyList<string[]> rows)
    {
        static int Filled(string[] row)
        {
            int count = 0;
            foreach (string cell in row)
                if (!string.IsNullOrWhiteSpace(cell)) count++;
            return count;
        }

        int start = 0, end = rows.Count - 1;
        while (start <= end && Filled(rows[start]) < 2) start++;
        while (end >= start && Filled(rows[end]) < 2) end--;

        var trimmed = new List<string[]>(Math.Max(0, end - start + 1));
        for (int i = start; i <= end; i++) trimmed.Add(rows[i]);
        return trimmed;
    }

    /// <summary>阅读顺序：先上后下、同一行先左后右。</summary>
    private static int CompareReadingOrder(OcrLineBox a, OcrLineBox b)
    {
        int tolerance = Math.Max(2, Math.Min(a.H, b.H) / 2);
        if (Math.Abs(a.Y - b.Y) > tolerance) return a.Y.CompareTo(b.Y);
        return a.X.CompareTo(b.X);
    }

    private static bool InTableArea(OcrLineBox line, Rectangle area)
    {
        int centerX = line.X + line.W / 2;
        int centerY = line.Y + line.H / 2;
        return centerX >= area.Left && centerX <= area.Right &&
               centerY >= area.Top && centerY <= area.Bottom;
    }

    private static int MedianHeight(IReadOnlyList<OcrLineBox> lines)
    {
        if (lines.Count == 0) return 1;

        var heights = new List<int>(lines.Count);
        foreach (OcrLineBox line in lines) heights.Add(line.H);
        heights.Sort();
        return Math.Max(1, heights[heights.Count / 2]);
    }

    /// <summary>行高相对中位数判标题层级；普通正文返回 0。</summary>
    private static int HeadingLevel(OcrLineBox line, int median)
    {
        if (median <= 0) return 0;

        double ratio = (double)line.H / median;
        if (ratio >= 1.9) return 1;
        if (ratio >= 1.45) return 2;
        if (ratio >= 1.2) return 3;
        return 0;
    }

    /// <summary>去掉行首列表记号；返回去掉后的正文。</summary>
    private static string StripListMark(string text, out ConversionKind kind)
    {
        kind = ConversionKind.Paragraph;

        Match ordered = OrderedMark.Match(text);
        if (ordered.Success && ordered.Length < text.Length)
        {
            kind = ConversionKind.Ordered;
            return text[ordered.Length..].Trim();
        }

        Match bullet = BulletMark.Match(text);
        if (bullet.Success && bullet.Length < text.Length)
        {
            kind = ConversionKind.Bullet;
            return text[bullet.Length..].Trim();
        }

        return text;
    }

    /// <summary>接段落：两边都是中日韩字符就直接连（中文没有词间空格），否则补一个空格。</summary>
    private static void Join(StringBuilder builder, string next)
    {
        if (builder.Length == 0)
        {
            builder.Append(next);
            return;
        }

        char left = builder[^1];
        char right = next.Length > 0 ? next[0] : ' ';
        bool tight = IsWide(left) || IsWide(right) ||
                     left is '，' or '。' or '、' or '；' or '：' or '！' or '？' or '）' or '」' or '』' ||
                     right is '，' or '。' or '、' or '；' or '：' or '！' or '？' or '）' or '」' or '』';

        if (!tight && !char.IsWhiteSpace(left)) builder.Append(' ');
        builder.Append(next);
    }

    private static bool IsWide(char c) =>
        c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;

    // ============ 块 → Markdown ============

    private static string ToMarkdown(IReadOnlyList<ConversionBlock> blocks)
    {
        var sb = new StringBuilder();
        int orderedIndex = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            ConversionBlock block = blocks[i];
            if (block.Kind == ConversionKind.Ordered)
            {
                if (i == 0 || blocks[i - 1].Kind != ConversionKind.Ordered) orderedIndex = 0;
                orderedIndex++;
            }

            if (sb.Length > 0)
            {
                // 同一段列表项之间只换行（紧凑列表），其余块之间空一行
                bool tight = block.Kind is ConversionKind.Bullet or ConversionKind.Ordered &&
                             i > 0 && blocks[i - 1].Kind == block.Kind;
                sb.Append(tight ? "\r\n" : "\r\n\r\n");
            }

            switch (block.Kind)
            {
                case ConversionKind.Heading:
                    sb.Append('#', Math.Clamp(block.Level, 1, MaxLevel)).Append(' ').Append(block.Text);
                    break;
                case ConversionKind.Bullet:
                    sb.Append("- ").Append(block.Text);
                    break;
                case ConversionKind.Ordered:
                    sb.Append(orderedIndex.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(block.Text);
                    break;
                case ConversionKind.Table:
                    AppendMarkdownTable(sb, block.Rows);
                    break;
                default:
                    sb.Append(block.Text);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendMarkdownTable(StringBuilder sb, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return;

        int columns = 0;
        foreach (string[] row in rows) columns = Math.Max(columns, row.Length);
        if (columns == 0) return;

        AppendRow(rows[0]);
        sb.Append("\r\n|");
        for (int i = 0; i < columns; i++) sb.Append(" --- |");
        for (int i = 1; i < rows.Count; i++)
        {
            sb.Append("\r\n");
            AppendRow(rows[i]);
        }

        return;

        void AppendRow(string[] row)
        {
            sb.Append('|');
            for (int i = 0; i < columns; i++)
            {
                string cell = i < row.Length ? row[i] : "";
                sb.Append(' ').Append(MarkdownCell(cell)).Append(" |");
            }
        }
    }

    /// <summary>单元格里换行会打断表格，`|` 会多切一列——都换成安全写法。</summary>
    private static string MarkdownCell(string cell)
    {
        if (string.IsNullOrEmpty(cell)) return "";
        return cell.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
            .Replace("|", "\\|").Trim();
    }

    // ============ 块 → HTML ============

    private static string ToHtml(IReadOnlyList<ConversionBlock> blocks)
    {
        var sb = new StringBuilder();
        string? openList = null;
        string? openTable = null;
        bool headerDone = false;
        bool inHeader = false;

        for (int i = 0; i < blocks.Count; i++)
        {
            ConversionBlock block = blocks[i];
            string? wanted = block.Kind switch
            {
                ConversionKind.Bullet => "ul",
                ConversionKind.Ordered => "ol",
                ConversionKind.Table => "table",
                _ => null,
            };

            if (openList != null && openList != wanted) CloseList();
            if (openTable != null && wanted != "table") CloseTable();

            switch (block.Kind)
            {
                case ConversionKind.Heading:
                    int level = Math.Clamp(block.Level, 1, MaxLevel);
                    sb.Append("<h").Append(level).Append('>').Append(HtmlText(block.Text))
                        .Append("</h").Append(level).Append('>');
                    break;

                case ConversionKind.Paragraph:
                    sb.Append("<p>").Append(HtmlText(block.Text)).Append("</p>");
                    break;

                case ConversionKind.Bullet:
                case ConversionKind.Ordered:
                    if (openList == null)
                    {
                        openList = wanted;
                        sb.Append('<').Append(wanted).Append('>');
                    }

                    sb.Append("<li>").Append(HtmlText(block.Text)).Append("</li>");
                    break;

                case ConversionKind.Table:
                    if (openTable == null)
                    {
                        openTable = "table";
                        headerDone = false;
                        inHeader = false;
                        sb.Append("<table>");
                    }

                    if (!headerDone)
                    {
                        // 第一行当表头（与 TableDocument 的 th 口径一致）
                        string[] head = block.Rows.Count > 0 ? block.Rows[0] : Array.Empty<string>();
                        sb.Append("<thead><tr>");
                        foreach (string cell in head)
                            sb.Append("<th>").Append(HtmlText(cell)).Append("</th>");
                        sb.Append("</tr></thead><tbody>");
                        headerDone = true;
                        inHeader = true;

                        for (int r = 1; r < block.Rows.Count; r++)
                        {
                            sb.Append("<tr>");
                            foreach (string cell in block.Rows[r])
                                sb.Append("<td>").Append(HtmlText(cell)).Append("</td>");
                            sb.Append("</tr>");
                        }
                    }

                    break;
            }
        }

        if (openList != null || openTable != null)
        {
            if (inHeader) sb.Append("</tbody>");
            if (openTable != null) sb.Append("</table>");
            if (openList != null) sb.Append("</").Append(openList).Append('>');
        }

        return sb.ToString();

        void CloseList()
        {
            if (openList == null) return;
            sb.Append("</").Append(openList).Append('>');
            openList = null;
        }

        void CloseTable()
        {
            if (openTable == null) return;
            if (inHeader) sb.Append("</tbody>");
            sb.Append("</table>");
            openTable = null;
            headerDone = false;
            inHeader = false;
        }
    }

    private static string HtmlText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
