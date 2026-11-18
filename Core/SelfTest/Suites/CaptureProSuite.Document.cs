using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using LTools.Features;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 文档导出（Word / LaTeX / HTML / 纯文本）：照上游 <c>screenshotdocumentexport.cpp</c>
/// 的出口，内容来自本地的版面派生块。
/// </summary>
/// <remarks>
/// PDF 走系统浏览器的 headless 打印，依赖机器上装了 Edge / Chrome，<b>不在自测里跑</b>
/// （仓库外 <c>capture-overlay-smoke</c> 会按「有没有引擎」分别断言成功或明确报错）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static ConversionDocument SampleDocument()
    {
        var lines = new List<OcrLineBox>
        {
            Line("报告_2026 100%", 10, 10, 200, 40),        // 行高 2 倍 → 一级标题
            Line("正文第一段", 10, 70, 200),
            Line("• 项目一 & 二", 10, 100, 200),
            Line("1. 第一步", 10, 122, 200),
            Line("姓名 年龄", 10, 160, 350, 20,
                new[] { Word("姓名", 10, 160, 60), Word("年龄", 300, 160, 60) }),
            Line("张三 18", 10, 190, 350, 20,
                new[] { Word("张三", 10, 190, 60), Word("18", 300, 190, 60) }),
        };

        TableGrid table = OcrTable.Build(lines);
        return ConversionDocument.Build(lines, table);
    }

    private static void DocumentExportCases()
    {
        DocumentSourceCases();
        DocumentDocxCases();
    }

    private static void DocumentSourceCases()
    {
        ConversionDocument document = SampleDocument();
        Check.Equal(5, document.Blocks.Count, "样例文档：标题 + 段落 + 项目 + 步骤 + 表格");
        Check.True(document.Blocks[4].Kind == ConversionKind.Table, "最后一块是表格");

        // 纯文本：列表带 •，表格按制表符分行
        string plain = document.PlainText;
        Check.True(plain.Contains("报告_2026 100%"), "纯文本含标题");
        Check.True(plain.Contains("• 项目一 & 二"), "纯文本的列表项带记号");
        Check.True(plain.Contains("姓名\t年龄"), "纯文本的表格按制表符分行");
        Check.True(plain.Contains("张三\t18"), "纯文本的表格行");

        // LaTeX：section / itemize / enumerate / tabular + 特殊字符转义
        string latex = DocumentExport.Latex(document);
        Check.True(latex.Contains("\\documentclass[11pt]{article}"), "LaTeX 有文档类");
        Check.True(latex.Contains("\\usepackage{ctex}"), "LaTeX 用 ctex（中文能直接编译）");
        Check.True(latex.Contains("\\section{报告\\_2026 100\\%}"), "标题：section + 下划线 / 百分号转义");
        Check.True(latex.Contains("\\begin{itemize}"), "无序列表环境");
        Check.True(latex.Contains("\\item 项目一 \\& 二"), "列表项 + & 转义");
        Check.True(latex.Contains("\\begin{enumerate}"), "有序列表环境");
        Check.True(latex.Contains("\\begin{tabular}{ll}"), "表格列数 = 2");
        Check.True(latex.Contains("\\toprule"), "表格用 booktabs 的 toprule");
        Check.True(latex.Contains("姓名 & 年龄 \\\\"), "表头一行");
        Check.True(latex.Contains("张三 & 18 \\\\"), "表体一行");
        Check.True(latex.TrimEnd().EndsWith("\\end{document}", StringComparison.Ordinal), "LaTeX 收尾");

        // 独立 HTML（导出 / PDF 用）
        string html = DocumentExport.StandaloneHtml(document, "报告 <2026>");
        Check.True(html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal), "HTML 有 doctype");
        Check.True(html.Contains("<meta charset=\"utf-8\">"), "HTML 声明字符集");
        Check.True(html.Contains("<title>报告 &lt;2026&gt;</title>"), "标题转义");
        Check.True(html.Contains("<h1>报告_2026 100%</h1>"), "标题渲染成 h1");
        Check.True(html.Contains("<ul><li>项目一 &amp; 二</li></ul>"), "列表渲染成 ul");
        Check.True(html.Contains("<table><thead><tr><th>姓名</th><th>年龄</th></tr></thead>"),
            "表格有表头");
        Check.True(html.Contains("</body>"), "HTML 收尾");

        // 扩展名 ↔ 格式
        Check.True(DocumentExport.KindOf("a.PDF") == DocumentExportKind.Pdf, "大写扩展名也认 PDF");
        Check.True(DocumentExport.KindOf("a.md") == DocumentExportKind.Markdown, "md → Markdown");
        Check.True(DocumentExport.KindOf("a.htm") == DocumentExportKind.Html, "htm → HTML");
        Check.True(DocumentExport.KindOf("a.tex") == DocumentExportKind.Latex, "tex → LaTeX");
        Check.True(DocumentExport.KindOf("a.unknown") == DocumentExportKind.Docx, "认不出按 docx");
        Check.Equal("a.pdf", DocumentExport.NormalizePath("a.docx", DocumentExportKind.Pdf),
            "换扩展名");
    }

    private static void DocumentDocxCases()
    {
        ConversionDocument document = SampleDocument();
        byte[]? bytes = DocumentExport.Docx(document);
        Check.True(bytes is { Length: > 0 }, "docx 字节非空");
        if (bytes == null) return;

        using var memory = new MemoryStream(bytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);

        string[] required =
        {
            "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml",
            "word/numbering.xml", "word/_rels/document.xml.rels",
        };

        foreach (string name in required)
            Check.True(archive.GetEntry(name) != null, "docx 有 " + name);

        string Body(string name)
        {
            ZipArchiveEntry? entry = archive.GetEntry(name);
            if (entry == null) return "";
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        string xml = Body("word/document.xml");
        Check.True(xml.Contains("<w:document"), "document.xml 是 WordprocessingML");
        Check.True(xml.Contains("w:val=\"Heading1\""), "标题用 Heading1 样式");
        Check.True(xml.Contains("w:numId w:val=\"1\""), "项目符号走 numId 1");
        Check.True(xml.Contains("w:numId w:val=\"2\""), "有序列表走 numId 2");
        Check.True(xml.Contains("<w:tbl>"), "有表格");
        Check.True(xml.Contains("<w:tblBorders>"), "表格带边框");
        Check.True(xml.Contains("报告_2026 100%"), "正文文字写进文档");
        Check.True(xml.Contains("张三"), "表格文字写进文档");
        Check.True(xml.Contains("<w:sectPr>"), "有节属性（页面尺寸 / 边距）");

        string styles = Body("word/styles.xml");
        Check.True(styles.Contains("Heading1") && styles.Contains("Heading3"), "样式表有三级标题");
        Check.True(styles.Contains("ListParagraph"), "样式表有列表段落");
        Check.True(styles.Contains("Microsoft YaHei"), "默认字体指定中文字体");

        string numbering = Body("word/numbering.xml");
        Check.True(numbering.Contains("w:val=\"bullet\""), "编号定义有无序");
        Check.True(numbering.Contains("w:val=\"decimal\""), "编号定义有有序");

        Check.True(DocumentExport.Docx(ConversionDocument.Empty) == null, "空文档不生成 docx");
    }
}
