using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace LTools.Features.CapturePro;

/// <summary>文档导出的格式（照上游 <c>ScreenshotRecognitionFileKind</c> 的 Document 一族）。</summary>
internal enum DocumentExportKind
{
    Docx,
    Pdf,
    Markdown,
    Html,
    Latex,
}

/// <summary>
/// 把 <see cref="ConversionDocument"/> 导出成文档文件：Word / PDF / Markdown / HTML / LaTeX。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshotdocumentexport.cpp</c>：上游的识别结果是云端给的富文本模型，
/// 导出走 <c>QTextDocument</c> 的 docx / PDF 写手；本项目没有云端模型，导出的是
/// 「按版面派生的块」（见 <see cref="ConversionDocument"/>），形状一致、内容以 OCR 为准。
/// </para>
/// <para>
/// **PDF 的平台桥**：.NET 没有内置 PDF 写手，这里调用系统自带的 Microsoft Edge / Chrome
/// 的 headless 打印（<c>--print-to-pdf</c>）把同一份 HTML 转成 PDF；找不到浏览器时明确报错，
/// 不假装导出成功（见 <c>docs/SCREEN_CAPTURE_PRO.md</c> §5 的平台差异表）。
/// </para>
/// </remarks>
internal static class DocumentExport
{
    private const string WordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    private const string RelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>保存对话框的筛选串（顺序与 <see cref="DocumentExportKind"/> 一致）。</summary>
    public const string Filter =
        "Word 文档 (*.docx)|*.docx|PDF 文档 (*.pdf)|*.pdf|Markdown (*.md)|*.md|" +
        "HTML 页面 (*.html)|*.html|LaTeX (*.tex)|*.tex";

