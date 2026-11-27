using System;
using System.Collections.Generic;
using System.Drawing;
using LTools.Core.Actions;
using LTools.Features.CapturePro;

namespace LTools.Features.Units.Screen;

/// <summary>
/// 表格识别：把一张图（或现截的一块屏幕）里的表格读成「行 × 列」。
/// </summary>
/// <remarks>
/// <para>
/// 还原由 <see cref="TableReader"/> 负责（有线框按线、没线框按文字位置聚类），
/// 这里只做参数与输出整形：<c>rows</c> / <c>columns</c> / <c>rowCount</c> / <c>merges</c> /
/// <c>text</c> / <c>found</c>。
/// </para>
/// <para>
/// 合并单元格按用户约定**只填左上角**，其余格是空串；跨度另给 <c>merges</c>，
/// HTML 输出里带 <c>rowspan / colspan</c>，TSV / CSV / Markdown 只能展开成留空。
/// 认不出 2 行 × 2 列只是 <c>found = false</c>，不是失败（同「文字识别」）。
/// </para>
/// </remarks>
public sealed class TableUnit : IStepUnit
{
    private static readonly string[] Engines = { "windows", "v6tiny", "v6small", "v6medium" };

    /// <summary>图片来源：图片（默认，来自「截图」或文件）/ 前台窗口 / 整个屏幕 / 固定区域。</summary>
    private static readonly string[] Sources = { "image", "foreground", "screen", "rect" };

    private static readonly string[] Detects = { "auto", "lines", "cluster" };

    private static readonly string[] Formats = { "tsv", "markdown", "html", "csv", "json" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.table",
        Name = "表格识别",
        Category = "屏幕",
        Icon = "ocr",
        Description = "把表格读成行与列：有表格线的按线、没有的按文字位置聚类；合并单元格只填左上角，跨度见「合并单元格」输出",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("source", "图片来源", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("image"), Options: Sources,
                OptionParams: OptionMap.Of(
                    ("image", ["image"]),
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("image", "图片", Editor: InputEditor.image, Required: true),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
            new InputSpec("engine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6small"), Options: Engines,
                Description: "表格建议用 PP-OCRv6：词框更细，列分得更准（系统 OCR 的粗词框容易把窄列并掉）"),
            new InputSpec("detect", "识别方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("auto"), Options: Detects,
                Description: "自动 = 有表格线就按线、没有就按文字位置聚类；只认线框 = 找不到线就算没表格；只按文字位置 = 不看线"),
            new InputSpec("format", "文本格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("tsv"), Options: Formats,
                Description: "决定「表格文本」输出：TSV 贴进 Excel 直接分列，CSV 会做转义"),
        },
        Outputs = new[]
        {
            new OutputSpec("rows", "行", ValueKind.list, "rows",
                Description: "每行一个列表，空格子是空串；合并单元格只在左上角有值"),
            new OutputSpec("columns", "列数", ValueKind.number, "columns"),
            new OutputSpec("rowCount", "行数", ValueKind.number, "rowCount"),
            new OutputSpec("merges", "合并单元格", ValueKind.list, "merges",
                Description: "每项 {row, col, rowSpan, colSpan}，行列从 0 开始；没有合并时是空列表"),
            new OutputSpec("text", "表格文本", ValueKind.text, "tableText",
                Description: "按「文本格式」拼好的整张表；没找到表格时为空"),
            new OutputSpec("found", "找到表格", ValueKind.@bool, "tableFound",
                Description: "≥2 行 × ≥2 列且至少一格有字"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string source = args.Text("source").Trim().ToLowerInvariant();
        if (source.Length == 0) source = "image";

        string format = args.Text("format").Trim().ToLowerInvariant();
        if (format.Length == 0) format = "tsv";

        using Bitmap bitmap = ScreenBitmap.Load(ctx, args, source);

        TableReadResult result = TableReader.Read(bitmap, args.Text("engine").Trim(), args.Text("detect").Trim());
        TableDocument table = result.Table;
        bool found = result.Found;

        ctx.SetOutput("rows", found ? Rows(table) : Value.FromList());
        ctx.SetOutput("columns", Value.FromNumber(found ? table.ColumnCount : 0));
        ctx.SetOutput("rowCount", Value.FromNumber(found ? table.RowCount : 0));
        ctx.SetOutput("merges", found ? Merges(table) : Value.FromList());
        ctx.SetOutput("text", Value.FromText(found ? Format(table, format) : ""));
        ctx.SetOutput("found", Value.FromBool(found));
    }

    /// <summary>二维列表：只写锚点（合并格覆盖的位置留空串）。</summary>
    private static Value Rows(TableDocument table)
    {
        var rows = new List<Value>(table.RowCount);
        for (int row = 0; row < table.RowCount; row++)
        {
            var cells = new List<Value>(table.ColumnCount);
            for (int column = 0; column < table.ColumnCount; column++)
                cells.Add(Value.FromText(table.IsAnchor(row, column) ? table.CellText(row, column) : ""));
            rows.Add(Value.FromList(cells));
        }

        return Value.FromList(rows);
    }

    /// <summary>合并单元格清单：<c>{row, col, rowSpan, colSpan}</c>（只列跨行 / 跨列的）。</summary>
    private static Value Merges(TableDocument table)
    {
        var list = new List<Value>();
        for (int row = 0; row < table.RowCount; row++)
        {
            for (int column = 0; column < table.ColumnCount; column++)
            {
                if (!table.IsAnchor(row, column)) continue;

                TableRange span = table.SpanRangeAt(row, column);
                if (!span.IsValid || (span.RowCount <= 1 && span.ColumnCount <= 1)) continue;

                list.Add(Value.Object(
                    ("row", Value.FromNumber(span.Top)),
                    ("col", Value.FromNumber(span.Left)),
                    ("rowSpan", Value.FromNumber(span.RowCount)),
                    ("colSpan", Value.FromNumber(span.ColumnCount))));
            }
        }

        return Value.FromList(list);
    }

    private static string Format(TableDocument table, string format) => format switch
    {
        "markdown" => table.ToMarkdown(),
        "html" => table.ToHtml(),
        "csv" => table.ToCsv(),
        "json" => table.ToJson(),
        _ => table.ToTsv(),
    };
}
