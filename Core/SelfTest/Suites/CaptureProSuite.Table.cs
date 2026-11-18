using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 识别表格：槽位模型（合并 / 拆分 / 清空 / 扩展选区）、HTML / TSV 往返、粘贴规则、
/// xlsx 导出的 part 结构与转义。
/// </summary>
/// <remarks>
/// 纯数据 + 纯字节，<b>不开窗口</b>：表格画布（命中 / 拖选 / 内嵌编辑框）与结果窗工具条走
/// 仓库外冒烟（<c>capture-overlay-smoke</c>）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void TableDocumentCases()
    {
        var document = new TableDocument(2, 3);
        Check.Equal(2, document.RowCount, "2 行");
        Check.Equal(3, document.ColumnCount, "3 列");
        Check.False(document.IsEmpty, "有格子就不是空文档");
        Check.True(document.IsAnchor(0, 0), "默认每格都是自己的锚点");
        Check.Equal((1, 2), document.AnchorAt(1, 2), "锚点就是自己");

        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 3; column++)
                Check.True(document.SetCellText(row, column, $"{row}{column}"), "改文字");
        Check.False(document.SetCellText(0, 0, "00"), "文字没变返回 false（别记撤销）");

        // 合并：文字按行优先、去空白、换行连起来，左上角当锚点
        Check.True(document.CanMerge(new TableRange(0, 0, 0, 1)), "同一行两格能合并");
        Check.False(document.CanMerge(TableRange.Single(1, 1)), "单格不能合并");
        Check.False(document.CanMerge(TableRange.Empty), "没选区不能合并");
        Check.True(document.Merge(new TableRange(0, 0, 0, 1)), "合并成功");
        Check.Equal(new TableRange(0, 0, 0, 1), document.SpanRangeAt(0, 1), "盖住的格子指向锚点的整块");
        Check.Equal(2, document.AnchorCellAt(0, 0)!.ColumnSpan, "锚点记跨 2 列");
        Check.False(document.IsAnchor(0, 1), "被盖住的不是锚点");
        Check.Equal("00\n01", document.CellText(0, 1), "合并后的文字（两格都能读到）");
        Check.Equal((0, 0), document.AnchorAt(0, 1), "盖住的格子 AnchorAt 回锚点");

        // 拖动只碰到合并格的一部分 → 选区自动扩成整块
        Check.Equal(new TableRange(0, 0, 0, 1),
            document.ExpandedRange(TableRange.Single(0, 1)), "选中半块合并格 = 选中整块");
        Check.Equal(new TableRange(0, 0, 1, 2),
            document.ExpandedRange(new TableRange(0, 0, 1, 2)), "整块选区原样");
        Check.Equal(TableRange.Empty, document.ExpandedRange(TableRange.Empty), "空选区还是空");

        // 拆分：文字留在左上角，其它格回来当独立格
        Check.True(document.CanSplit(new TableRange(0, 0, 1, 2)), "选区里有合并格就能拆");
        Check.True(document.Split(new TableRange(0, 0, 1, 2)), "拆分成功");
        Check.Equal(1, document.AnchorCellAt(0, 0)!.ColumnSpan, "span 回到 1");
        Check.True(document.IsAnchor(0, 1), "拆开之后是独立锚点");
        Check.Equal("00\n01", document.CellText(0, 0), "文字留在左上角");
        Check.Equal("", document.CellText(0, 1), "拆出来的格子是空的");
        Check.False(document.CanSplit(new TableRange(0, 0, 1, 2)), "没有合并格就拆不了");

        // 清空只清文字，结构不动
        Check.True(document.Merge(new TableRange(0, 0, 1, 0)), "先竖着合并一列");
        Check.True(document.Clear(new TableRange(0, 0, 1, 2)), "清空选中区域");
        Check.Equal("", document.CellText(0, 0), "文字清掉了");
        Check.Equal(2, document.AnchorCellAt(0, 0)!.RowSpan, "合并关系还在");
        Check.False(document.Clear(new TableRange(0, 0, 1, 2)), "已经空了 → false");

        // 上限：单边 256、总格 16384
        Check.True(new TableDocument(257, 2).IsEmpty, "单边超 256 = 空文档");
        Check.True(new TableDocument(200, 200).IsEmpty, "总格超 16384 = 空文档");
        Check.False(new TableDocument(128, 128).IsEmpty, "128 × 128 = 16384 可以");

        // 表头：构造时给第一行标记
        var header = new TableDocument(2, 2, firstRowIsHeader: true);
        Check.True(header.FirstRowIsHeader(), "第一行是表头");
        Check.True(header.AnchorCellAt(1, 0)!.Header == false, "第二行不是表头");
        Check.True(header.ToHtml().Contains("<th></th>"), "表头写成 th");
    }

    private static void TableHtmlCases()
    {
        // 跨列 / 跨行 / th 表头 / <br> 换行
        const string source =
            "<table><tr><th>A</th><th>B</th></tr>" +
            "<tr><td rowspan=\"2\">C<br>第二行</td><td>D</td></tr>" +
            "<tr><td>E</td></tr></table>";

        TableDocument document = TableDocument.FromHtml(source);
        Check.Equal(3, document.RowCount, "三行（rowspan 撑出来的那行也算）");
        Check.Equal(2, document.ColumnCount, "两列");
        Check.Equal("A", document.CellText(0, 0), "表头文字");
        Check.True(document.AnchorCellAt(0, 0)!.Header, "th 是表头");
        Check.Equal(2, document.AnchorCellAt(1, 0)!.RowSpan, "rowspan=2 记在锚点上");
        Check.Equal("C\n第二行", document.CellText(1, 0), "<br> 变换行");
        Check.Equal("D", document.CellText(1, 1), "同一行第二格");
        Check.Equal("E", document.CellText(2, 1), "下一行让开跨行格落在第 2 列");
        Check.Equal("C\n第二行", document.CellText(2, 0), "跨行格盖住的位置读到同一格");

        // HTML 往返：span / 表头 / 换行都留着
        string html = document.ToHtml();
        Check.True(html.Contains("rowspan=\"2\""), "导出的 HTML 带 rowspan");
        Check.True(html.Contains("<th>A</th>"), "表头导出成 th");
        Check.True(html.Contains("C<br/>第二行"), "单元格换行导出成 br");
        TableDocument roundtrip = TableDocument.FromHtml(html);
        Check.True(roundtrip.ContentEquals(document), "HTML 往返内容一致");

        // TSV：合并格只写左上角，其它位置留空
        Check.Equal("A\tB\nC\n第二行\tD\n\tE", document.ToTsv(), "TSV 每行一列一制表符");

        // 没有表：requireTable = false 退化成纯文本，true 则拒绝
        Check.True(TableDocument.FromHtml("<p>只有字</p>", requireTable: true).IsEmpty,
            "要求是表格时，没有 table 就不收");
        TableDocument plain = TableDocument.FromHtml("<p>第一行</p><p>第二行</p>");
        Check.Equal("第一行\n第二行", plain.ToTsv(), "没有表就按段落退化成行");

        // 单元格里再套一张表：结构不展开，退化成换行
        TableDocument nested = TableDocument.FromHtml(
            "<table><tr><td>外<table><tr><td>内</td></tr></table></td><td>右</td></tr></table>");
        Check.Equal(1, nested.RowCount, "嵌套表不撑出新的行");
        Check.Equal(2, nested.ColumnCount, "外层还是两列");
        Check.Equal("外\n内", nested.CellText(0, 0), "嵌套内容退化成换行");
        Check.Equal("右", nested.CellText(0, 1), "右边那格不受影响");

        // TSV 入口：换行 / 制表符、行尾空行不额外多一行
        TableDocument tsv = TableDocument.FromTsv("a\tb\r\n1\t2\r\n");
        Check.Equal(2, tsv.RowCount, "两行");
        Check.Equal(2, tsv.ColumnCount, "两列");
        Check.Equal("2", tsv.CellText(1, 1), "第二行第二列");
    }

    private static void TablePasteCases()
    {
        TableDocument target = new TableDocument(3, 3);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                target.SetCellText(row, column, $"{row}{column}");

        var source = new TableDocument(2, 2);
        source.SetCellText(0, 0, "x");
        source.SetCellText(1, 1, "y");

        Check.Equal(TablePasteResult.Ok, target.Paste(source, 1, 1), "2×2 贴进 3×3 右下角");
        Check.Equal("x", target.CellText(1, 1), "第一格贴上了");
        Check.Equal("y", target.CellText(2, 2), "第二行第二格贴上了");
        Check.Equal("", target.CellText(1, 2), "源里的空格照样清掉目的格（不是跳过）");

        Check.Equal(TablePasteResult.DoesNotFit, target.Paste(source, 2, 2), "放不下就拒绝");
        Check.Equal(TablePasteResult.EmptySource, target.Paste(TableDocument.Empty, 0, 0),
            "空源 = 没有可贴的");

        // 目的区域切开了合并格 → 拒绝（上游的第二个出口）
        var merged = new TableDocument(3, 3);
        merged.SetCellText(0, 0, "合并");
        merged.Merge(new TableRange(0, 0, 1, 1));
        var single = new TableDocument(1, 1);
        single.SetCellText(0, 0, "单");
        Check.Equal(TablePasteResult.CutsMergedCell, merged.Paste(single, 0, 0),
            "只盖住合并格一半 → 拒绝");
        Check.Equal("合并", merged.CellText(0, 0), "拒绝时文档不动");

        // 带跨行 / 跨列的源贴过去，目的区域跟着合并
        var spanned = new TableDocument(2, 2);
        spanned.SetCellText(0, 0, "宽");
        spanned.Merge(new TableRange(0, 0, 0, 1));
        Check.Equal(TablePasteResult.Ok, target.Paste(spanned, 0, 0), "贴一块跨列的源");
        Check.Equal(2, target.AnchorCellAt(0, 0)!.ColumnSpan, "目的区域也合并成 2 列");
        Check.Equal("宽", target.CellText(0, 1), "合并后的文字读得到");
    }

    private static void TableXlsxCases()
    {
        var document = new TableDocument(2, 2, firstRowIsHeader: true);
        document.SetCellText(0, 0, "名称");
        document.SetCellText(0, 1, "数量");
        document.SetCellText(1, 0, "苹果");
        document.SetCellText(1, 1, "123456789012");
        document.Merge(new TableRange(0, 0, 0, 1));

        byte[]? bytes = XlsxWriter.Build(document, new[] { 120, 90 });
        Check.True(bytes != null, "写出了工作簿");

        using var stream = new MemoryStream(bytes!);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        string[] names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
        foreach (string required in new[]
                 {
                     "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                     "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml",
                 })
        {
            Check.True(names.Contains(required), "六个 part 都在：" + required);
        }

        string sheet = Compact(ReadEntry(archive, "xl/worksheets/sheet1.xml"));
        Check.True(sheet.Contains("t=\"inlineStr\""), "单元格是 inlineStr（不建 sharedStrings）");
        Check.True(sheet.Contains("s=\"1\""), "表头用第 2 个样式（加粗）");
        Check.True(sheet.Contains("<mergeCell ref=\"A1:B1\"/>"), "合并格写进 mergeCells");
        Check.True(sheet.Contains(">苹果<"), "中文原样写进去");
        Check.True(sheet.Contains("xml:space=\"preserve\""), "文本节点保留空白");
        Check.True(sheet.Contains("<col min=\"1\" max=\"1\""), "列宽按像素折成 1/256 字符宽");

        string styles = Compact(ReadEntry(archive, "xl/styles.xml"));
        Check.True(styles.Contains("<b/>"), "styles 里有加粗字体");
        Check.True(styles.Contains("numFmtId=\"49\""), "文本格式（前导零 / 长数字不被吃掉）");
        Check.True(styles.Contains("wrapText=\"1\""), "自动换行");

        string workbook = ReadEntry(archive, "xl/workbook.xml");
        Check.True(workbook.Contains("<sheet "), "workbook 里有一张表");

        Check.True(XlsxWriter.Build(TableDocument.Empty) == null, "空文档不写工作簿");

        // 单元格引用
        Check.Equal("A1", XlsxWriter.CellReference(0, 0), "第一格 A1");
        Check.Equal("Z10", XlsxWriter.CellReference(9, 25), "第 26 列是 Z");
        Check.Equal("AA1", XlsxWriter.CellReference(0, 26), "第 27 列是 AA");
        Check.Equal("XFD1048576", XlsxWriter.CellReference(1_048_575, 16_383), "Excel 最后一格");

        // SpreadsheetML 自己的转义：控制字符 / 「看起来像 _xHHHH_」都要转义
        Check.Equal("普通文本", XlsxWriter.SpreadsheetText("普通文本"), "普通文本原样");
        Check.Equal("a_x0001_b", XlsxWriter.SpreadsheetText("a\u0001b"), "控制字符转 _xHHHH_");
        Check.Equal("a\nb", XlsxWriter.SpreadsheetText("a\nb"), "换行保留（xml 自己会转义）");
        Check.Equal("_x005F_x0041_", XlsxWriter.SpreadsheetText("_x0041_"),
            "字面的 _x0041_ 先把下划线转义，别被当编码吃掉");
        Check.Equal("_xZZZZ_", XlsxWriter.SpreadsheetText("_xZZZZ_"),
            "不像编码的 _x 原样保留（上游只转义 _xHHHH_ 形状）");

        // 剪贴板长数字加 `'` 前缀（上游 needsSpreadsheetTextPrefix）
        Check.True(TableDocument.NeedsSpreadsheetTextPrefix("123456789012"), "12 位数字要加前缀");
        Check.False(TableDocument.NeedsSpreadsheetTextPrefix("12345678901"), "11 位不加");
        Check.False(TableDocument.NeedsSpreadsheetTextPrefix("苹果"), "文本不加");
        Check.False(TableDocument.NeedsSpreadsheetTextPrefix("1.2345678901"),
            "小数点也算数字但只有 11 位 → 不加");
        Check.False(TableDocument.NeedsSpreadsheetTextPrefix("12 3456"), "不是纯数字");

        var clipboard = new TableDocument(1, 2);
        clipboard.SetCellText(0, 0, "123456789012");
        clipboard.SetCellText(0, 1, "短");
        (string html, string text) = clipboard.ToClipboard(
            new TableRange(0, 0, 0, 1));
        Check.True(html.Contains("'123456789012"), "剪贴板 HTML 加了 ' 前缀");
        Check.Equal("123456789012\t短", text, "纯文本部分不加前缀（粘贴到记事本还是原样）");
    }

    /// <summary>XmlWriter 把空元素写成 <c>&lt;b /&gt;</c>（斜杠前有空格），比对前先抹平。</summary>
    private static string Compact(string xml) => xml.Replace(" />", "/>");

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry? entry = archive.GetEntry(name);
        if (entry == null) return "";
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