    /// <summary>按扩展名定格式（用户在下拉里改了名字也认）。</summary>
    public static DocumentExportKind KindOf(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => DocumentExportKind.Pdf,
            ".md" or ".markdown" => DocumentExportKind.Markdown,
            ".html" or ".htm" => DocumentExportKind.Html,
            ".tex" => DocumentExportKind.Latex,
            _ => DocumentExportKind.Docx,
        };
    }

    /// <summary>换扩展名（保存对话框选中的类型与文件名对不上时）。</summary>
    public static string NormalizePath(string path, DocumentExportKind kind)
    {
        string extension = kind switch
        {
            DocumentExportKind.Pdf => ".pdf",
            DocumentExportKind.Markdown => ".md",
            DocumentExportKind.Html => ".html",
            DocumentExportKind.Latex => ".tex",
            _ => ".docx",
        };

        string current = Path.GetExtension(path);
        if (string.Equals(current, extension, StringComparison.OrdinalIgnoreCase)) return path;
        return Path.ChangeExtension(path, extension);
    }

    /// <summary>写文件；失败把原因带回来（界面直接显示，不再弹二次框）。</summary>
    public static bool Write(DocumentExportKind kind, ConversionDocument document, string path,
        out string error)
    {
        error = "";
        if (document == null || document.IsEmpty)
        {
            error = "没有可导出的文档内容";
            return false;
        }

        try
        {
            switch (kind)
            {
                case DocumentExportKind.Markdown:
                    File.WriteAllText(path, document.Markdown, new UTF8Encoding(false));
                    return true;

                case DocumentExportKind.Html:
                    File.WriteAllText(path, StandaloneHtml(document, Path.GetFileNameWithoutExtension(path)),
                        new UTF8Encoding(false));
                    return true;

                case DocumentExportKind.Latex:
                    File.WriteAllText(path, Latex(document), new UTF8Encoding(false));
                    return true;

                case DocumentExportKind.Pdf:
                    return WritePdf(document, path, out error);

                default:
                    byte[]? bytes = Docx(document);
                    if (bytes == null)
                    {
                        error = "文档内容为空，无法生成 docx";
                        return false;
                    }

                    File.WriteAllBytes(path, bytes);
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ============ Markdown / HTML ============

    /// <summary>独立 HTML 页（导出 / PDF 用）：带样式、字符集与标题。</summary>
    public static string StandaloneHtml(ConversionDocument document, string title)
    {
        string heading = string.IsNullOrWhiteSpace(title) ? "识别文档" : title.Trim();
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\r\n<html lang=\"zh-CN\">\r\n<head>\r\n");
        sb.Append("<meta charset=\"utf-8\">\r\n<meta name=\"viewport\" content=\"width=device-width\">\r\n");
        sb.Append("<title>").Append(Escape(heading)).Append("</title>\r\n<style>\r\n");
        sb.Append("body{font-family:\"Microsoft YaHei UI\",\"Segoe UI\",sans-serif;font-size:14px;");
        sb.Append("line-height:1.75;color:#1f2328;margin:36px auto;max-width:900px;padding:0 24px}\r\n");
        sb.Append("h1{font-size:26px;margin:20px 0 12px}h2{font-size:21px;margin:18px 0 10px}");
        sb.Append("h3{font-size:17px;margin:16px 0 8px}p{margin:8px 0}\r\n");
        sb.Append("ul,ol{margin:8px 0;padding-left:26px}table{border-collapse:collapse;width:100%;");
        sb.Append("margin:12px 0}th,td{border:1px solid #d0d7de;padding:6px 10px;text-align:left;");
        sb.Append("vertical-align:top}th{background:#f3f4f6;font-weight:600}\r\n");
        sb.Append(".meta{margin-top:28px;font-size:12px;color:#8b949e}\r\n</style>\r\n</head>\r\n<body>\r\n");
        sb.Append(document.Html);
        sb.Append("\r\n<p class=\"meta\">由 LTools 截图 Pro 识别整理</p>\r\n</body>\r\n</html>\r\n");
        return sb.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ============ LaTeX ============

    /// <summary>LaTeX 源码（article + ctex，中文能直接编译）。</summary>
    public static string Latex(ConversionDocument document)
    {
        var sb = new StringBuilder();
        sb.Append("% Generated by LTools 截图 Pro\r\n");
        sb.Append("\\documentclass[11pt]{article}\r\n");
        sb.Append("\\usepackage[T1]{fontenc}\r\n");
        sb.Append("\\usepackage[utf8]{inputenc}\r\n");
        sb.Append("\\usepackage{ctex}\r\n");
        sb.Append("\\usepackage{booktabs}\r\n");
        sb.Append("\\begin{document}\r\n\r\n");

        for (int i = 0; i < document.Blocks.Count; i++)
        {
            ConversionBlock block = document.Blocks[i];
            switch (block.Kind)
            {
                case ConversionKind.Heading:
                    sb.Append(block.Level switch
                    {
                        1 => "\\section{",
                        2 => "\\subsection{",
                        _ => "\\subsubsection{",
                    }).Append(LatexText(block.Text)).Append("}\r\n\r\n");
                    break;

                case ConversionKind.Paragraph:
                    sb.Append(LatexText(block.Text)).Append("\r\n\r\n");
                    break;

                case ConversionKind.Bullet:
                case ConversionKind.Ordered:
                    if (i == 0 || document.Blocks[i - 1].Kind != block.Kind)
                        sb.Append(block.Kind == ConversionKind.Bullet
                            ? "\\begin{itemize}\r\n"
                            : "\\begin{enumerate}\r\n");
                    sb.Append("  \\item ").Append(LatexText(block.Text)).Append("\r\n");
                    if (i + 1 >= document.Blocks.Count || document.Blocks[i + 1].Kind != block.Kind)
                        sb.Append(block.Kind == ConversionKind.Bullet
                            ? "\\end{itemize}\r\n\r\n"
                            : "\\end{enumerate}\r\n\r\n");
                    break;

                case ConversionKind.Table:
                    AppendLatexTable(sb, block.Rows);
                    break;
            }
        }

        sb.Append("\\end{document}\r\n");
        return sb.ToString();
    }

    private static void AppendLatexTable(StringBuilder sb, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return;

        int columns = 0;
        foreach (string[] row in rows) columns = Math.Max(columns, row.Length);
        if (columns == 0) return;

        sb.Append("\\begin{tabular}{").Append(new string('l', columns)).Append("}\r\n");
        sb.Append("\\toprule\r\n");
        for (int r = 0; r < rows.Count; r++)
        {
            string[] row = rows[r];
            for (int c = 0; c < columns; c++)
            {
                if (c > 0) sb.Append(" & ");
                sb.Append(LatexText(c < row.Length ? row[c] : ""));
            }

            sb.Append(" \\\\\r\n");
            if (r == 0) sb.Append("\\midrule\r\n");
        }

        sb.Append("\\bottomrule\r\n\\end{tabular}\r\n\r\n");
    }

    /// <summary>LaTeX 特殊字符转义（反斜杠先换，别把后面加的反斜杠再转一遍）。</summary>
    private static string LatexText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\textbackslash{}"); break;
                case '{': sb.Append("\\{"); break;
                case '}': sb.Append("\\}"); break;
                case '$': sb.Append("\\$"); break;
                case '&': sb.Append("\\&"); break;
                case '#': sb.Append("\\#"); break;
                case '%': sb.Append("\\%"); break;
                case '_': sb.Append("\\_"); break;
                case '^': sb.Append("\\textasciicircum{}"); break;
                case '~': sb.Append("\\textasciitilde{}"); break;
                case '\r': break;
                case '\n': sb.Append(" "); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    // ============ Word（docx） ============

    /// <summary>最小可用的 WordprocessingML 文档；空文档返回 null。</summary>
    public static byte[]? Docx(ConversionDocument document)
    {
        if (document == null || document.IsEmpty) return null;

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            if (!AddXml(archive, "[Content_Types].xml", ContentTypesNamespace, null, "Types", xml =>
                {
                    xml.WriteStartElement("Default");
                    xml.WriteAttributeString("Extension", "rels");
                    xml.WriteAttributeString("ContentType",
                        "application/vnd.openxmlformats-package.relationships+xml");
                    xml.WriteEndElement();

                    xml.WriteStartElement("Default");
                    xml.WriteAttributeString("Extension", "xml");
                    xml.WriteAttributeString("ContentType", "application/xml");
                    xml.WriteEndElement();

                    Part(xml, "/word/document.xml",
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
                    Part(xml, "/word/styles.xml",
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
                    Part(xml, "/word/numbering.xml",
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml");
                }) ||
                !AddXml(archive, "_rels/.rels", RelationshipNamespace, null, "Relationships", xml =>
                    Relationship(xml, "rId1", "officeDocument", "word/document.xml")) ||
                !AddXml(archive, "word/document.xml", WordNamespace, "w", "document",
                    xml => WriteBody(xml, document)) ||
                !AddXml(archive, "word/_rels/document.xml.rels", RelationshipNamespace, null,
                    "Relationships", xml =>
                    {
                        Relationship(xml, "rId1", "styles", "styles.xml");
                        Relationship(xml, "rId2", "numbering", "numbering.xml");
                    }) ||
                !AddXml(archive, "word/styles.xml", WordNamespace, "w", "styles", WriteStyles) ||
                !AddXml(archive, "word/numbering.xml", WordNamespace, "w", "numbering", WriteNumbering))
            {
                return null;
            }
        }

        return memory.ToArray();
    }

    private static void WriteBody(XmlWriter xml, ConversionDocument document)
    {
        xml.WriteStartElement("w", "body", WordNamespace);

        for (int i = 0; i < document.Blocks.Count; i++)
        {
            ConversionBlock block = document.Blocks[i];
            switch (block.Kind)
            {
                case ConversionKind.Heading:
                    Paragraph(xml, block.Text, "Heading" + Math.Clamp(block.Level, 1, 3), null, false);
                    break;

                case ConversionKind.Paragraph:
                    Paragraph(xml, block.Text, null, null, false);
                    break;

                case ConversionKind.Bullet:
                    Paragraph(xml, block.Text, "ListParagraph", 1, false);
                    break;

                case ConversionKind.Ordered:
                    Paragraph(xml, block.Text, "ListParagraph", 2, false);
                    break;

                case ConversionKind.Table:
                    Table(xml, block.Rows);
                    // 表格后面补一个空段：Word 里两张表紧贴会连成一张
                    Paragraph(xml, "", null, null, false);
                    break;
            }
        }

        xml.WriteStartElement("w", "sectPr", WordNamespace);
        Element(xml, "pgSz", ("w", "11906"), ("h", "16838"));
        Element(xml, "pgMar", ("top", "1440"), ("right", "1440"), ("bottom", "1440"),
            ("left", "1440"), ("header", "708"), ("footer", "708"), ("gutter", "0"));
        xml.WriteEndElement();

        xml.WriteEndElement();
    }

    private static void Paragraph(XmlWriter xml, string text, string? style, int? numbering, bool bold)
    {
        xml.WriteStartElement("w", "p", WordNamespace);

        if (style != null || numbering != null)
        {
            xml.WriteStartElement("w", "pPr", WordNamespace);
            if (style != null) Element(xml, "pStyle", ("val", style));
            if (numbering != null)
            {
                xml.WriteStartElement("w", "numPr", WordNamespace);
                Element(xml, "ilvl", ("val", "0"));
                Element(xml, "numId", ("val", numbering.Value.ToString(CultureInfo.InvariantCulture)));
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        if (text.Length > 0)
        {
            xml.WriteStartElement("w", "r", WordNamespace);
            if (bold)
            {
                xml.WriteStartElement("w", "rPr", WordNamespace);
                xml.WriteStartElement("w", "b", WordNamespace);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }

            xml.WriteStartElement("w", "t", WordNamespace);
            xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(text);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static void Table(XmlWriter xml, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return;

        int columns = 0;
        foreach (string[] row in rows) columns = Math.Max(columns, row.Length);
        if (columns == 0) return;

        xml.WriteStartElement("w", "tbl", WordNamespace);

        xml.WriteStartElement("w", "tblPr", WordNamespace);
        Element(xml, "tblStyle", ("val", "TableGrid"));
        Element(xml, "tblW", ("w", "0"), ("type", "auto"));
        xml.WriteStartElement("w", "tblBorders", WordNamespace);
        foreach (string side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            Element(xml, side, ("val", "single"), ("sz", "4"), ("space", "0"), ("color", "auto"));
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("w", "tblGrid", WordNamespace);
        for (int c = 0; c < columns; c++) Element(xml, "gridCol", ("w", "2400"));
        xml.WriteEndElement();

        for (int r = 0; r < rows.Count; r++)
        {
            xml.WriteStartElement("w", "tr", WordNamespace);
            for (int c = 0; c < columns; c++)
            {
                xml.WriteStartElement("w", "tc", WordNamespace);
                xml.WriteStartElement("w", "tcPr", WordNamespace);
                Element(xml, "tcW", ("w", "0"), ("type", "auto"));
                xml.WriteEndElement();
                Paragraph(xml, c < rows[r].Length ? rows[r][c] : "", null, null, r == 0);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter xml)
    {
        xml.WriteStartElement("w", "docDefaults", WordNamespace);
        xml.WriteStartElement("w", "rPrDefault", WordNamespace);
        xml.WriteStartElement("w", "rPr", WordNamespace);
        Element(xml, "rFonts", ("eastAsia", "Microsoft YaHei"), ("ascii", "Calibri"), ("hAnsi", "Calibri"));
        Element(xml, "sz", ("val", "22"));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("w", "pPrDefault", WordNamespace);
        xml.WriteEndElement();
        xml.WriteEndElement();

        Style(xml, "Normal", "Normal", null, null, null);
        Style(xml, "Heading1", "heading 1", "Normal", "0", "36");
        Style(xml, "Heading2", "heading 2", "Normal", "1", "32");
        Style(xml, "Heading3", "heading 3", "Normal", "2", "28");
        Style(xml, "ListParagraph", "List Paragraph", "Normal", null, null);

        xml.WriteStartElement("w", "style", WordNamespace);
        xml.WriteAttributeString("w", "type", WordNamespace, "table");
        xml.WriteAttributeString("w", "styleId", WordNamespace, "TableGrid");
        Element(xml, "name", ("val", "Table Grid"));
        xml.WriteEndElement();
    }

    private static void Style(XmlWriter xml, string id, string name, string? basedOn, string? outline,
        string? size)
    {
        xml.WriteStartElement("w", "style", WordNamespace);
        xml.WriteAttributeString("w", "type", WordNamespace, "paragraph");
        xml.WriteAttributeString("w", "styleId", WordNamespace, id);
        Element(xml, "name", ("val", name));
        if (basedOn != null) Element(xml, "basedOn", ("val", basedOn));

        xml.WriteStartElement("w", "pPr", WordNamespace);
        if (id.StartsWith("Heading", StringComparison.Ordinal))
            Element(xml, "spacing", ("before", "240"), ("after", "120"));
        if (outline != null) Element(xml, "outlineLvl", ("val", outline));
        xml.WriteEndElement();

        if (size != null)
        {
            xml.WriteStartElement("w", "rPr", WordNamespace);
            xml.WriteStartElement("w", "b", WordNamespace);
            xml.WriteEndElement();
            Element(xml, "sz", ("val", size));
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static void WriteNumbering(XmlWriter xml)
    {
        AbstractNumbering(xml, "0", "bullet", "•", "Symbol");
        AbstractNumbering(xml, "1", "decimal", "%1.", null);

        foreach ((string numId, string abstractId) in new[] { ("1", "0"), ("2", "1") })
        {
            xml.WriteStartElement("w", "num", WordNamespace);
            xml.WriteAttributeString("w", "numId", WordNamespace, numId);
            Element(xml, "abstractNumId", ("val", abstractId));
            xml.WriteEndElement();
        }
    }

    private static void AbstractNumbering(XmlWriter xml, string id, string format, string text,
        string? font)
    {
        xml.WriteStartElement("w", "abstractNum", WordNamespace);
        xml.WriteAttributeString("w", "abstractNumId", WordNamespace, id);
        Element(xml, "multiLevelType", ("val", "singleLevel"));

        xml.WriteStartElement("w", "lvl", WordNamespace);
        xml.WriteAttributeString("w", "ilvl", WordNamespace, "0");
        Element(xml, "start", ("val", "1"));
        Element(xml, "numFmt", ("val", format));
        Element(xml, "lvlText", ("val", text));
        Element(xml, "lvlJc", ("val", "left"));

        xml.WriteStartElement("w", "pPr", WordNamespace);
        Element(xml, "ind", ("left", "720"), ("hanging", "360"));
        xml.WriteEndElement();

        if (font != null)
        {
            xml.WriteStartElement("w", "rPr", WordNamespace);
            Element(xml, "rFonts", ("eastAsia", font), ("ascii", font), ("hAnsi", font));
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>带前缀的 <c>&lt;w:xxx w:key="value"/&gt;</c>。</summary>
    private static void Element(XmlWriter xml, string name, params (string Key, string Value)[] attributes)
    {
        xml.WriteStartElement("w", name, WordNamespace);
        foreach ((string key, string value) in attributes)
            xml.WriteAttributeString("w", key, WordNamespace, value);
        xml.WriteEndElement();
    }

    private static void Part(XmlWriter xml, string name, string contentType)
    {
        xml.WriteStartElement("Override");
        xml.WriteAttributeString("PartName", name);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void Relationship(XmlWriter xml, string id, string type, string target)
    {
        xml.WriteStartElement("Relationship");
        xml.WriteAttributeString("Id", id);
        xml.WriteAttributeString("Type",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type);
        xml.WriteAttributeString("Target", target);
        xml.WriteEndElement();
    }

    private static bool AddXml(ZipArchive archive, string name, string nameSpace, string? prefix,
        string localName, Action<XmlWriter> fill)
    {
        try
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                CloseOutput = false,
            };

            using (XmlWriter xml = XmlWriter.Create(stream, settings))
            {
                xml.WriteStartDocument(true);
                xml.WriteStartElement(prefix, localName, nameSpace);
                fill(xml);
                xml.WriteEndElement();
                xml.WriteEndDocument();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ============ PDF（系统浏览器 headless 打印） ============

    private const string PrintFolder = "ltools-capture-doc";

    /// <summary>找到可用的 Chromium 内核浏览器；找不到返回 null。</summary>
    public static string? PdfEngine()
    {
        var candidates = new List<string>();
        foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" })
        {
            string root = Environment.GetEnvironmentVariable(variable) ?? "";
            if (root.Length == 0) continue;
            candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        }

        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static bool WritePdf(ConversionDocument document, string path, out string error)
    {
        error = "";
        string? engine = PdfEngine();
        if (engine == null)
        {
            error = "没有找到 Microsoft Edge 或 Chrome，无法导出 PDF（可改导 Word / HTML）";
            return false;
        }

        string folder = Path.Combine(Path.GetTempPath(), PrintFolder);
        string htmlPath = Path.Combine(folder, "doc-" + Guid.NewGuid().ToString("N") + ".html");
        string profile = Path.Combine(folder, "profile-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(profile);
            File.WriteAllText(htmlPath, StandaloneHtml(document, Path.GetFileNameWithoutExtension(path)),
                new UTF8Encoding(false));

            var start = new ProcessStartInfo(engine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            start.ArgumentList.Add("--headless=new");
            start.ArgumentList.Add("--disable-gpu");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--no-pdf-header-footer");
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add("--print-to-pdf=" + path);
            start.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri);

            using Process? process = Process.Start(start);
            if (process == null)
            {
                error = "无法启动 PDF 引擎";
                return false;
            }

            // 两个管道都要排空，否则输出填满缓冲会把子进程卡住
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                    // 进程已经没了
                }

                error = "PDF 引擎超时（60 秒）";
                return false;
            }

            _ = output.Result;
            _ = errors.Result;

            // 浏览器可能把自己重开成子进程（父进程立刻退出），PDF 要稍后才落盘：等一小会儿
            for (int waited = 0; waited < 200 && !File.Exists(path); waited++)
                System.Threading.Thread.Sleep(100);

            if (!File.Exists(path) || new FileInfo(path).Length < 5)
            {
                error = "PDF 引擎没有生成文件（退出码 " + process.ExitCode + "）";
                return false;
            }

            using (FileStream stream = File.OpenRead(path))
            {
                var header = new byte[4];
                if (stream.Read(header, 0, 4) < 4 || Encoding.ASCII.GetString(header) != "%PDF")
                {
                    error = "PDF 引擎生成的文件头不对";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            TryDelete(htmlPath);
            TryDeleteFolder(profile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响导出结果
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
            // 浏览器可能还占着 profile：留给系统清理
        }
    }
}
