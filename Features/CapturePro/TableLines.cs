using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace LTools.Features.CapturePro;

/// <summary>检测到的一条表格线：横线（<c>At</c> = y）或竖线（<c>At</c> = x），带它覆盖的范围。</summary>
internal readonly record struct TableSegment(bool Horizontal, int At, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>一个合并单元格：锚点（左上角）+ 跨几行几列。</summary>
internal readonly record struct TableCellSpan(int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>
/// 由表格线还原出来的网格：行 / 列边界（像素）+ 合并单元格。
/// </summary>
/// <remarks>
/// <para>
/// 行列边界来自线本身，所以**窄列不会并错**、空单元格天然存在；单元格内部没有分隔线的
/// 就是合并格（<see cref="Merges"/>）。没有 2 行 × 2 列就当作不是表格。
/// </para>
/// <para>
/// 纯几何：只吃 <see cref="TableSegment"/> 列表，不碰 OpenCv，<c>--selftest</c> 直接断言；
/// 真正找线是 <see cref="TableLines.Detect"/>（唯一依赖 Bitmap / OpenCv 的地方）。
/// </para>
/// </remarks>
internal sealed class TableLinesResult
{
    private readonly int _rows;
    private readonly int _columns;

    private TableLinesResult(IReadOnlyList<int> rowBounds, IReadOnlyList<int> columnBounds,
        IReadOnlyList<TableCellSpan> merges)
    {
        RowBounds = rowBounds;
        ColumnBounds = columnBounds;
        Merges = merges;
        _rows = rowBounds.Count - 1;
        _columns = columnBounds.Count - 1;
        Bounds = rowBounds.Count > 1 && columnBounds.Count > 1
            ? Rectangle.FromLTRB(columnBounds[0], rowBounds[0],
                Math.Max(columnBounds[^1], columnBounds[0] + 1), Math.Max(rowBounds[^1], rowBounds[0] + 1))
            : Rectangle.Empty;
    }

    /// <summary>横向边界的 y（升序，n + 1 个 → n 行）。</summary>
    public IReadOnlyList<int> RowBounds { get; }

    /// <summary>竖向边界的 x（升序，m + 1 个 → m 列）。</summary>
    public IReadOnlyList<int> ColumnBounds { get; }

    /// <summary>合并单元格（只有跨行 / 跨列的才在这里）。</summary>
    public IReadOnlyList<TableCellSpan> Merges { get; }

    /// <summary>整张表格在图里的外接矩形。</summary>
    public Rectangle Bounds { get; }

    public int RowCount => _rows;

    public int ColumnCount => _columns;

    /// <summary>至少 2 行 2 列才算表格（单行 / 单列只是普通文字）。</summary>
    public bool HasTable => _rows >= 2 && _columns >= 2;

    /// <summary>某个格子的像素矩形（按边界围出来）。</summary>
    public Rectangle CellRect(int row, int column)
    {
        if (row < 0 || row >= _rows || column < 0 || column >= _columns) return Rectangle.Empty;
        return Rectangle.FromLTRB(
            ColumnBounds[column], RowBounds[row],
            ColumnBounds[column + 1], RowBounds[row + 1]);
    }

    /// <summary>这个格子是不是合并格的锚点（左上角）。</summary>
    public bool IsAnchor(int row, int column)
    {
        foreach (TableCellSpan span in Merges)
        {
            if (row == span.Row && column == span.Column) return true;
            if (row >= span.Row && row < span.Row + span.RowSpan &&
                column >= span.Column && column < span.Column + span.ColumnSpan)
                return false;
        }
        return true;
    }

    /// <summary>这个格子所在的合并格（没合并就是它自己）。</summary>
    public TableCellSpan SpanAt(int row, int column)
    {
        foreach (TableCellSpan span in Merges)
        {
            if (row >= span.Row && row < span.Row + span.RowSpan &&
                column >= span.Column && column < span.Column + span.ColumnSpan)
                return span;
        }
        return new TableCellSpan(row, column, 1, 1);
    }

    internal static TableLinesResult Create(IReadOnlyList<int> rowBounds, IReadOnlyList<int> columnBounds,
        IReadOnlyList<TableCellSpan> merges) => new(rowBounds, columnBounds, merges);
}

/// <summary>
/// 表格线检测：二值化 → 形态学抽横 / 竖线 → 由线还原网格。
/// </summary>
/// <remarks>
/// <para>
/// 只管「有框线的表格」；识别不出线时返回两边都空，调用方（<see cref="TableReader"/>）
/// 会按 <c>detect</c> 决定是退回词框聚类还是直接报「没找到表格」。
/// </para>
/// <para>
/// 线检测本身对参数敏感（DPI、抗锯齿、彩色底、圆角），所以「够不够长」的门槛按图宽 / 高
/// 的比例给（25%），不写死像素；线宽容差给 4 像素，足够吸收 1–3 像素的线和高 DPI 的抖动。
/// </para>
/// </remarks>
internal static class TableLines
{
    /// <summary>一条线至少占图宽 / 高的这么多，才认它是表格线（不是下划线 / 文字笔画）。</summary>
    internal const double MinLengthRatio = 0.25;

    /// <summary>同一条边界上多条线的合并容差（像素）。</summary>
    internal const int ClusterTolerance = 4;

    /// <summary>横向 / 竖向共用：找线。失败（没线）时两个列表都是空的。</summary>
    public static (List<TableSegment> Horizontal, List<TableSegment> Vertical) Detect(Bitmap bitmap)
    {
        var horizontal = new List<TableSegment>();
        var vertical = new List<TableSegment>();
        if (bitmap.Width < 8 || bitmap.Height < 8) return (horizontal, vertical);

        using Mat color = FromBitmap(bitmap);
        if (color.Empty()) return (horizontal, vertical);

        using var gray = new Mat();
        Cv2.CvtColor(color, gray,
            color.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.MeanC,
            ThresholdTypes.BinaryInv, 15, 10);

        int kernelWidth = Math.Max(8, gray.Cols / 30);
        int kernelHeight = Math.Max(8, gray.Rows / 30);

        using var hKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(kernelWidth, 1));
        using var vKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, kernelHeight));
        using var hMask = new Mat();
        using var vMask = new Mat();
        Cv2.MorphologyEx(binary, hMask, MorphTypes.Open, hKernel);
        Cv2.MorphologyEx(binary, vMask, MorphTypes.Open, vKernel);

        // 断续的线（虚线、被字压住的线）连起来再取外接矩形
        using var hJoin = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 1));
        using var vJoin = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, 9));
        Cv2.Dilate(hMask, hMask, hJoin);
        Cv2.Dilate(vMask, vMask, vJoin);

        Collect(hMask, horizontal: true, horizontal);
        Collect(vMask, horizontal: false, vertical);
        return (horizontal, vertical);
    }

    /// <summary>由线还原网格；没有 2 行 2 列时返回 null。</summary>
    public static TableLinesResult? Build(IReadOnlyList<TableSegment> horizontal,
        IReadOnlyList<TableSegment> vertical, System.Drawing.Size image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;

        double minHorizontal = image.Width * MinLengthRatio;
        double minVertical = image.Height * MinLengthRatio;

        List<int> rows = Cluster(horizontal
            .Where(s => s.Length >= minHorizontal)
            .Select(s => s.At));
        List<int> columns = Cluster(vertical
            .Where(s => s.Length >= minVertical)
            .Select(s => s.At));

        if (rows.Count < 3 || columns.Count < 3) return null;

        var bounds = Rectangle.FromLTRB(columns[0], rows[0], columns[^1], rows[^1]);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        if (bounds.Width < image.Width * MinLengthRatio || bounds.Height < image.Height * MinLengthRatio)
            return null;

        List<TableSegment> h = horizontal.Where(s => s.Length >= minHorizontal).ToList();
        List<TableSegment> v = vertical.Where(s => s.Length >= minVertical).ToList();

        int rowsCount = rows.Count - 1;
        int columnsCount = columns.Count - 1;
        var parent = Enumerable.Range(0, rowsCount * columnsCount).ToArray();

        int Index(int row, int column) => row * columnsCount + column;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        for (int row = 0; row < rowsCount; row++)
        {
            for (int column = 0; column < columnsCount; column++)
            {
                if (column + 1 < columnsCount &&
                    !HasSeparator(v, wantVertical: true, columns[column + 1], rows[row], rows[row + 1]))
                    Union(Index(row, column), Index(row, column + 1));

                if (row + 1 < rowsCount &&
                    !HasSeparator(h, wantVertical: false, rows[row + 1], columns[column], columns[column + 1]))
                    Union(Index(row, column), Index(row + 1, column));
            }
        }

        var groups = new Dictionary<int, List<(int Row, int Column)>>();
        for (int row = 0; row < rowsCount; row++)
        {
            for (int column = 0; column < columnsCount; column++)
            {
                int root = Find(Index(row, column));
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<(int, int)>();
                list.Add((row, column));
            }
        }

        var merges = new List<TableCellSpan>();
        foreach (var cells in groups.Values)
        {
            if (cells.Count <= 1) continue;

            int minRow = cells.Min(c => c.Row), maxRow = cells.Max(c => c.Row);
            int minColumn = cells.Min(c => c.Column), maxColumn = cells.Max(c => c.Column);
            int area = (maxRow - minRow + 1) * (maxColumn - minColumn + 1);

            // 只认「填满一个矩形」的组：L 形的线（多半是噪点 / 半条线）当作没合并，别把表拆歪
            if (area != cells.Count) continue;
            merges.Add(new TableCellSpan(minRow, minColumn, maxRow - minRow + 1, maxColumn - minColumn + 1));
        }

        return TableLinesResult.Create(rows, columns, merges);
    }

    // ============ 内部 ============

    private static void Collect(Mat mask, bool horizontal, List<TableSegment> into)
    {
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (OpenCvSharp.Point[] contour in contours)
        {
            OpenCvSharp.Rect rect = Cv2.BoundingRect(contour);
            if (horizontal)
            {
                if (rect.Width < 8 || rect.Height > rect.Width) continue;
                into.Add(new TableSegment(true, rect.Y + rect.Height / 2, rect.X, rect.X + rect.Width));
            }
            else
            {
                if (rect.Height < 8 || rect.Width > rect.Height) continue;
                into.Add(new TableSegment(false, rect.X + rect.Width / 2, rect.Y, rect.Y + rect.Height));
            }
        }
    }

    /// <summary>把「在同一条边界上」的坐标聚起来（相差 ≤ <see cref="ClusterTolerance"/> 像素）。</summary>
    internal static List<int> Cluster(IEnumerable<int> values)
    {
        var result = new List<int>();
        int sum = 0, count = 0;

        foreach (int value in values.OrderBy(v => v))
        {
            if (count > 0 && value - sum / count > ClusterTolerance)
            {
                result.Add(sum / count);
                sum = 0;
                count = 0;
            }

            sum += value;
            count++;
        }

        if (count > 0) result.Add(sum / count);
        return result;
    }

    /// <summary>
    /// 这条边界上的线是否把两侧的格子隔开：覆盖该段 ≥ 一半才算（虚线 / 只画了一半不算分开）。
    /// </summary>
    private static bool HasSeparator(IReadOnlyList<TableSegment> segments, bool wantVertical, int at,
        int start, int end)
    {
        int length = Math.Max(1, end - start);
        foreach (TableSegment segment in segments)
        {
            if (segment.Horizontal == wantVertical) continue;
            if (Math.Abs(segment.At - at) > ClusterTolerance) continue;

            int overlap = Math.Min(segment.End, end) - Math.Max(segment.Start, start);
            if (overlap >= length / 2) return true;
        }

        return false;
    }

    /// <summary>Bitmap → Mat：走一次 PNG 编码，不引 OpenCvSharp4.Extensions（多一个包只为这一个转换）。</summary>
    private static Mat FromBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
    }
}
