using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LTools.Views;

namespace LTools.Core.SelfTest;

/// <summary>
/// 速记预览的 Markdown → FlowDocument 映射（<see cref="MarkdownRender"/>）。
/// </summary>
/// <remarks>
/// 渲染器是纯函数式的（给源码 + 主题，吐一份文档），所以自测直接断言文档结构：
/// 标题字号 / 粗体 / 行内代码 / 链接 / 任务列表 / 围栏代码块 / 引用 / 表格 / 分隔线，
/// 每块还要带源码行号（点预览回源码靠它）。图片分「找得到 → Image」「找不到 → 占位文字」两条。
/// </remarks>
internal static class MarkdownSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("markdown", "速记预览：块与行内结构", Blocks));
        cases.Add(new("markdown", "速记预览：图片与空文档", ImagesAndEmpty));
    }

    private const string Sample = """"
        # 标题

        正文 **粗体** *斜体* `code` [链接](https://example.com)

        - 项一
        - [ ] 待办

        ```csharp
        var x = 1;
        ```

        > 引用一句

        | 名字 | 数量 |
        | --- | --- |
        | 苹果 | 3 |

        ---
        """";

    private static void Blocks()
    {
        FlowDocument document = Build(Sample);
        Check.True(document.Blocks.Count >= 6, "样例至少 6 块（实际 " + document.Blocks.Count + "）");

        var heading = document.Blocks.FirstBlock as Paragraph;
        Check.True(heading != null, "第一块是标题段落");
        Check.True(heading!.FontSize > 13, "标题字号比正文大（" + heading.FontSize + "）");
        Check.Equal(FontWeights.SemiBold, heading.FontWeight, "标题是 SemiBold");
        Check.Equal(1, heading.Tag, "第一块带源码行号 1");

        var paragraph = heading.NextBlock as Paragraph;
        Check.True(paragraph != null, "第二块是正文段落");
        var bold = FindInline<Span>(paragraph!.Inlines, s => s.FontWeight == FontWeights.Bold);
        Check.True(bold != null, "**粗体** 变成 Bold 的 Span");
        var italic = FindInline<Span>(paragraph.Inlines, s => s.FontStyle == FontStyles.Italic);
        Check.True(italic != null, "*斜体* 变成 Italic 的 Span");
        var code = FindInline<Run>(paragraph.Inlines, r => r.FontFamily.Source.Contains("Cascadia", StringComparison.Ordinal));
        Check.True(code != null, "行内代码用等宽字体");
        Check.Equal("#FFB45309", (code!.Foreground as SolidColorBrush)?.Color.ToString(), "行内代码用主题的 warning 色");
        var link = FindInline<Hyperlink>(paragraph.Inlines, _ => true);
        Check.Equal("https://example.com/", link?.NavigateUri?.AbsoluteUri, "链接带 NavigateUri");

        var list = paragraph.NextBlock as List;
        Check.True(list != null, "段落之后是无序列表");
        Check.Equal(2, list!.ListItems.Count, "列表两项");
        Check.True(ParagraphText(((Paragraph)list.ListItems.Cast<ListItem>().ElementAt(1).Blocks.FirstBlock!)).Contains("☐"),
            "任务列表项画成 ☐");

        var fence = list.NextBlock as Paragraph;
        Check.True(fence != null, "列表之后是围栏代码块");
        Check.True(fence!.FontFamily.Source.Contains("Cascadia", StringComparison.Ordinal), "代码块用等宽字体");
        Check.True(ParagraphText(fence).Contains("var x = 1;"), "代码块保留源码：" + ParagraphText(fence));
        Check.Equal("#FFF4F4F5", (fence.Background as SolidColorBrush)?.Color.ToString(), "代码块用主题的 SurfaceAlt 底");
        Check.True(fence.Tag is int fenceLine && fenceLine > 1, "代码块带自己的源码行号（" + fence.Tag + "）");

        var quote = fence.NextBlock as Section;
        Check.True(quote != null, "代码块之后是引用");
        Check.Equal(3d, quote!.BorderThickness.Left, "引用左侧 3px 竖线");
        Check.True(ParagraphText((Paragraph)quote.Blocks.FirstBlock!).Contains("引用一句"), "引用正文");

        var table = quote.NextBlock as Table;
        Check.True(table != null, "引用之后是表格");
        Check.Equal(1, table!.RowGroups.Count, "表格一个行组");
        TableRowGroup group = table.RowGroups[0];
        Check.Equal(2, group.Rows.Count, "表格两行（表头 + 数据）");
        Check.Equal(2, group.Rows[0].Cells.Count, "两列");
        Check.Equal(FontWeights.SemiBold, group.Rows[0].Cells[0].FontWeight, "表头加粗");
        Check.Equal("#FFF4F4F5", (group.Rows[0].Cells[0].Background as SolidColorBrush)?.Color.ToString(),
            "表头有浅灰底");
        Check.True(ParagraphText((Paragraph)group.Rows[1].Cells[0].Blocks.FirstBlock!).Contains("苹果"), "数据格内容");

        Check.True(table.NextBlock is BlockUIContainer, "最后是分隔线（BlockUIContainer）");
    }

    private static void ImagesAndEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ltools-selftest", "md-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 1×1 的 png：够验证「找得到就变成 Image」
            File.WriteAllBytes(Path.Combine(dir, "dot.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            FlowDocument found = MarkdownRender.Build("![点](dot.png)", dir, Theme());
            var paragraph = found.Blocks.FirstBlock as Paragraph;
            var container = FindInline<InlineUIContainer>(paragraph!.Inlines, _ => true);
            Check.True(container?.Child is Image, "本地图片渲染成 Image");
            Check.Equal(1.0, ((Image)container!.Child).Width, "1px 的图按原宽显示");

            // 笔记名带空格：插入时写成 <…>，Markdig 才会把整段当路径；手写的 %20 也要认
            string spaced = "有 空格.png";
            File.Copy(Path.Combine(dir, "dot.png"), Path.Combine(dir, spaced));
            FlowDocument pointy = MarkdownRender.Build("![空格](<" + spaced + ">)", dir, Theme());
            Check.True(FindInline<InlineUIContainer>(((Paragraph)pointy.Blocks.FirstBlock!).Inlines, _ => true)
                ?.Child is Image, "带空格的路径用 <…> 包起来能渲染");

            FlowDocument encoded = MarkdownRender.Build("![空格](%E6%9C%89%20%E7%A9%BA%E6%A0%BC.png)", dir, Theme());
            Check.True(FindInline<InlineUIContainer>(((Paragraph)encoded.Blocks.FirstBlock!).Inlines, _ => true)
                ?.Child is Image, "百分号编码的路径也能解析");

            FlowDocument missing = MarkdownRender.Build("![没有这张](nope.png)", dir, Theme());
            var missingParagraph = (Paragraph)missing.Blocks.FirstBlock!;
            Check.True(ParagraphText(missingParagraph).Contains("[图片：没有这张]"),
                "找不到的图片显示占位文字：" + ParagraphText(missingParagraph));

            FlowDocument remote = MarkdownRender.Build("![远程](https://example.com/a.png)", dir, Theme());
            Check.True(ParagraphText((Paragraph)remote.Blocks.FirstBlock!).Contains("[图片：远程]"),
                "http 图片不下载，显示占位文字");

            FlowDocument empty = MarkdownRender.Build("", dir, Theme());
            Check.Equal(1, empty.Blocks.Count, "空文档给一块提示");
            Check.True(ParagraphText((Paragraph)empty.Blocks.FirstBlock!).Contains("还没有内容"), "空文档的提示文案");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    // —— 工具 ——

    private static FlowDocument Build(string markdown) => MarkdownRender.Build(markdown, Path.GetTempPath(), Theme());

    /// <summary>主题资源由 App 的 Modern.xaml 提供；自测不建窗口，拿一个游离元素查资源即可。</summary>
    private static FrameworkElement Theme() => new Border();

    private static T? FindInline<T>(InlineCollection inlines, Func<T, bool> match) where T : Inline
    {
        foreach (Inline inline in inlines)
        {
            if (inline is T hit && match(hit)) return hit;
            if (inline is Span span)
            {
                T? inner = FindInline(span.Inlines, match);
                if (inner != null) return inner;
            }
        }
        return null;
    }

    private static string ParagraphText(Block block)
    {
        if (block is not Paragraph paragraph) return "";
        var text = new System.Text.StringBuilder();
        Append(paragraph.Inlines, text);
        return text.ToString();
    }

    private static void Append(InlineCollection inlines, System.Text.StringBuilder text)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run: text.Append(run.Text); break;
                case LineBreak: text.Append('\n'); break;
                case Span span: Append(span.Inlines, text); break;
            }
        }
    }
}
