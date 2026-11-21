using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace LTools.Views;

/// <summary>
/// Markdown → <see cref="FlowDocument"/>：速记预览的唯一实现（Ctrl+P）。
/// </summary>
/// <remarks>
/// <para>
/// 只用 Markdig 解析成 AST，渲染自己做：这样配色跟主题走、每块能带上源码行号（点预览回源码），
/// 也不用把 Markdig.Wpf 那套（自带配色 / 固定链接样式 / 单独的 xshd）再掰弯。
/// </para>
/// <para>
/// 支持的形状：标题 1–6、段落、粗 / 斜 / 删除线、行内代码、围栏代码块、链接、图片（本地相对路径）、
/// 引用、有序 / 无序 / 嵌套列表、任务列表、围栏表格、分隔线。**不做**：行内 HTML（原样跳过）、
/// 数学、脚注、front-matter——速记用不到，多一个就多一份要维护的解析分支。
/// </para>
/// <para>
/// 软换行按硬换行渲染（<c>UseSoftlineBreakAsHardlineBreak</c>）：中文笔记里按一次回车就期望换行，
/// 标准 Markdown 那种「软换行折成空格」在速记里只会让人以为格式丢了。
/// </para>
/// </remarks>
internal static class MarkdownRender
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>把 Markdown 源码渲染成一份只读文档。<paramref name="baseDirectory"/> 是图片相对路径的根。</summary>
    public static FlowDocument Build(string? markdown, string baseDirectory, FrameworkElement theme,
        double maxImageWidth = 760)
    {
        var ctx = new Ctx(theme, baseDirectory, maxImageWidth);
        var document = new FlowDocument
        {
            FontFamily = ctx.Ui,
            FontSize = ctx.BodySize,
            Foreground = ctx.Text,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            // 中文正文按 1.55 倍行高排：默认行高读长段落太挤。MaxHeight 不会把图片裁掉。
            LineHeight = ctx.BodySize * 1.55,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };

        AddBlocks(document.Blocks, Markdown.Parse(markdown ?? "", Pipeline), ctx, compact: false);
        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("这条速记还没有内容"))
            {
                Foreground = ctx.Hint,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
        return document;
    }

    // ============ 块 ============

    private static void AddBlocks(BlockCollection target, ContainerBlock source, Ctx ctx, bool compact)
    {
        foreach (MdBlock block in source)
            target.Add(RenderBlock(block, ctx, compact));
    }

    private static WpfBlock RenderBlock(MdBlock block, Ctx ctx, bool compact)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                double size = heading.Level switch
                {
                    1 => ctx.BodySize * 1.6,
                    2 => ctx.BodySize * 1.35,
                    3 => ctx.BodySize * 1.15,
                    _ => ctx.BodySize * 1.03,
                };
                var paragraph = new Paragraph
                {
                    FontSize = size,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, heading.Level == 1 ? 4 : 12, 0, 6),
                    LineHeight = size * 1.35,
                };
                AddInlines(paragraph.Inlines, heading.Inline, ctx);
                return Tag(paragraph, heading);
            }

            case ParagraphBlock paragraphBlock:
            {
                var paragraph = new Paragraph
                {
                    Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 8),
                };
                AddInlines(paragraph.Inlines, paragraphBlock.Inline, ctx);
                return Tag(paragraph, paragraphBlock);
            }

            case ListBlock listBlock:
            {
                var list = new List
                {
                    MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(16, 2, 0, 8),
                    Padding = new Thickness(0),
                };
                foreach (ListItemBlock item in listBlock)
                {
                    var listItem = new ListItem();
                    AddBlocks(listItem.Blocks, item, ctx, compact);
                    list.ListItems.Add(listItem);
                }
                return Tag(list, listBlock);
            }

            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderBrush = ctx.Border,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 4, 0, 10),
                    Foreground = ctx.Secondary,
                };
                AddBlocks(section.Blocks, quote, ctx, compact);
                return Tag(section, quote);
            }

            case FencedCodeBlock fenced:
                return CodeBlock(fenced, fenced.Lines.ToString(), ctx, compact);

            case CodeBlock code:
                return CodeBlock(code, code.Lines.ToString(), ctx, compact);

            case MdTable table:
                return TableBlock(table, ctx);

            case ThematicBreakBlock rule:
                return Tag(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = ctx.Border,
                    Margin = new Thickness(0, 10, 0, 10),
                }), rule);

            case HtmlBlock html:
            {
                // 不做行内 HTML：原样显示，至少让人看见「这里本来写了什么」。
                return Tag(new Paragraph(new Run(html.Lines.ToString().TrimEnd()))
                {
                    FontFamily = ctx.Mono,
                    FontSize = ctx.BodySize - 0.5,
                    Foreground = ctx.Hint,
                    Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 8),
                }, html);
            }

            case LeafBlock leaf:
            {
                var paragraph = new Paragraph(new Run(leaf.Lines.ToString().TrimEnd()))
                {
                    Margin = compact ? new Thickness(0) : new Thickness(0, 2, 0, 8),
                };
                return Tag(paragraph, leaf);
            }

            case ContainerBlock container:
            {
                var section = new Section { Margin = new Thickness(0) };
                AddBlocks(section.Blocks, container, ctx, compact);
                return Tag(section, container);
            }

            default:
                return new Section();
        }
    }

    private static WpfBlock CodeBlock(MdBlock source, string text, Ctx ctx, bool compact)
    {
        return Tag(new Paragraph(new Run(text.TrimEnd('\r', '\n')))
        {
            FontFamily = ctx.Mono,
            FontSize = ctx.BodySize - 0.5,
            Background = ctx.SurfaceAlt,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 6, 0, 10),
            LineHeight = (ctx.BodySize - 0.5) * 1.6,
        }, source);
    }

    private static WpfBlock TableBlock(MdTable source, Ctx ctx)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 10),
        };

        int columns = source.ColumnDefinitions.Count;
        if (columns == 0)
            foreach (MdTableRow row in source)
                foreach (MdTableCell cell in row)
                    columns = Math.Max(columns, cell.ColumnIndex + Math.Max(1, cell.ColumnSpan));
        columns = Math.Max(1, columns);

        for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (MdTableRow row in source)
        {
            var tableRow = new TableRow();
            foreach (MdTableCell cell in row)
            {
                var target = new TableCell
                {
                    BorderBrush = ctx.Border,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = row.IsHeader ? ctx.SurfaceAlt : Brushes.Transparent,
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    ColumnSpan = Math.Max(1, cell.ColumnSpan),
                };
                AddBlocks(target.Blocks, cell, ctx, compact: true);
                tableRow.Cells.Add(target);
            }
            group.Rows.Add(tableRow);
        }
        table.RowGroups.Add(group);
        return Tag(table, source);
    }

    // ============ 行内 ============

    private static void AddInlines(InlineCollection target, ContainerInline? source, Ctx ctx)
    {
        if (source == null) return;
        for (MdInline? inline = source.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~')
                        span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeights.Bold;
                    else
                        span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis, ctx);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = ctx.Mono,
                        FontSize = ctx.BodySize - 0.5,
                        Foreground = ctx.Warning,
                        Background = ctx.SurfaceAlt,
                    });
                    break;

                case LinkInline { IsImage: true } image:
                    target.Add(ImageInline(image, ctx));
                    break;

                case LinkInline link:
                    target.Add(LinkInline(link, ctx));
                    break;

                case AutolinkInline autolink:
                    target.Add(AutoLink(autolink.Url, autolink.Url, ctx));
                    break;

                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ ") { Foreground = ctx.Secondary });
                    break;

                case Markdig.Syntax.Inlines.LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                case ContainerInline container:
                    AddInlines(target, container, ctx);
                    break;
            }
        }
    }

    private static WpfInline LinkInline(LinkInline link, Ctx ctx)
    {
        string url = link.Url ?? "";
        var hyperlink = new Hyperlink
        {
            Foreground = ctx.Link,
            TextDecorations = TextDecorations.Underline,
            ToolTip = url,
        };
        AddInlines(hyperlink.Inlines, link, ctx);
        if (TryAbsoluteUri(url, out Uri? uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(uri!);
            };
        }
        return hyperlink;
    }

    private static WpfInline AutoLink(string url, string text, Ctx ctx)
    {
        var hyperlink = new Hyperlink(new Run(text))
        {
            Foreground = ctx.Link,
            TextDecorations = TextDecorations.Underline,
            ToolTip = url,
        };
        if (TryAbsoluteUri(url, out Uri? uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(uri!);
            };
        }
        return hyperlink;
    }

    private static WpfInline ImageInline(LinkInline link, Ctx ctx)
    {
        string url = link.Url ?? "";
        string alt = PlainText(link);
        string? path = ResolveImage(url, ctx.BaseDirectory);
        if (path == null)
            return Placeholder(alt.Length > 0 ? alt : url, ctx);

        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            double scale = bitmap.PixelWidth > ctx.MaxImageWidth
                ? ctx.MaxImageWidth / bitmap.PixelWidth
                : 1;
            var image = new Image
            {
                Source = bitmap,
                Width = bitmap.PixelWidth * scale,
                Height = bitmap.PixelHeight * scale,
                Stretch = Stretch.Uniform,
            };
            return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
        }
        catch
        {
            return Placeholder(alt.Length > 0 ? alt : url, ctx);
        }
    }

    private static WpfInline Placeholder(string name, Ctx ctx)
        => new Run($"[图片：{name}]") { Foreground = ctx.Hint };

    /// <summary>相对路径按速记所在目录解析；只认本地文件（不下载 http 图片）。</summary>
    private static string? ResolveImage(string url, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

        string? TryResolve(string candidate)
        {
            try
            {
                string path = Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDirectory, candidate);
                path = Path.GetFullPath(path);
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        return TryResolve(url) ?? TryResolve(Uri.UnescapeDataString(url));
    }

    private static string PlainText(ContainerInline source)
    {
        var text = new System.Text.StringBuilder();
        for (MdInline? inline = source.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case ContainerInline container: text.Append(PlainText(container)); break;
            }
        }
        return text.ToString();
    }

    private static bool TryAbsoluteUri(string url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme is not ("http" or "https" or "mailto")) return false;
        uri = parsed;
        return true;
    }

    private static void OpenExternal(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* 没有默认浏览器之类：点了没反应，但别把预览弄崩 */ }
    }

    private static T Tag<T>(T element, MarkdownObject source) where T : TextElement
    {
        element.Tag = source.Line + 1; // Markdig 的行号是 0 起
        return element;
    }

    /// <summary>一次渲染用到的主题色与尺寸（不在每个元素上重复查资源）。</summary>
    private sealed class Ctx
    {
        public Ctx(FrameworkElement theme, string baseDirectory, double maxImageWidth)
        {
            BaseDirectory = baseDirectory;
            MaxImageWidth = maxImageWidth;
            Ui = theme.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Microsoft YaHei UI");
            BodySize = theme.TryFindResource("FontSizeBody") is double size ? size : 13;
            Mono = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            Text = Brush(theme, "TextBrush", 0x18, 0x18, 0x1B);
            Secondary = Brush(theme, "SecondaryTextBrush", 0x71, 0x71, 0x7A);
            Hint = Brush(theme, "HintTextBrush", 0xA1, 0xA1, 0xAA);
            Border = Brush(theme, "BorderBrush", 0xE4, 0xE4, 0xE7);
            SurfaceAlt = Brush(theme, "SurfaceAltBrush", 0xF4, 0xF4, 0xF5);
            Link = Brush(theme, "LinkBrush", 0x3B, 0x6E, 0xA5);
            Warning = Brush(theme, "WarningBrush", 0xB4, 0x53, 0x09);
        }

        public FontFamily Ui { get; }
        public FontFamily Mono { get; }
        public double BodySize { get; }
        public Brush Text { get; }
        public Brush Secondary { get; }
        public Brush Hint { get; }
        public Brush Border { get; }
        public Brush SurfaceAlt { get; }
        public Brush Link { get; }
        public Brush Warning { get; }
        public string BaseDirectory { get; }
        public double MaxImageWidth { get; }

        private static Brush Brush(FrameworkElement theme, string key, byte r, byte g, byte b)
            => theme.TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
