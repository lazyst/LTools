using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 表格识别里能纯逻辑化的部分：表格线 → 网格（含合并）、导出格式（TSV / Markdown / CSV / JSON）。
/// </summary>
/// <remarks>
/// 不跑 OCR、不开窗口：真图 + 真识别靠仓库内的 <c>tests/TableSmoke</c>。
/// </remarks>
internal static class TableSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("tableDetect", "线坐标聚类：相邻合并、远处分开", Cluster));
        cases.Add(new("tableDetect", "满格线 → 3 行 3 列，没有合并", FullGrid));
        cases.Add(new("tableDetect", "缺一段竖线 → 表头跨两列（合并）", MergeColumns));
        cases.Add(new("tableDetect", "缺一段横线 → 跨两行（合并）", MergeRows));
        cases.Add(new("tableDetect", "L 形线不算合并（别把表拆歪）", NotRectangular));
        cases.Add(new("tableDetect", "线太短 / 只有 1 行 → 不是表格", NotATable));
        cases.Add(new("tableDetect", "识别方式：自动 / 只认线 / 只按文字", DetectMode));
        cases.Add(new("tableDetect", "找到表格的判定：≥2 行 × ≥2 列且有字", FoundRule));
        cases.Add(new("tableDetect", "导出：Markdown 转义与合并留空", Markdown));
        cases.Add(new("tableDetect", "导出：CSV 引号 / 逗号 / 换行按 RFC 4180", Csv));
        cases.Add(new("tableDetect", "导出：JSON 二维数组（合并格留空）", Json));
    }

    // ============ 线 → 网格 ============

    private static void Cluster()
    {
        var values = new List<int> { 50, 52, 51, 200, 203, 400 };
        List<int> clusters = TableLines.Cluster(values);
        Check.Equal(3, clusters.Count, "三组");
        Check.Equal(51, clusters[0], "相近的取平均");
        Check.Equal(201, clusters[1], "203 / 200 同组");
        Check.Equal(400, clusters[2], "远的单独一组");
    }

    private static void FullGrid()
    {
        var size = new System.Drawing.Size(400, 400);
        var (h, v) = Grid(size, rows: 3, columns: 3);

        TableLinesResult? result = TableLines.Build(h, v, size);
        Check.True(result != null, "识别出网格");
        if (result == null) return;

        Check.Equal(3, result.RowCount, "3 行");
        Check.Equal(3, result.ColumnCount, "3 列");
        Check.False(result.Merges.Count > 0, "没有合并");
        Check.True(result.HasTable, "2×2 以上算表格");
        Check.Equal(result.ColumnBounds[3] - result.ColumnBounds[2], result.CellRect(1, 2).Width, "单元格宽度来自边界");
    }

    private static void MergeColumns()
    {
        var size = new System.Drawing.Size(200, 200);
        var (h, v) = Grid(size, rows: 2, columns: 2);

        // 中间那条竖线只画了下半段：第一行的两格没被分开
        v.RemoveAll(s => !s.Horizontal && s.At == 100);
        v.Add(new TableSegment(false, 100, 100, 200));

        TableLinesResult? result = TableLines.Build(h, v, size);
        Check.True(result?.Merges.Count == 1, "有一处合并");
        if (result == null || result.Merges.Count != 1) return;

        TableCellSpan span = result.Merges[0];
        Check.Equal(0, span.Row, "合并从第 0 行开始");
        Check.Equal(0, span.Column, "合并从第 0 列开始");
        Check.Equal(1, span.RowSpan, "不跨行");
        Check.Equal(2, span.ColumnSpan, "跨两列");
        Check.True(result.IsAnchor(0, 0) && !result.IsAnchor(0, 1), "只有左上角是锚点");
        Check.Equal(0, result.SpanAt(0, 1).Column, "被盖住的格子归到锚点");
    }

    private static void MergeRows()
    {
        var size = new System.Drawing.Size(200, 200);
        var (h, v) = Grid(size, rows: 2, columns: 2);

        // 中间那条横线只画了右半段：左列的两格没被分开
        h.RemoveAll(s => s.Horizontal && s.At == 100);
        h.Add(new TableSegment(true, 100, 100, 200));

        TableLinesResult? result = TableLines.Build(h, v, size);
        Check.True(result?.Merges.Count == 1, "有一处合并");
        if (result == null || result.Merges.Count != 1) return;

        TableCellSpan span = result.Merges[0];
        Check.Equal(2, span.RowSpan, "跨两行");
        Check.Equal(1, span.ColumnSpan, "不跨列");
    }

    private static void NotRectangular()
    {
        var size = new System.Drawing.Size(200, 200);

        // 2×2，但缺的两段线正好凑出一个 L：竖线只画下半段（第 0 行两格通），
        // 横线只画左半段（右列上下两格通）→ 合并组是 L 形，不该被当成一个合并格
        var h = new List<TableSegment>
        {
            new(true, 0, 0, 200), new(true, 100, 0, 100), new(true, 200, 0, 200),
        };
        var v = new List<TableSegment>
        {
            new(false, 0, 0, 200), new(false, 100, 100, 200), new(false, 200, 0, 200),
        };

        TableLinesResult? result = TableLines.Build(h, v, size);
        Check.True(result != null, "还是能识别出网格");
        Check.Equal(0, result?.Merges.Count ?? -1, "L 形不算合并");
    }

    private static void NotATable()
    {
        var size = new System.Drawing.Size(400, 400);
        var (h, v) = Grid(size, rows: 1, columns: 3);
        Check.True(TableLines.Build(h, v, size) == null, "只有 1 行 → 不是表格");

        // 线只占图宽的 10%，比 25% 的门槛短 → 不算表格线
        var shortH = new List<TableSegment> { new(true, 10, 0, 40), new(true, 50, 0, 40), new(true, 90, 0, 40) };
        var shortV = new List<TableSegment> { new(false, 0, 0, 100), new(false, 40, 0, 100), new(false, 80, 0, 100) };
        Check.True(TableLines.Build(shortH, shortV, new System.Drawing.Size(800, 800)) == null, "太短的线不算");
    }

    /// <summary>满格线：rows × columns 个格子，覆盖整张图。</summary>
    private static (List<TableSegment> H, List<TableSegment> V) Grid(System.Drawing.Size size, int rows, int columns)
    {
        var h = new List<TableSegment>();
        var v = new List<TableSegment>();

        for (int row = 0; row <= rows; row++)
        {
            int y = row * size.Height / rows;
            h.Add(new TableSegment(true, y, 0, size.Width));
        }

        for (int column = 0; column <= columns; column++)
        {
            int x = column * size.Width / columns;
            v.Add(new TableSegment(false, x, 0, size.Height));
        }

        return (h, v);
    }

    // ============ 单元级的小判定 ============

    private static void DetectMode()
    {
        Check.Equal(TableDetectMode.auto, TableReader.ParseMode(""), "空 = 自动");
        Check.Equal(TableDetectMode.auto, TableReader.ParseMode("AUTO"), "大小写不敏感");
        Check.Equal(TableDetectMode.lines, TableReader.ParseMode("lines"), "只认线");
        Check.Equal(TableDetectMode.cluster, TableReader.ParseMode("cluster"), "只按文字");
        Check.Equal(TableDetectMode.auto, TableReader.ParseMode("garbage"), "认不出的值按自动");
    }

    private static void FoundRule()
    {
        Check.False(new TableReadResult().Found, "空表不算找到");
        Check.False(new TableReadResult { Table = new TableDocument(1, 3) }.Found, "1 行不算");
        Check.False(new TableReadResult { Table = new TableDocument(3, 1) }.Found, "1 列不算");

        var empty = new TableDocument(2, 2);
        Check.False(new TableReadResult { Table = empty }.Found, "2×2 但一个格子都没字不算");

        var table = new TableDocument(2, 2);
        table.SetCellText(1, 1, "有字");
        Check.True(new TableReadResult { Table = table }.Found, "2×2 且有一格有字就算");
    }

    // ============ 导出格式 ============

    /// <summary>2 行 3 列：表头跨两列 + 各种需要转义的字符。</summary>
    private static TableDocument Sample()
    {
        var table = new TableDocument(2, 3);
        table.Merge(new TableRange(0, 0, 0, 1));       // 表头跨两列
        table.SetCellText(0, 0, "表头|A");
        table.SetCellText(0, 2, "含,逗号");
        table.SetCellText(1, 0, "引\"号");
        table.SetCellText(1, 1, "多\n行");
        table.SetCellText(1, 2, " 前后空格 ");
        return table;
    }

    private static void Markdown()
    {
        string md = Sample().ToMarkdown();
        string[] lines = md.Split('\n');

        Check.Equal(3, lines.Length, "表头 + 分隔行 + 数据行");
        Check.True(lines[1].Contains("---"), "第二行是分隔行");
        Check.True(md.Contains("表头\\|A"), "竖线转义");
        Check.True(md.Contains("<br>"), "换行变 <br>");
        Check.True(lines[0].StartsWith("| "), "以 | 开头");
        Check.Equal(3, CountCells(lines[1]), "分隔行 3 格（合并留空）");
        Check.True(lines[2].Contains("  |"), "被合并盖住的格子留空");
    }

    private static int CountCells(string line) => line.Split('|').Length - 2;

    private static void Csv()
    {
        string csv = Sample().ToCsv();
        string[] lines = csv.Split("\r\n");

        Check.Equal(2, lines.Length, "两行");
        Check.True(lines[0].Contains("\"含,逗号\""), "含逗号 → 加引号");
        Check.True(lines[1].Contains("\"引\"\"号\""), "引号翻倍");
        Check.True(lines[1].Contains("\"多\n行\""), "换行 → 整格加引号（引号内保留换行）");
        Check.True(lines[1].Contains("\" 前后空格 \""), "首尾空白 → 加引号");
        Check.Equal(3, CountCsvFields(lines[0]), "表头行 3 格（含,逗号 在引号里）");
    }

    /// <summary>按 CSV 规则数字段（引号内的逗号不算分隔）。</summary>
    private static int CountCsvFields(string line)
    {
        int fields = 1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (c == ',' && !quoted) fields++;
        }
        return fields;
    }

    private static void Json()
    {
        string json = Sample().ToJson();
        using JsonDocument doc = JsonDocument.Parse(json);
        Check.Equal(JsonValueKind.Array, doc.RootElement.ValueKind, "顶层是数组");
        Check.Equal(2, doc.RootElement.GetArrayLength(), "两行");

        JsonElement header = doc.RootElement[0];
        Check.Equal(3, header.GetArrayLength(), "第一行 3 列");
        Check.Equal("表头|A", header[0].GetString(), "锚点有值");
        Check.Equal("", header[1].GetString(), "被合并盖住的格子是空串");
        Check.Equal("含,逗号", header[2].GetString(), "普通格原样");
    }
}
