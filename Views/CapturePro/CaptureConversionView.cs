using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LTools.Features.CapturePro;
using TableCell = System.Windows.Documents.TableCell;

namespace LTools.Views.CapturePro;

/// <summary>
/// Markdown / HTML 页的**渲染预览**：拿 <see cref="ConversionDocument"/> 的块列表画成流文档
/// （标题 / 段落 / 列表 / 表格），可以划选、Ctrl+C 复制选中那段。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>ScreenshotImageConversionView</c>：上游是 <c>QTextBrowser</c> +
/// <c>Qt::TextBrowserInteraction</c>，渲染出来的内容能选、能复制，复制时**选了就复制选中**、
/// 没选就复制整份源码（见 <c>screenshotimageconversionclipboard.h</c>）。
/// </para>
/// <para>
/// 这里不解析 HTML / Markdown 源码——预览与源码出自同一份块列表（<see cref="ConversionDocument.Blocks"/>），
/// 省掉「自己写的渲染器再被自己写的解析器解释」这一层，两份永远一致。
/// </para>
/// </remarks>
internal sealed class CaptureConversionView : FlowDocumentScrollViewer
{
    /// <summary>正文行高（与文字层同一套读感）。</summary>
    private const double LineHeight = 1.55;

    private ConversionDocument _document = ConversionDocument.Empty;

    public CaptureConversionView()
    {
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(12, 10, 12, 10);
        Background = Brushes.Transparent;
        Focusable = true;
        Document = Build(ConversionDocument.Empty, "这张截图没有识别到文字。");
    }

    /// <summary>当前渲染的是哪一份（状态行 / 复制用）。</summary>
    internal ConversionDocument Content => _document;

    /// <summary>选中的纯文本（没选就是空串）。</summary>
    internal string SelectedText
    {
        get
        {
            try
            {
                return Selection is { IsEmpty: false } ? Selection.Text.TrimEnd('\r', '\n') : "";
            }
            catch (InvalidOperationException)
            {
                return "";
            }
        }
    }

    /// <summary>换一份内容（<paramref name="emptyHint"/> 只影响空态文案：几页共用同一份块列表）。</summary>
    internal void Render(ConversionDocument document, string emptyHint)
    {
        _document = document;
        Document = Build(document, emptyHint);
    }

    /// <summary>全选（Ctrl+A 走这里；<see cref="FlowDocumentScrollViewer"/> 自己不带）。</summary>
    internal void SelectAllText()
    {
        if (Document == null) return;
        Selection.Select(Document.ContentStart, Document.ContentEnd);
    }

    // ============ 块 → FlowDocument ============

    private static FlowDocument Build(ConversionDocument document, string emptyHint)
    {
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            LineHeight = 13 * LineHeight,
            TextAlignment = TextAlignment.Left,
        };

        List? openList = null;
        string? openListKind = null;

        foreach (ConversionBlock block in document.Blocks)
        {
            string kind = block.Kind switch
            {
                ConversionKind.Bullet => "ul",
                ConversionKind.Ordered => "ol",
                _ => "",
            };

            if (openList != null && openListKind != kind)
            {
                flow.Blocks.Add(openList);
                openList = null;
                openListKind = null;
            }

            switch (block.Kind)
            {
                case ConversionKind.Heading:
                    flow.Blocks.Add(Heading(block.Text, block.Level));
                    break;

                case ConversionKind.Paragraph:
                    flow.Blocks.Add(Paragraph(block.Text, new Thickness(0, 4, 0, 4)));
                    break;

                case ConversionKind.Bullet:
                case ConversionKind.Ordered:
                    if (openList == null)
                    {
                        openList = new List
                        {
                            MarkerStyle = block.Kind == ConversionKind.Bullet
                                ? TextMarkerStyle.Disc
                                : TextMarkerStyle.Decimal,
                            Margin = new Thickness(20, 4, 0, 4),
                            Padding = new Thickness(0),
                        };
                        openListKind = kind;
                    }

                    openList.ListItems.Add(new ListItem(Paragraph(block.Text, new Thickness(0))));
                    break;

                case ConversionKind.Table:
                    flow.Blocks.Add(Table(block.Rows));
                    break;
            }
        }

        if (openList != null) flow.Blocks.Add(openList);
        if (flow.Blocks.Count == 0)
        {
            flow.Blocks.Add(Paragraph(string.IsNullOrEmpty(emptyHint)
                ? "这张截图没有识别到文字。"
                : emptyHint,
                new Thickness(0, 8, 0, 0),
                dim: true));
        }

        return flow;
    }

    private static Paragraph Heading(string text, int level)
    {
        double size = level switch { 1 => 20, 2 => 17, _ => 15 };
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level == 1 ? 12 : 10, 0, 6),
            LineHeight = size * 1.35,
        };
        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    private static Paragraph Paragraph(string text, Thickness margin, bool dim = false)
    {
        var paragraph = new Paragraph { Margin = margin };
        if (dim) paragraph.Foreground = Dim();
        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    private static System.Windows.Documents.Table Table(IReadOnlyList<string[]> rows)
    {
        var table = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = Border(),
            BorderThickness = new Thickness(1),
        };

        int columns = 0;
        foreach (string[] row in rows) columns = Math.Max(columns, row.Length);
        if (columns == 0) return table;

        for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (int r = 0; r < rows.Count; r++)
        {
            var tr = new TableRow();
            bool header = r == 0;
            if (header) tr.Background = HeaderFill();

            for (int c = 0; c < columns; c++)
            {
                string text = c < rows[r].Length ? rows[r][c] : "";
                var cell = new TableCell(new Paragraph(new Run(text))
                {
                    Margin = new Thickness(0),
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    LineHeight = 13 * LineHeight,
                })
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderBrush = Border(),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                };
                tr.Cells.Add(cell);
            }

            group.Rows.Add(tr);
        }

        return table;
    }

    private static Brush Border() => Res("BorderBrush", Color.FromArgb(0x33, 0x00, 0x00, 0x00));

    private static Brush HeaderFill() => Res("SurfaceAltBrush", Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

    private static Brush Dim() => Res("SecondaryTextBrush", Color.FromArgb(0x99, 0x00, 0x00, 0x00));

    private static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch (ResourceReferenceKeyNotFoundException)
        {
            // 换主题的过程中键短暂不存在：用兜底色
        }

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
