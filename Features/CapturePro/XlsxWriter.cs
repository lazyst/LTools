using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把 <see cref="TableDocument"/> 写成最小 OOXML 工作簿（<c>.xlsx</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshotrecognitionfileexport.cpp::tableWorkbook</c> 一比一移植：
/// 六个 part、**inlineStr 单元格**（不建 sharedStrings）、表头走 styles 里的第 2 个字体（加粗）、
/// 合并格写 <c>mergeCells</c>、列宽按像素折成 1/256 字符宽。
/// </para>
/// <para>
/// 用 <see cref="ZipArchive"/> 而不是第三方库：只需要「写几个 xml part」，引一个包不值当。
/// </para>
/// </remarks>
internal static class XlsxWriter
{
    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string RelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private const string OfficeRelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>工作簿字节；文档为空 / 超出 Excel 上限时返回 null。</summary>
    public static byte[]? Build(TableDocument document, IReadOnlyList<int>? columnWidths = null)
    {
        if (document == null || document.IsEmpty) return null;
        if (document.RowCount > 1_048_576 || document.ColumnCount > 16_384) return null;

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            if (!AddXml(archive, "[Content_Types].xml", "Types",
                    "http://schemas.openxmlformats.org/package/2006/content-types", xml =>
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

                        Part(xml, "/xl/workbook.xml",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                        Part(xml, "/xl/worksheets/sheet1.xml",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                        Part(xml, "/xl/styles.xml",
                            "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                    }) ||
                !AddXml(archive, "_rels/.rels", "Relationships", RelationshipNamespace, xml =>
                    Relationship(xml, "rId1", "officeDocument", "xl/workbook.xml")) ||
                !AddXml(archive, "xl/workbook.xml", "workbook", SpreadsheetNamespace, xml =>
                    {
                        xml.WriteStartElement("sheets");
                        xml.WriteStartElement("sheet");
                        xml.WriteAttributeString("name", "表格");
                        xml.WriteAttributeString("sheetId", "1");
                        xml.WriteAttributeString("r", "id", OfficeRelationshipNamespace, "rId1");
                        xml.WriteEndElement();
                        xml.WriteEndElement();
                    }) ||
                !AddXml(archive, "xl/_rels/workbook.xml.rels", "Relationships", RelationshipNamespace,
                    xml =>
                    {
                        Relationship(xml, "rId1", "worksheet", "worksheets/sheet1.xml");
                        Relationship(xml, "rId2", "styles", "styles.xml");
                    }) ||
                !AddXml(archive, "xl/styles.xml", "styleSheet", SpreadsheetNamespace, WriteStyles))
            {
                return null;
            }

            // 行 / 格可以很多：写失败就不要留下半个文件
            bool ok = AddXml(archive, "xl/worksheets/sheet1.xml", "worksheet", SpreadsheetNamespace,
                xml => WriteSheet(xml, document, columnWidths));
            if (!ok) return null;
        }

        return memory.ToArray();
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

    /// <summary>
    /// 字体 0 = 正文、字体 1 = 加粗（表头）；单元格样式一律 <c>numFmtId=49</c>（文本）+ 自动换行 + 顶端对齐，
    /// 这样「00123」这种识别结果不会被表格软件吃掉前导零。
    /// </summary>
    private static void WriteStyles(XmlWriter xml)
    {
        xml.WriteStartElement("fonts");
        xml.WriteAttributeString("count", "2");
        xml.WriteStartElement("font");
        xml.WriteEndElement();
        xml.WriteStartElement("font");
        xml.WriteStartElement("b");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("fills");
        xml.WriteAttributeString("count", "2");
        foreach (string pattern in new[] { "none", "gray125" })
        {
            xml.WriteStartElement("fill");
            xml.WriteStartElement("patternFill");
            xml.WriteAttributeString("patternType", pattern);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();

        xml.WriteStartElement("borders");
        xml.WriteAttributeString("count", "1");
        xml.WriteStartElement("border");
        xml.WriteEndElement();
        xml.WriteEndElement();

        for (int group = 0; group < 2; group++)
        {
            bool cells = group == 1;
            xml.WriteStartElement(cells ? "cellXfs" : "cellStyleXfs");
            xml.WriteAttributeString("count", cells ? "2" : "1");
            for (int index = 0; index < (cells ? 2 : 1); index++)
            {
                xml.WriteStartElement("xf");
                xml.WriteAttributeString("numFmtId", "49");
                xml.WriteAttributeString("fontId", index.ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("fillId", "0");
                xml.WriteAttributeString("borderId", "0");
                if (cells)
                {
                    xml.WriteAttributeString("xfId", "0");
                    xml.WriteAttributeString("applyNumberFormat", "1");
                    xml.WriteAttributeString("applyFont", "1");
                    xml.WriteAttributeString("applyAlignment", "1");
                    xml.WriteStartElement("alignment");
                    xml.WriteAttributeString("wrapText", "1");
                    xml.WriteAttributeString("vertical", "top");
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        xml.WriteStartElement("cellStyles");
        xml.WriteAttributeString("count", "1");
        xml.WriteStartElement("cellStyle");
        xml.WriteAttributeString("name", "Normal");
        xml.WriteAttributeString("xfId", "0");
        xml.WriteAttributeString("builtinId", "0");
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter xml, TableDocument document,
        IReadOnlyList<int>? columnWidths)
    {
        xml.WriteStartElement("dimension");
        xml.WriteAttributeString("ref", "A1:" +
            CellReference(document.RowCount - 1, document.ColumnCount - 1));
        xml.WriteEndElement();

        if (columnWidths is { Count: > 0 })
        {
            xml.WriteStartElement("cols");
            for (int column = 0; column < document.ColumnCount && column < columnWidths.Count; column++)
            {
                int pixels = columnWidths[column];
                if (pixels <= 0) continue;

                // OOXML 的列宽单位是「字符宽 × 1/256」；按 7 px 一个数字宽折算，上限 255。
                double width = Math.Min(255.0, Math.Floor(pixels * 256.0 / 7.0) / 256.0);
                xml.WriteStartElement("col");
                xml.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("width", width.ToString("0.00000000", CultureInfo.InvariantCulture));
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData");
        var merges = new List<string>();

        for (int row = 0; row < document.RowCount; row++)
        {
            xml.WriteStartElement("row");
            xml.WriteAttributeString("r", (row + 1).ToString(CultureInfo.InvariantCulture));

            for (int column = 0; column < document.ColumnCount; column++)
            {
                TableCell? cell = document.AnchorCellAt(row, column);
                if (cell == null) continue;

                string reference = CellReference(row, column);
                xml.WriteStartElement("c");
                xml.WriteAttributeString("r", reference);
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteAttributeString("s", cell.Header ? "1" : "0");
                xml.WriteStartElement("is");
                xml.WriteStartElement("t");
                xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace",
                    "preserve");
                xml.WriteString(SpreadsheetText(cell.Text));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();

                if (cell.RowSpan > 1 || cell.ColumnSpan > 1)
                {
                    merges.Add(reference + ":" + CellReference(
                        Math.Min(document.RowCount - 1, row + cell.RowSpan - 1),
                        Math.Min(document.ColumnCount - 1, column + cell.ColumnSpan - 1)));
                }
            }

            xml.WriteEndElement();
        }

        xml.WriteEndElement();

        if (merges.Count > 0)
        {
            xml.WriteStartElement("mergeCells");
            xml.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string range in merges)
            {
                xml.WriteStartElement("mergeCell");
                xml.WriteAttributeString("ref", range);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }
    }

    /// <summary>0 → A、25 → Z、26 → AA（与上游 <c>cellReference</c> 一致）。</summary>
    internal static string CellReference(int row, int column)
    {
        var sb = new StringBuilder();
        for (int value = column + 1; value > 0; value = (value - 1) / 26)
            sb.Insert(0, (char)('A' + (value - 1) % 26));
        return sb.Append((row + 1).ToString(CultureInfo.InvariantCulture)).ToString();
    }

    /// <summary>
    /// SpreadsheetML 自己有一套转义：控制字符与「看起来像 `_xHHHH_` 的字面量」都要转义，
    /// 否则 Excel 打开时会把它们当编码吃掉。
    /// </summary>
    internal static string SpreadsheetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);

        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            bool escapePrefix = ch == '_' && index + 6 < text.Length && text[index + 1] == 'x' &&
                                text[index + 6] == '_' && IsHex(text, index + 2);
            if (escapePrefix || (ch < 0x20 && ch != '\n' && ch != '\t') || ch == '\uFFFE' || ch == '\uFFFF')
            {
                sb.Append("_x").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool IsHex(string text, int start)
    {
        for (int index = start; index < start + 4 && index < text.Length; index++)
        {
            char ch = text[index];
            bool hex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }

        return start + 4 <= text.Length;
    }

    private static bool AddXml(ZipArchive archive, string name, string root, string nameSpace,
        Action<XmlWriter> fill)
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
                xml.WriteStartDocument();
                xml.WriteStartElement(root, nameSpace);
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
}
