using System;
using System.Collections.Generic;
using LTools.Features;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 图片转 Markdown / HTML 的本地派生：行高判标题、纵向间距判段落、行首记号判列表、
/// 表格区域剔除、两份源码（含转义）。
/// </summary>
/// <remarks>
/// 纯函数 + 行框数据，<b>不开窗口</b>；预览渲染与复制走仓库外 <c>capture-overlay-smoke</c>。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static OcrLineBox Line(string text, int x, int y, int w, int h = 20,
        IReadOnlyList<OcrWordBox>? words = null) => new()
    {
        Text = text,
        X = x,
        Y = y,
        W = w,
        H = h,
        Words = words ?? Array.Empty<OcrWordBox>(),
    };

    private static OcrWordBox Word(string text, int x, int y, int w, int h = 20) => new()
    {
        Text = text,
        X = x,
        Y = y,
        W = w,
        H = h,
    };

    private static void ConversionBlockCases()
    {
        var lines = new List<OcrLineBox>
        {
            Line("报告", 10, 10, 120, 40),          // 行高 2 倍 → 一级标题
            Line("第一行文字", 10, 70, 200),         // 中文接段落不补空格
            Line("第二行文字", 10, 92, 200),
            Line("• 项目一", 10, 130, 100),
            Line("• 项目二", 10, 152, 100),
            Line("1. 步骤一", 10, 182, 100),
            Line("2、步骤二", 10, 204, 100),
            Line("Hello", 10, 240, 60),             // 同一行的两截英文 → 补空格接起来
            Line("World", 80, 240, 60),
        };

        ConversionDocument document = ConversionDocument.Build(lines, TableGrid.Empty);
        Check.Equal(7, document.Blocks.Count, "块数：标题 + 段落 + 两条项目 + 两条步骤 + 英文段落");

        Check.True(document.Blocks[0].Kind == ConversionKind.Heading, "第一块是标题");
        Check.Equal(1, document.Blocks[0].Level, "一级标题");
        Check.Equal("报告", document.Blocks[0].Text, "标题文字");

        Check.True(document.Blocks[1].Kind == ConversionKind.Paragraph, "第二块是段落");
        Check.Equal("第一行文字第二行文字", document.Blocks[1].Text, "中文行接起来不补空格");

        Check.True(document.Blocks[2].Kind == ConversionKind.Bullet, "• 是列表项");
        Check.Equal("项目一", document.Blocks[2].Text, "列表项去掉记号");
        Check.True(document.Blocks[3].Kind == ConversionKind.Bullet, "第二条也是列表项");

        Check.True(document.Blocks[4].Kind == ConversionKind.Ordered, "1. 是有序列表");
        Check.Equal("步骤一", document.Blocks[4].Text, "有序项去掉编号");
        Check.True(document.Blocks[5].Kind == ConversionKind.Ordered, "2、也算有序列表");

        // 英文行：同一行两截要补一个空格（中文才紧贴）
        var english = new List<OcrLineBox>
        {
            Line("Hello", 10, 10, 60),
            Line("World", 80, 10, 60),
        };
        ConversionDocument text = ConversionDocument.Build(english, TableGrid.Empty);
        Check.Equal(1, text.Blocks.Count, "同一行的两截归成一个段落");
        Check.Equal("Hello World", text.Blocks[0].Text, "英文接段落补空格");

        // 空输入
        ConversionDocument empty = ConversionDocument.Build(Array.Empty<OcrLineBox>(), TableGrid.Empty);
        Check.True(empty.IsEmpty, "没有行就是空文档");
        Check.Equal("", empty.Markdown, "空文档没有 Markdown");
        Check.Equal("", empty.Html, "空文档没有 HTML");
        Check.Equal(0, empty.Length, "空文档没有字符");
    }

    private static void ConversionSourceCases()
    {
        var lines = new List<OcrLineBox>
        {
            Line("表格上方的一段", 10, 10, 200),
            Line("姓名 年龄", 10, 60, 350, 20,
                new[] { Word("姓名", 10, 60, 60), Word("年龄", 300, 60, 60) }),
            Line("张三 18", 10, 90, 350, 20,
                new[] { Word("张三", 10, 90, 60), Word("18", 300, 90, 60) }),
        };

        TableGrid table = OcrTable.Build(lines);
        Check.True(table.HasTable, "两行两列被判成表格");
        Check.True(OcrTable.TableArea(lines) != null, "表格区域算得出来");

        ConversionDocument document = ConversionDocument.Build(lines, table);
        Check.Equal(2, document.Blocks.Count, "表格里的行不再重复成段落");
        Check.True(document.Blocks[0].Kind == ConversionKind.Paragraph, "表格上方那段仍是段落");
        Check.True(document.Blocks[1].Kind == ConversionKind.Table, "最后一块是表格");
        Check.Equal(2, document.Blocks[1].Rows.Count, "表格两行");

        string markdown = document.Markdown;
        Check.Equal("表格上方的一段\r\n\r\n| 姓名 | 年龄 |\r\n| --- | --- |\r\n| 张三 | 18 |", markdown,
            "Markdown：段落 + 管道表格");

        string html = document.Html;
        Check.Equal("<p>表格上方的一段</p><table><thead><tr><th>姓名</th><th>年龄</th></tr></thead>" +
                    "<tbody><tr><td>张三</td><td>18</td></tr></tbody></table>", html,
            "HTML：段落 + 表头 th + 表体 td");

        // 转义：HTML 尖括号与 &，Markdown 单元格里的竖线 / 换行
        var escapes = new List<OcrLineBox>
        {
            Line("<b>a & b</b>", 10, 10, 200),
        };
        var table2 = new TableGrid(new[]
        {
            new[] { "a|b", "c\nd" },
            new[] { "e", "f" },
        }, 2);
        ConversionDocument escaped = ConversionDocument.Build(escapes, table2);

        Check.True(escaped.Html.Contains("&lt;b&gt;a &amp; b&lt;/b&gt;"), "HTML 转义尖括号与 &");
        Check.True(escaped.Markdown.Contains("| a\\|b | c d |"), "Markdown 表格转义竖线并把换行压成空格");

        // 列表在两边都成组：一个 <ul> 包住两项
        var listLines = new List<OcrLineBox>
        {
            Line("• 一", 10, 10, 100),
            Line("• 二", 10, 32, 100),
        };
        ConversionDocument list = ConversionDocument.Build(listLines, TableGrid.Empty);
        Check.Equal("- 一\r\n- 二", list.Markdown, "Markdown 紧凑列表");
        Check.Equal("<ul><li>一</li><li>二</li></ul>", list.Html, "HTML 一个 ul 包住两项");
        Check.Equal(2, list.Length, "字符数只算正文");
    }
}
