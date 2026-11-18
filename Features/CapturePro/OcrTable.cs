using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>
/// 近似表格：把 OCR 的词框按「同一行 / 同一列」聚起来，得到一张可复制的网格。
/// </summary>
/// <remarks>
/// <para>
/// 刻意做成**纯数据 + 纯函数**（<c>--selftest</c> 直接断言，不用真跑 OCR）：
/// 行按纵向重叠、列按横向间隙聚类，没有「表格线检测」那种重活——
/// 截图里的表格常常没有可见边框，靠词框位置反而更稳。
/// </para>
/// <para>
/// 结论只当「参考」：<see cref="HasTable"/> 判不到 2 行 × 2 列就说明这大概不是表格，
/// 界面照实说「没识别出表格」，别把一段普通文字硬排成网格。
/// </para>
/// </remarks>
internal sealed class TableGrid
{
    public static readonly TableGrid Empty = new(Array.Empty<string[]>(), 0);

    public TableGrid(IReadOnlyList<string[]> rows, int columns)
    {
        Rows = rows;
        Columns = columns;
    }

    /// <summary>每行按列对齐的单元格文本（空串 = 这一格没有字）。</summary>
    public IReadOnlyList<string[]> Rows { get; }

    public int Columns { get; }

    public int RowCount => Rows.Count;

    /// <summary>至少 2 行 2 列才算表格（单行 / 单列只是普通文字）。</summary>
    public bool HasTable => RowCount >= 2 && Columns >= 2;

