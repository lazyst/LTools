using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LTools.Features.CapturePro;

/// <summary>
/// 表格选区：左上 / 右下角，含两端。
/// </summary>
/// <remarks>
/// 刻意做成 <c>default</c> 即「无效」：上游 <c>ScreenshotTableRange</c> 的默认值是四个 -1，
/// 这里多带一个 <c>_valid</c> 标记，所以 <c>default(TableRange).IsValid</c> 是 false
/// （值类型默认全 0，光靠 Top/Bottom 比大小会得到一个合法的 1×1）。
/// </remarks>
internal readonly struct TableRange : IEquatable<TableRange>
{
    private readonly bool _valid;

    public TableRange(int top, int left, int bottom, int right)
    {
        Top = top;
        Left = left;
        Bottom = bottom;
        Right = right;
        _valid = top >= 0 && left >= 0 && bottom >= top && right >= left;
    }

    public int Top { get; }

    public int Left { get; }

    public int Bottom { get; }

    public int Right { get; }

    public bool IsValid => _valid;

    public int RowCount => _valid ? Bottom - Top + 1 : 0;

    public int ColumnCount => _valid ? Right - Left + 1 : 0;

    public static TableRange Empty => default;

    public static TableRange Single(int row, int column) => new(row, column, row, column);

    /// <summary>两个角（顺序任意）围出来的矩形。</summary>
    public static TableRange Between(int rowA, int columnA, int rowB, int columnB) =>
        new(Math.Min(rowA, rowB), Math.Min(columnA, columnB),
            Math.Max(rowA, rowB), Math.Max(columnA, columnB));

    public static TableRange Union(TableRange a, TableRange b)
    {
        if (!a.IsValid) return b;
        if (!b.IsValid) return a;
        return new TableRange(Math.Min(a.Top, b.Top), Math.Min(a.Left, b.Left),
            Math.Max(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right));
    }

    public bool Contains(int row, int column) =>
        _valid && row >= Top && row <= Bottom && column >= Left && column <= Right;

    public bool Equals(TableRange other) =>
        _valid == other._valid && (!_valid ||
            (Top == other.Top && Left == other.Left && Bottom == other.Bottom && Right == other.Right));

    public override bool Equals(object? obj) => obj is TableRange other && Equals(other);

    public override int GetHashCode() =>
        _valid ? HashCode.Combine(Top, Left, Bottom, Right) : 0;

    public static bool operator ==(TableRange a, TableRange b) => a.Equals(b);

    public static bool operator !=(TableRange a, TableRange b) => !a.Equals(b);

    public override string ToString() =>
        _valid ? string.Format(CultureInfo.InvariantCulture, "{0},{1}-{2},{3}", Top, Left, Bottom, Right)
               : "empty";
}

/// <summary>一个单元格：文本 + 跨行 / 跨列 + 是否表头（上游 <c>ScreenshotTableCell</c>）。</summary>
internal sealed class TableCell
{
    public string Text = "";

    public int RowSpan = 1;

    public int ColumnSpan = 1;

    public bool Header;

    public TableCell Clone() => new()
    {
        Text = Text,
        RowSpan = RowSpan,
        ColumnSpan = ColumnSpan,
        Header = Header,
    };
}

/// <summary>
/// 识别出来的表格文档：槽位表（每个格子都知道自己的锚点）+ 合并 / 拆分 / 清空 + HTML / TSV 出口。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshottabledocument.h/.cpp</c> 一比一移植：**槽位表 + 锚点**是全部语义的来源，
/// 合并 = 锚点记 <c>RowSpan/ColumnSpan</c> 并把盖住的槽位清成「指向锚点」；拆分 = 把整块槽位重建成独立格，
/// 文字留在左上角。界面只读 <see cref="SpanRangeAt"/> / <see cref="ExpandedRange"/> 决定画多大、
/// 选区要不要整块带上，别在 UI 里另算一套。
/// </para>
/// <para>
/// 纯数据 + 纯函数（<c>--selftest</c> 直接断言），上限与上游一致：单边 256、总格 16384。
/// </para>
/// </remarks>
internal sealed class TableDocument
{
    public const int MaximumDimension = 256;

