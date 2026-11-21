using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>表格的还原方式（动作单元参数 <c>detect</c>）。</summary>
internal enum TableDetectMode
{
    /// <summary>先找表格线，找不到（或线里没有字）再退回词框聚类。</summary>
    auto,

    /// <summary>只认有框线的表格；找不到就是没找到（结果不会被聚类带偏）。</summary>
    lines,

    /// <summary>只按文字位置聚类（等同截图流水线里的「近似表格」）。</summary>
    cluster,
}

/// <summary>一次表格识别的结果。</summary>
internal sealed class TableReadResult
{
    public TableDocument Table { get; init; } = TableDocument.Empty;

    /// <summary>这张表是按表格线还原的（false = 按词框聚类的近似表格）。</summary>
    public bool FromLines { get; init; }

    /// <summary>≥2 行 × ≥2 列、且至少有一个格子里有字。</summary>
    public bool Found
    {
        get
        {
            TableDocument table = Table;
            if (table.IsEmpty || table.RowCount < 2 || table.ColumnCount < 2) return false;

            for (int row = 0; row < table.RowCount; row++)
            {
                for (int column = 0; column < table.ColumnCount; column++)
                    if (table.IsAnchor(row, column) && table.CellText(row, column).Trim().Length > 0) return true;
            }

            return false;
        }
    }
}

/// <summary>
/// 表格识别的唯一入口：取一张位图 → （表格线 → 单元格；没线时退回词框聚类）→ <see cref="TableDocument"/>。
/// </summary>
/// <remarks>
/// <para>
/// 两条路的分工：<see cref="TableLines"/> 负责「行列边界从哪来」，
/// <see cref="OcrTable"/> 负责「没线时按文字位置聚」，本类只做编排与「词框塞进哪个格子」。
/// 动作单元（<c>screen.table</c>）只调 <see cref="Read"/>，别再自己拼这套。
/// </para>
/// <para>
/// 有线时**只 OCR 表格那一块**（外扩 4 像素防切边）：整屏识别时周围的正文既慢又会把列带歪。
/// 没有线时沿用截图流水线的做法——整图 OCR 再聚类。
/// </para>
/// </remarks>
internal static class TableReader
{
    /// <summary>OCR 表格区域时四周外扩的像素（别把字的边切掉）。</summary>
    private const int CropPadding = 4;

    /// <summary>读一张图里的表格；<paramref name="detect"/> 为空按 <see cref="TableDetectMode.auto"/>。</summary>
    public static TableReadResult Read(Bitmap bitmap, string? engine, string? detect)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        TableDetectMode mode = ParseMode(detect);
        if (mode != TableDetectMode.cluster)
        {
            var (horizontal, vertical) = TableLines.Detect(bitmap);
            TableLinesResult? grid = TableLines.Build(horizontal, vertical, bitmap.Size);

            if (grid is { HasTable: true })
            {
                TableDocument fromLines = ReadCells(bitmap, engine, grid);
                // auto：线网格里一个字都没认到（空白框 / 线认错了），退回聚类再试
                if (mode == TableDetectMode.lines || HasContent(fromLines))
                    return new TableReadResult { Table = fromLines, FromLines = true };
            }
            else if (mode == TableDetectMode.lines)
            {
                return new TableReadResult();
            }
        }