    /// <summary>TSV：单元格用制表符，行用换行——贴进 Excel / 表格工具就能分列。</summary>
    public string ToTsv()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Rows.Count; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append(string.Join('\t', Rows[i]));
        }
        return sb.ToString();
    }

    /// <summary>HTML 表格（贴进网页 / 富文本编辑器）。</summary>
    public string ToHtml()
    {
        var sb = new StringBuilder("<table>\r\n");
        foreach (string[] row in Rows)
        {
            sb.Append("  <tr>");
            foreach (string cell in row)
                sb.Append("<td>").Append(Escape(cell)).Append("</td>");
            sb.Append("</tr>\r\n");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}

/// <summary>
/// 把带坐标的 OCR 行 / 词框聚成 <see cref="TableGrid"/>。
/// </summary>
internal static class OcrTable
{
    /// <summary>行聚类：词框中心落在当前行带内就算同一行。</summary>
    public static TableGrid Build(IReadOnlyList<OcrLineBox> lines) => BuildCore(lines, out _);

    /// <summary>
    /// 表格在图像里占的矩形（没有 2 行 × 2 列的表格时返回 null）。
    /// 「图片转 Markdown / HTML」用它把这些行从段落里剔掉——同一批字已经被表格表达了。
    /// </summary>
    public static System.Drawing.Rectangle? TableArea(IReadOnlyList<OcrLineBox> lines)
    {
        TableGrid grid = BuildCore(lines, out List<OcrWordBox> used);
        if (!grid.HasTable || used.Count == 0) return null;

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (OcrWordBox cell in used)
        {
            left = Math.Min(left, cell.X);
            top = Math.Min(top, cell.Y);
            right = Math.Max(right, cell.X + cell.W);
            bottom = Math.Max(bottom, cell.Y + cell.H);
        }

        return right > left && bottom > top
            ? System.Drawing.Rectangle.FromLTRB(left, top, right, bottom)
            : null;
    }

    private static TableGrid BuildCore(IReadOnlyList<OcrLineBox> lines, out List<OcrWordBox> used)
    {
        used = new List<OcrWordBox>();
        if (lines == null || lines.Count == 0) return TableGrid.Empty;

        var cells = new List<OcrWordBox>();
        foreach (OcrLineBox line in lines)
        {
            foreach (OcrWordBox word in line.Words)
                if (word.W > 0 && word.H > 0 && word.Text.Length > 0) cells.Add(word);

            // 引擎只给整行、没有词框：那一行本身当一个格子
            if (line.Words.Count == 0 && line.W > 0 && line.H > 0 && line.Text.Length > 0)
                cells.Add(new OcrWordBox { Text = line.Text, X = line.X, Y = line.Y, W = line.W, H = line.H });
        }
        if (cells.Count == 0) return TableGrid.Empty;

        int height = MedianHeight(cells);
        int rowTolerance = Math.Max(2, height / 2);
        int columnTolerance = Math.Max(3, height / 3);

        List<List<OcrWordBox>> rows = BuildRows(cells, rowTolerance);
        List<(int Left, int Right)> columns = BuildColumns(cells, columnTolerance);
        if (columns.Count == 0) return TableGrid.Empty;

        var grid = new List<string[]>(rows.Count);
        foreach (List<OcrWordBox> row in rows)
        {
            var values = new string[columns.Count];
            Array.Fill(values, "");

            // 同一格里可能落着好几个词：按间距决定拼不拼空格（中文逐字框要贴在一起）
            var lastRight = new int[columns.Count];
            Array.Fill(lastRight, int.MinValue);

            foreach (OcrWordBox word in row.OrderBy(w => w.X))
            {
                int index = ColumnOf(word, columns);
                if (index < 0) continue;

                string text = word.Text;
                if (values[index].Length > 0)
                {
                    int gap = word.X - lastRight[index];
                    bool space = gap > height * 0.7;
                    if (space && !EndsWithSpace(values[index])) values[index] += " ";
                }

                values[index] += text;
                lastRight[index] = word.X + word.W;
            }

            // 整行都是空格的没意义（多半是噪点）
            if (values.Any(v => v.Trim().Length > 0))
            {
                grid.Add(values);

                // 表格区域只认「有两格以上有字」的行：单格的行多半是被误吸收进来的普通段落
                if (values.Count(v => v.Trim().Length > 0) >= 2) used.AddRange(row);
            }
        }

        return grid.Count == 0 ? TableGrid.Empty : new TableGrid(grid, columns.Count);
    }

    // ============ 聚类 ============

    private static List<List<OcrWordBox>> BuildRows(List<OcrWordBox> cells, int tolerance)
    {
        var rows = new List<List<OcrWordBox>>();
        List<OcrWordBox>? current = null;
        int top = 0, bottom = 0;

        foreach (OcrWordBox cell in cells.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            int center = cell.Y + cell.H / 2;
            bool same = current != null && center >= top - tolerance && center <= bottom + tolerance;

            if (!same)
            {
                current = new List<OcrWordBox>();
                rows.Add(current);
                top = cell.Y;
                bottom = cell.Y + cell.H;
            }
            else
            {
                top = Math.Min(top, cell.Y);
                bottom = Math.Max(bottom, cell.Y + cell.H);
            }

            current!.Add(cell);
        }

        return rows;
    }

    private static List<(int Left, int Right)> BuildColumns(List<OcrWordBox> cells, int tolerance)
    {
        var columns = new List<(int Left, int Right)>();

        foreach (OcrWordBox cell in cells.OrderBy(c => c.X))
        {
            if (columns.Count > 0)
            {
                (int left, int right) = columns[^1];
                if (cell.X <= right + tolerance)
                {
                    columns[^1] = (left, Math.Max(right, cell.X + cell.W));
                    continue;
                }
            }
            columns.Add((cell.X, cell.X + cell.W));
        }

        return columns;
    }

    /// <summary>这个格子落在哪一列：按横向重叠最多的那一段算，落在缝里退回最近的。</summary>
    private static int ColumnOf(OcrWordBox cell, List<(int Left, int Right)> columns)
    {
        int best = -1, bestOverlap = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            int overlap = Math.Min(cell.X + cell.W, columns[i].Right) - Math.Max(cell.X, columns[i].Left);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = i;
            }
        }
        if (best >= 0) return best;

        int center = cell.X + cell.W / 2;
        for (int i = 0; i < columns.Count; i++)
            if (center >= columns[i].Left && center <= columns[i].Right) return i;
        return columns.Count > 0 ? (center < columns[0].Left ? 0 : columns.Count - 1) : -1;
    }

    private static int MedianHeight(List<OcrWordBox> cells)
    {
        var heights = cells.Select(c => c.H).OrderBy(h => h).ToList();
        int median = heights[heights.Count / 2];
        return Math.Max(1, median);
    }

    private static bool EndsWithSpace(string text) =>
        text.Length == 0 || char.IsWhiteSpace(text[^1]);

    /// <summary>给冒烟 / 自检用的稳定格式（区域 + 文本），方便人眼对照。</summary>
    public static string Describe(TableGrid grid)
    {
        if (!grid.HasTable) return "没有表格";
        return string.Format(CultureInfo.InvariantCulture, "{0} 行 × {1} 列", grid.RowCount, grid.Columns);
    }
}