    public const int MaximumCells = 16384;

    private static readonly Regex TagPattern = new(
        @"<!--[\s\S]*?-->|</?([A-Za-z][A-Za-z0-9]*)\b(?:[^<>""']|""[^""]*""|'[^']*')*/?>",
        RegexOptions.CultureInvariant);

    private static readonly Regex SpanPattern = new(
        @"\b(rowspan|colspan)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NumericText = new(
        @"^[+-]?[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant);

    private sealed class Slot
    {
        public TableCell? Cell;

        public int AnchorRow = -1;

        public int AnchorColumn = -1;
    }

    private Slot[][] _slots;

    private TableDocument()
    {
        _slots = Array.Empty<Slot[]>();
    }

    public TableDocument(int rows, int columns, bool firstRowIsHeader = false)
    {
        _slots = Array.Empty<Slot[]>();
        Initialize(rows, columns, firstRowIsHeader);
    }

    /// <summary>空文档（每次新建，避免共享可变态）。</summary>
    public static TableDocument Empty => new();

    public int RowCount => _slots.Length;

    public int ColumnCount => _slots.Length == 0 ? 0 : _slots[0].Length;

    public bool IsEmpty => RowCount == 0 || ColumnCount == 0;

    public bool FirstRowIsHeader()
    {
        if (IsEmpty) return false;
        for (int column = 0; column < ColumnCount; column++)
            if (IsAnchor(0, column) && _slots[0][column].Cell!.Header) return true;
        return false;
    }

    // ============ 查询 ============

    public bool Contains(int row, int column) =>
        row >= 0 && row < RowCount && column >= 0 && column < ColumnCount;

    /// <summary>这个格子归哪个锚点所有；越界返回 (-1, -1)。</summary>
    public (int Row, int Column) AnchorAt(int row, int column)
    {
        if (!Contains(row, column)) return (-1, -1);
        Slot slot = _slots[row][column];
        return (slot.AnchorRow, slot.AnchorColumn);
    }

    public bool IsAnchor(int row, int column) =>
        Contains(row, column) && _slots[row][column].AnchorRow == row &&
        _slots[row][column].AnchorColumn == column;

    /// <summary>只有锚点格有单元格数据；被合并盖住的返回 null。</summary>
    public TableCell? AnchorCellAt(int row, int column) =>
        IsAnchor(row, column) ? _slots[row][column].Cell : null;

    /// <summary>这个位置所属的单元格（盖住的格子返回锚点那一个）。</summary>
    public TableCell? CellAt(int row, int column)
    {
        (int anchorRow, int anchorColumn) = AnchorAt(row, column);
        return anchorRow >= 0 ? AnchorCellAt(anchorRow, anchorColumn) : null;
    }

    public string CellText(int row, int column) => CellAt(row, column)?.Text ?? "";

    /// <summary>这个位置所在单元格实际占的矩形（合并格会超出单个格子）。</summary>
    public TableRange SpanRangeAt(int row, int column)
    {
        (int anchorRow, int anchorColumn) = AnchorAt(row, column);
        TableCell? cell = anchorRow >= 0 ? AnchorCellAt(anchorRow, anchorColumn) : null;
        if (cell == null) return TableRange.Empty;
        return new TableRange(anchorRow, anchorColumn,
            Math.Min(RowCount - 1, anchorRow + cell.RowSpan - 1),
            Math.Min(ColumnCount - 1, anchorColumn + cell.ColumnSpan - 1));
    }

    /// <summary>把矩形扩到「所有被碰到的合并格都完整包含进来」，直到不再变化。</summary>
    public TableRange ExpandedRange(TableRange source)
    {
        TableRange range = BoundedRange(source);
        if (!range.IsValid) return TableRange.Empty;

        bool changed = true;
        while (changed)
        {
            changed = false;
            TableRange before = range;
            for (int row = before.Top; row <= before.Bottom; row++)
            {
                for (int column = before.Left; column <= before.Right; column++)
                {
                    TableRange span = SpanRangeAt(row, column);
                    if (!span.IsValid) continue;
                    range = TableRange.Union(range, span);
                }
            }

            range = BoundedRange(range);
            changed = range != before;
        }

        return range;
    }

    /// <summary>把矩形夹进文档范围（无效输入返回空）。</summary>
    public TableRange BoundedRange(TableRange range)
    {
        if (!range.IsValid || IsEmpty) return TableRange.Empty;
        var bounded = new TableRange(
            Math.Clamp(range.Top, 0, RowCount - 1),
            Math.Clamp(range.Left, 0, ColumnCount - 1),
            Math.Clamp(range.Bottom, 0, RowCount - 1),
            Math.Clamp(range.Right, 0, ColumnCount - 1));
        return bounded.IsValid ? bounded : TableRange.Empty;
    }

    // ============ 编辑 ============

    /// <summary>改文字（写到锚点）。文字没变返回 false（调用方据此决定要不要记撤销）。</summary>
    public bool SetCellText(int row, int column, string text)
    {
        (int anchorRow, int anchorColumn) = AnchorAt(row, column);
        if (anchorRow < 0) return false;
        TableCell cell = _slots[anchorRow][anchorColumn].Cell!;
        if (cell.Text == text) return false;
        cell.Text = text;
        return true;
    }

    /// <summary>选区（扩成完整合并格后）至少 2 格才谈得上合并。</summary>
    public bool CanMerge(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        return range.IsValid && (range.RowCount > 1 || range.ColumnCount > 1);
    }

    /// <summary>合并：各格文字按「非空的、去首尾空白」顺序用换行连起来。</summary>
    public bool Merge(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        if (!CanMerge(range)) return false;

        bool headerRow = FirstRowIsHeader();
        var values = new List<string>();
        var visited = new HashSet<(int, int)>();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                (int anchorRow, int anchorColumn) = AnchorAt(row, column);
                if (anchorRow < 0 || !visited.Add((anchorRow, anchorColumn))) continue;
                string value = CellText(anchorRow, anchorColumn).Trim();
                if (value.Length > 0) values.Add(value);
            }
        }

        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                _slots[row][column] = new Slot
                {
                    Cell = new TableCell { Header = headerRow && row == 0 },
                    AnchorRow = row,
                    AnchorColumn = column,
                };
            }
        }

        Slot anchor = _slots[range.Top][range.Left];
        anchor.Cell!.Text = string.Join("\n", values);
        anchor.Cell.RowSpan = range.RowCount;
        anchor.Cell.ColumnSpan = range.ColumnCount;
        anchor.Cell.Header = headerRow && range.Top == 0;
        ApplySpan(range.Top, range.Left);
        return true;
    }

    /// <summary>选区里只要有一个合并格就能拆。</summary>
    public bool CanSplit(TableRange source)
    {
        TableRange range = BoundedRange(source);
        if (!range.IsValid) return false;

        var visited = new HashSet<(int, int)>();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                (int anchorRow, int anchorColumn) = AnchorAt(row, column);
                if (anchorRow < 0 || !visited.Add((anchorRow, anchorColumn))) continue;
                TableCell? cell = AnchorCellAt(anchorRow, anchorColumn);
                if (cell != null && (cell.RowSpan > 1 || cell.ColumnSpan > 1)) return true;
            }
        }

        return false;
    }

    /// <summary>拆分选区里所有合并格；文字留在各自的左上角。</summary>
    public bool Split(TableRange source)
    {
        TableRange range = BoundedRange(source);
        if (!CanSplit(range)) return false;

        bool headerRow = FirstRowIsHeader();
        var anchors = new List<(int Row, int Column)>();
        var visited = new HashSet<(int, int)>();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                (int anchorRow, int anchorColumn) = AnchorAt(row, column);
                if (anchorRow < 0 || !visited.Add((anchorRow, anchorColumn))) continue;
                TableCell? cell = AnchorCellAt(anchorRow, anchorColumn);
                if (cell != null && (cell.RowSpan > 1 || cell.ColumnSpan > 1))
                    anchors.Add((anchorRow, anchorColumn));
            }
        }

        foreach ((int row, int column) in anchors) SplitAnchor(row, column, headerRow);
        return true;
    }

    /// <summary>
    /// 把另一份表格贴到 (top, left)（上游 <c>pasteDocument</c>）：放不下整块拒绝，
    /// 会切开已有合并格也拒绝；成功时先拆开目的区域、再按源格的 span 重新合并。
    /// </summary>
    public TablePasteResult Paste(TableDocument source, int top, int left)
    {
        if (source == null || source.IsEmpty || IsEmpty) return TablePasteResult.EmptySource;

        int bottom = top + source.RowCount - 1;
        int right = left + source.ColumnCount - 1;
        if (top < 0 || left < 0 || bottom >= RowCount || right >= ColumnCount)
            return TablePasteResult.DoesNotFit;

        for (int row = top; row <= bottom; row++)
        {
            for (int column = left; column <= right; column++)
            {
                TableRange span = SpanRangeAt(row, column);
                if (span.Top < top || span.Left < left || span.Bottom > bottom || span.Right > right)
                    return TablePasteResult.CutsMergedCell;
            }
        }

        Split(new TableRange(top, left, bottom, right));
        for (int row = 0; row < source.RowCount; row++)
        {
            for (int column = 0; column < source.ColumnCount; column++)
            {
                TableCell? cell = source.AnchorCellAt(row, column);
                if (cell == null) continue;

                int targetRow = top + row;
                int targetColumn = left + column;
                if (cell.RowSpan > 1 || cell.ColumnSpan > 1)
                {
                    Merge(new TableRange(targetRow, targetColumn,
                        Math.Min(RowCount - 1, targetRow + cell.RowSpan - 1),
                        Math.Min(ColumnCount - 1, targetColumn + cell.ColumnSpan - 1)));
                }

                SetCellText(targetRow, targetColumn, cell.Text);
            }
        }

        return TablePasteResult.Ok;
    }

    /// <summary>清空选区里的文字（合并关系与表头属性都保留）。</summary>
    public bool Clear(TableRange source)
    {
        TableRange range = BoundedRange(source);
        if (!range.IsValid) return false;

        bool changed = false;
        var visited = new HashSet<(int, int)>();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                (int anchorRow, int anchorColumn) = AnchorAt(row, column);
                if (anchorRow < 0 || !visited.Add((anchorRow, anchorColumn))) continue;
                TableCell cell = _slots[anchorRow][anchorColumn].Cell!;
                if (cell.Text.Length == 0) continue;
                cell.Text = "";
                changed = true;
            }
        }

        return changed;
    }

    public TableDocument Clone()
    {
        var clone = new TableDocument(RowCount, ColumnCount);
        for (int row = 0; row < RowCount; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
            {
                Slot source = _slots[row][column];
                clone._slots[row][column] = new Slot
                {
                    Cell = source.Cell?.Clone(),
                    AnchorRow = source.AnchorRow,
                    AnchorColumn = source.AnchorColumn,
                };
            }
        }

        return clone;
    }

    /// <summary>内容是否完全一样（撤销栈 / 「重置」按钮据此判断）。</summary>
    public bool ContentEquals(TableDocument other)
    {
        if (other == null) return false;
        if (RowCount != other.RowCount || ColumnCount != other.ColumnCount) return false;
        for (int row = 0; row < RowCount; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
            {
                Slot a = _slots[row][column];
                Slot b = other._slots[row][column];
                if (a.AnchorRow != b.AnchorRow || a.AnchorColumn != b.AnchorColumn) return false;
                TableCell? ca = a.Cell;
                TableCell? cb = b.Cell;
                if (ca == null || cb == null)
                {
                    if (ca != cb) return false;
                    continue;
                }

                if (ca.Text != cb.Text || ca.RowSpan != cb.RowSpan ||
                    ca.ColumnSpan != cb.ColumnSpan || ca.Header != cb.Header) return false;
            }
        }

        return true;
    }

    // ============ 出口 ============

    public string ToHtml() => ToHtml(new TableRange(0, 0, RowCount - 1, ColumnCount - 1));

    /// <summary>表格 HTML（贴进网页 / 富文本编辑器；合并格带 rowspan / colspan）。</summary>
    public string ToHtml(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        if (!range.IsValid) return "";

        var sb = new StringBuilder("<table style=\"border-collapse:collapse;\"><tbody>");
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            sb.Append("<tr>");
            for (int column = range.Left; column <= range.Right; column++)
            {
                if (!IsAnchor(row, column)) continue;
                TableCell cell = _slots[row][column].Cell!;
                string tag = cell.Header ? "th" : "td";
                sb.Append('<').Append(tag);
                if (cell.RowSpan > 1)
                    sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                if (cell.ColumnSpan > 1)
                    sb.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                sb.Append('>').Append(EscapeCellText(cell.Text)).Append("</").Append(tag).Append('>');
            }

            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    public string ToTsv() => ToTsv(new TableRange(0, 0, RowCount - 1, ColumnCount - 1));

    /// <summary>制表符分隔文本（上游 <c>toPlainText</c>）：行用换行、列用制表符，合并格只写左上角。</summary>
    public string ToTsv(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        if (!range.IsValid) return "";

        var sb = new StringBuilder();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            if (row > range.Top) sb.Append('\n');
            for (int column = range.Left; column <= range.Right; column++)
            {
                if (column > range.Left) sb.Append('\t');
                if (IsAnchor(row, column)) sb.Append(_slots[row][column].Cell!.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>剪贴板里的 HTML 部分：超长数字加 `'` 前缀，别让表格软件把它当数字截断。</summary>
    public string ToSpreadsheetHtml(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        if (!range.IsValid) return "";

        TableDocument copy = Clone();
        for (int row = range.Top; row <= range.Bottom; row++)
        {
            for (int column = range.Left; column <= range.Right; column++)
            {
                if (!copy.IsAnchor(row, column)) continue;
                TableCell cell = copy._slots[row][column].Cell!;
                if (NeedsSpreadsheetTextPrefix(cell.Text)) cell.Text = "'" + cell.Text;
            }
        }

        return copy.ToHtml(range);
    }

    /// <summary>整份剪贴板内容：HTML（带前缀修正）+ TSV 文本。</summary>
    public (string Html, string Text) ToClipboard(TableRange source)
    {
        TableRange range = ExpandedRange(source);
        if (!range.IsValid) return ("", "");
        return (ToSpreadsheetHtml(range), ToTsv(range));
    }

    /// <summary>
    /// 数字串太长（≥ 12 位）时表格软件会转成科学计数法或截到 15 位精度，加个 `'` 强制当文本。
    /// </summary>
    public static bool NeedsSpreadsheetTextPrefix(string text)
    {
        string trimmed = text.Trim();
        if (!NumericText.IsMatch(trimmed)) return false;
        int digits = 0;
        foreach (char c in text)
            if (c >= '0' && c <= '9') digits++;
        return digits >= 12;
    }

    // ============ 入口 ============

    /// <summary>从 OCR 的近似表格建文档（没有表头信息，全部当普通格）。</summary>
    public static TableDocument FromGrid(TableGrid? grid)
    {
        if (grid == null || grid.RowCount <= 0 || grid.Columns <= 0) return Empty;
        if (grid.RowCount > MaximumDimension || grid.Columns > MaximumDimension) return Empty;
        if (grid.RowCount * grid.Columns > MaximumCells) return Empty;

        var document = new TableDocument(grid.RowCount, grid.Columns);
        for (int row = 0; row < grid.RowCount; row++)
        {
            string[] values = grid.Rows[row];
            for (int column = 0; column < grid.Columns && column < values.Length; column++)
                document._slots[row][column].Cell!.Text = values[column];
        }

        return document;
    }

    /// <summary>从 TSV / 纯文本建文档（制表符分列、换行分行，行尾空行不算）。</summary>
    public static TableDocument FromTsv(string? source) => FromDelimited(source, '\t');

    /// <summary>从纯文本按行建文档（每行一整格）。</summary>
    public static TableDocument FromText(string? source) => FromDelimited(source, '\t');

    private static TableDocument FromDelimited(string? source, char separator)
    {
        if (string.IsNullOrEmpty(source)) return Empty;

        string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.TrimEnd('\n');
        if (normalized.Trim().Length == 0) return Empty;

        string[] lines = normalized.Split('\n');
        if (lines.Length > MaximumDimension) return Empty;

        var values = new List<string[]>(lines.Length);
        int columns = 1;
        foreach (string line in lines)
        {
            string[] parts = line.Split(separator);
            if (parts.Length > MaximumDimension) return Empty;
            columns = Math.Max(columns, parts.Length);
            values.Add(parts);
        }

        if (lines.Length * columns > MaximumCells) return Empty;

        var document = new TableDocument(values.Count, columns);
        for (int row = 0; row < values.Count; row++)
            for (int column = 0; column < values[row].Length; column++)
                document._slots[row][column].Cell!.Text = values[row][column];

        return document;
    }

    /// <summary>
    /// 从 HTML 里取第一张表（上游 <c>fromHtml</c>）：支持 rowspan / colspan、<c>th</c> 表头、
    /// 单元格内的 <c>&lt;br&gt;</c> 与嵌套表（嵌套表的结构退化成换行，不往一张表里塞两层）。
    /// </summary>
    public static TableDocument FromHtml(string? source, bool requireTable = false)
    {
        if (string.IsNullOrEmpty(source)) return Empty;
        if (source.Length > 4 * 1024 * 1024) return Empty;

        string? table = ExtractTable(source);
        if (table == null)
            return requireTable ? Empty : FromText(HtmlToPlainText(source));

        int depth = 0;
        int row = -1;
        int column = 0;
        int gridRows = 0;
        int gridColumns = 0;
        int cells = 0;
        bool inRow = false;
        bool cellOpen = false;
        bool ignoring = false;
        var text = new StringBuilder();
        var occupied = new HashSet<(int Row, int Column)>();
        var anchors = new List<(int Row, int Column, TableCell Cell)>();

        void CloseCell()
        {
            if (!cellOpen || anchors.Count == 0) return;
            (int r, int c, TableCell cell) = anchors[^1];
            cell.Text = text.ToString().Replace('\u2029', '\n').Trim();
            anchors[^1] = (r, c, cell);
            text.Clear();
            cellOpen = false;
        }

        int position = 0;
        foreach (Match match in TagPattern.Matches(table))
        {
            // 标签之间的正文：只有落在单元格里才算数
            if (!ignoring && cellOpen && match.Index > position)
                text.Append(WebUtility.HtmlDecode(table.Substring(position, match.Index - position)));
            position = match.Index + match.Length;

            string tag = match.Groups[1].Success ? match.Groups[1].Value.ToLowerInvariant() : "";
            bool closing = match.Value.StartsWith("</", StringComparison.Ordinal);
            if (tag.Length == 0) continue;   // 注释

            if (tag == "table")
            {
                if (!closing)
                {
                    if (++depth > 32) return Empty;
                    continue;
                }

                if (depth == 0) continue;
                if (--depth == 0) break;
                if (cellOpen && text.Length > 0 && text[^1] != '\n') text.Append('\n');
                continue;
            }

            if (depth == 0) continue;

            if (ignoring)
            {
                if (closing && tag == "script" || closing && tag == "style") ignoring = false;
                continue;
            }

            if (tag == "script" || tag == "style")
            {
                if (!closing) ignoring = true;
                continue;
            }

            if (depth > 1)
            {
                // 单元格里又开了一张表：结构不展开，行 / 格边界退化成换行
                if (cellOpen && (tag == "tr" || tag == "td" || tag == "th") &&
                    text.Length > 0 && text[^1] != '\n')
                {
                    text.Append('\n');
                }

                continue;
            }

            switch (tag)
            {
                case "tr":
                    if (closing)
                    {
                        CloseCell();
                        inRow = false;
                        column = 0;
                    }
                    else
                    {
                        CloseCell();
                        row++;
                        column = 0;
                        inRow = true;
                        if (row >= MaximumDimension) return Empty;
                    }

                    break;

                case "td":
                case "th":
                    if (closing)
                    {
                        CloseCell();
                        continue;
                    }

                    CloseCell();
                    if (!inRow)
                    {
                        row++;
                        column = 0;
                        inRow = true;
                        if (row >= MaximumDimension) return Empty;
                    }

                    if (++cells > MaximumCells) return Empty;
                    int rowSpan = 1;
                    int columnSpan = 1;

                    foreach (Match span in SpanPattern.Matches(match.Value))
                    {
                        string value = span.Groups[2].Success ? span.Groups[2].Value
                            : span.Groups[3].Success ? span.Groups[3].Value
                            : span.Groups[4].Value;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out int count)) continue;
                        if (count < 1) continue;
                        if (count > MaximumDimension) return Empty;
                        if (span.Groups[1].Value.Equals("rowspan", StringComparison.OrdinalIgnoreCase))
                            rowSpan = count;
                        else
                            columnSpan = count;
                    }

                    // 落到这一行里第一个没被上面的跨行格占住的位置
                    while (column + columnSpan <= MaximumDimension)
                    {
                        bool available = true;
                        for (int c = column; c < column + columnSpan; c++)
                            if (occupied.Contains((row, c))) available = false;
                        if (available) break;
                        column++;
                    }

                    if (row + rowSpan > MaximumDimension || column + columnSpan > MaximumDimension)
                        return Empty;

                    gridRows = Math.Max(gridRows, row + rowSpan);
                    gridColumns = Math.Max(gridColumns, column + columnSpan);
                    if (gridRows * gridColumns > MaximumCells) return Empty;

                    for (int r = row; r < row + rowSpan; r++)
                        for (int c = column; c < column + columnSpan; c++)
                            occupied.Add((r, c));

                    anchors.Add((row, column, new TableCell
                    {
                        RowSpan = rowSpan,
                        ColumnSpan = columnSpan,
                        Header = tag == "th",
                    }));
                    text.Clear();
                    cellOpen = true;
                    column += columnSpan;
                    break;

                case "br":
                    if (cellOpen) text.Append('\n');
                    break;

                case "p":
                case "div":
                    if (cellOpen && closing && text.Length > 0 && text[^1] != '\n') text.Append('\n');
                    break;
            }
        }

        if (cellOpen)
        {
            // 表格没闭合（浏览器粘出来的 HTML 常见）：正文照收
            if (position < table.Length)
                text.Append(WebUtility.HtmlDecode(table.Substring(position)));
            CloseCell();
        }

        if (anchors.Count == 0) return Empty;

        bool firstRowHeader = !requireTable &&
            Regex.IsMatch(source, "<th\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var document = new TableDocument(gridRows, gridColumns);
        foreach ((int r, int c, TableCell cell) in anchors)
        {
            Slot slot = document._slots[r][c];
            slot.Cell = cell;
            if (firstRowHeader && r == 0) cell.Header = true;
            document.ApplySpan(r, c);
        }

        return document;
    }

    /// <summary>只取外层第一张表的起始标签到它自己的闭合标签（嵌套表保留在正文里）。</summary>
    private static string? ExtractTable(string source)
    {
        Match start = Regex.Match(source, "<table\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!start.Success) return null;

        int depth = 0;
        foreach (Match match in TagPattern.Matches(source))
        {
            if (match.Index < start.Index) continue;
            string tag = match.Groups[1].Success ? match.Groups[1].Value.ToLowerInvariant() : "";
            if (tag != "table") continue;
            if (match.Value.StartsWith("</", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0) return source.Substring(start.Index, match.Index + match.Length - start.Index);
            }
            else
            {
                depth++;
                if (depth > 32) return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 没有表格时的退化：块级标签当换行、行内标签当空格，去掉空行（照上游 <c>fromPlainText</c>）。
    /// </summary>
    private static string HtmlToPlainText(string source)
    {
        string text = Regex.Replace(source,
            @"<\s*(?:br|/p|/div|/tr|/li|/h[1-6]|/blockquote)\s*/?>", "\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = TagPattern.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \t]+", " ");

        IEnumerable<string> lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    private static string EscapeCellText(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("\n", "<br/>");

    // ============ 内部 ============

    private void Initialize(int rows, int columns, bool firstRowHeader)
    {
        if (rows <= 0 || columns <= 0 || rows > MaximumDimension || columns > MaximumDimension ||
            rows * columns > MaximumCells) return;

        _slots = new Slot[rows][];
        for (int row = 0; row < rows; row++)
        {
            _slots[row] = new Slot[columns];
            for (int column = 0; column < columns; column++)
            {
                _slots[row][column] = new Slot
                {
                    Cell = new TableCell { Header = firstRowHeader && row == 0 },
                    AnchorRow = row,
                    AnchorColumn = column,
                };
            }
        }
    }

    /// <summary>按锚点的 span 把盖住的槽位清成「指向锚点」，span 先夹进文档范围。</summary>
    private void ApplySpan(int row, int column)
    {
        if (!IsAnchor(row, column)) return;
        Slot anchor = _slots[row][column];
        TableCell cell = anchor.Cell!;
        cell.RowSpan = Math.Clamp(cell.RowSpan, 1, RowCount - row);
        cell.ColumnSpan = Math.Clamp(cell.ColumnSpan, 1, ColumnCount - column);

        for (int coveredRow = row; coveredRow < row + cell.RowSpan; coveredRow++)
        {
            for (int coveredColumn = column; coveredColumn < column + cell.ColumnSpan; coveredColumn++)
            {
                if (coveredRow == row && coveredColumn == column) continue;
                _slots[coveredRow][coveredColumn] = new Slot
                {
                    AnchorRow = row,
                    AnchorColumn = column,
                };
            }
        }
    }

    private void SplitAnchor(int row, int column, bool headerRow)
    {
        if (!IsAnchor(row, column)) return;
        TableCell merged = _slots[row][column].Cell!;
        for (int splitRow = row; splitRow < row + merged.RowSpan; splitRow++)
        {
            for (int splitColumn = column; splitColumn < column + merged.ColumnSpan; splitColumn++)
            {
                _slots[splitRow][splitColumn] = new Slot
                {
                    Cell = new TableCell { Header = headerRow && splitRow == 0 },
                    AnchorRow = splitRow,
                    AnchorColumn = splitColumn,
                };
            }
        }

        _slots[row][column].Cell!.Text = merged.Text;
    }
}

/// <summary>粘贴结果（上游 <c>pasteDocument</c> 的两个拒绝出口各自有话说，别合成一个 false）。</summary>
internal enum TablePasteResult
{
    /// <summary>贴好了。</summary>
    Ok,

    /// <summary>剪贴板里没有能当表格用的内容。</summary>
    EmptySource,

    /// <summary>源格比目的区域大，超出识别出的表格。</summary>
    DoesNotFit,

    /// <summary>目的区域里有一块合并格只被盖住一部分，先完整选中它。</summary>
    CutsMergedCell,
}