        var lines = OcrLayout.Read(bitmap, engine);
        return new TableReadResult { Table = TableDocument.FromGrid(OcrTable.Build(lines)) };
    }

    /// <summary>解析 <c>detect</c>：认不出的值按 auto（同「不可见参数不报错」的宽进口径）。</summary>
    public static TableDetectMode ParseMode(string? detect) => (detect ?? "").Trim().ToLowerInvariant() switch
    {
        "lines" => TableDetectMode.lines,
        "cluster" => TableDetectMode.cluster,
        _ => TableDetectMode.auto,
    };

    // ============ 有线：按单元格归属词框 ============

    private static TableDocument ReadCells(Bitmap bitmap, string? engine, TableLinesResult grid)
    {
        Rectangle crop = Inflate(grid.Bounds, CropPadding, bitmap.Size);
        if (crop.Width <= 0 || crop.Height <= 0) crop = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

        var document = new TableDocument(grid.RowCount, grid.ColumnCount);

        // 先按合并信息收拾格子，再往里写文字（写的时候只认锚点）
        foreach (TableCellSpan span in grid.Merges)
        {
            document.Merge(new TableRange(span.Row, span.Column,
                span.Row + span.RowSpan - 1, span.Column + span.ColumnSpan - 1));
        }

        using Bitmap part = crop == new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            ? (Bitmap)bitmap.Clone()
            : bitmap.Clone(crop, bitmap.PixelFormat);

        IReadOnlyList<OcrLineBox> ocr = OcrLayout.Read(part, engine);
        var cells = new List<(string Text, int X, int Y, int W, int H)>(Flatten(ocr).Select(word =>
            (word.Text, word.X + crop.X, word.Y + crop.Y, word.W, word.H)));

        if (cells.Count == 0) return document;

        int height = Math.Max(1, MedianHeight(cells));
        var builders = new Dictionary<(int Row, int Column), StringBuilder>();
        var rights = new Dictionary<(int Row, int Column), int>();

        foreach (var word in cells.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            int centerX = word.X + word.W / 2;
            int centerY = word.Y + word.H / 2;

            (int Row, int Column) hit = (-1, -1);
            for (int row = 0; row < grid.RowCount && hit.Row < 0; row++)
            {
                for (int column = 0; column < grid.ColumnCount; column++)
                {
                    if (!grid.CellRect(row, column).Contains(centerX, centerY)) continue;
                    TableCellSpan span = grid.SpanAt(row, column);
                    hit = (span.Row, span.Column);
                    break;
                }
            }

            if (hit.Row < 0) continue;   // 落在表格外（外扩的边距里）

            if (!builders.TryGetValue(hit, out StringBuilder? builder))
                builders[hit] = builder = new StringBuilder();

            if (builder.Length > 0 && rights.TryGetValue(hit, out int right))
            {
                // 同一格里多个词：间距大才补空格（中文逐字框要贴在一起）
                bool space = word.X - right > height * 0.7;
                if (space && !char.IsWhiteSpace(builder[^1])) builder.Append(' ');
            }

            builder.Append(word.Text);
            rights[hit] = word.X + word.W;
        }

        foreach (var (key, builder) in builders)
        {
            string text = builder.ToString().Trim();
            if (text.Length > 0) document.SetCellText(key.Row, key.Column, text);
        }

        return document;
    }

    // ============ 小工具 ============

    private static bool HasContent(TableDocument table)
    {
        for (int row = 0; row < table.RowCount; row++)
        {
            for (int column = 0; column < table.ColumnCount; column++)
                if (table.IsAnchor(row, column) && table.CellText(row, column).Trim().Length > 0) return true;
        }
        return false;
    }

    /// <summary>词框（或只有整行时的那一行）摊平；坐标是所在位图的像素。</summary>
    private static IEnumerable<(string Text, int X, int Y, int W, int H)> Flatten(IReadOnlyList<OcrLineBox> lines)
    {
        foreach (OcrLineBox line in lines)
        {
            if (line.Words.Count == 0)
            {
                if (line.Text.Length > 0 && line.W > 0 && line.H > 0)
                    yield return (line.Text, line.X, line.Y, line.W, line.H);
                continue;
            }

            foreach (OcrWordBox word in line.Words)
            {
                if (word.Text.Length > 0 && word.W > 0 && word.H > 0)
                    yield return (word.Text, word.X, word.Y, word.W, word.H);
            }
        }
    }

    private static int MedianHeight(List<(string Text, int X, int Y, int W, int H)> words)
    {
        if (words.Count == 0) return 1;
        var heights = words.Select(w => w.H).OrderBy(h => h).ToList();
        return heights[heights.Count / 2];
    }

    private static Rectangle Inflate(Rectangle rect, int padding, Size limit) =>
        Rectangle.Intersect(
            Rectangle.FromLTRB(rect.Left - padding, rect.Top - padding, rect.Right + padding, rect.Bottom + padding),
            new Rectangle(0, 0, limit.Width, limit.Height));
}
