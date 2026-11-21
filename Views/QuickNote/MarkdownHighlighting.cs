using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace LTools.Views;

/// <summary>
/// 速记正文的 Markdown 高亮定义。
/// </summary>
/// <remarks>
/// <para>
/// xshd 的颜色不能绑定 <c>StaticResource</c>，所以这里用主题里的实际色值现拼一份定义
/// （见 <c>Themes/Modern.xaml</c> 的 <c>TextBrush</c> / <c>WarningColor</c> / <c>LinkBrush</c> / <c>HintTextBrush</c>），
/// 而不是提交一个写死颜色的 <c>.xshd</c> 文件——主题一改，这里跟着变。
/// </para>
/// <para>
/// 只做「记号可辨认」，不做 CodeMirror 那种 overlay 级精度：xshd 只有行级规则与跨行 span，
/// 围栏代码块 / 行内代码 / 标题 / 引用 / 链接 / 列表记号是准的，嵌套的粗斜体会有偏差。
/// 真正的渲染（标题字号、表格、图片）在预览里做，不在高亮这一层。
/// </para>
/// <para>
/// <b>规则里不能写反向引用（<c>\1</c>）</b>：AvalonEdit 会把每条规则再包一层分组，
/// 编号位移后 <c>\1</c> 指向不存在的组，加载时直接抛 <c>HighlightingDefinitionInvalidException</c>
/// （分隔线那条踩过）。要「同一种字符重复三次」就老老实实列三支。
/// </para>
/// <para>
/// <b>跨行 span 必须显式写 <c>multiline="true"</c></b>：不写时 AvalonEdit 按单行处理，
/// 围栏代码块的三个反引号各自变成一条独立的「span」，中间的内容反而不上色（踩过）。
/// </para>
/// <para>
/// <b>规则正文里的 <c>#</c> 必须写成 <c>\#</c></b>：xshd 把元素内容里的 <c>#</c> 当行注释起点，
/// 于是 <c>^\#</c> 被截成 <c>^</c>，运行时变成「规则匹配 0 个字符」的异常
/// （标题那条踩过；<c>[#]</c> 也安全，但 <c>\#</c> 更贴近原正则）。反引号不受影响。
/// </para>
/// </remarks>
internal static class MarkdownHighlighting
{
    /// <summary>按当前主题造一份 Markdown 高亮定义。</summary>
    public static IHighlightingDefinition Create(FrameworkElement theme)
    {
        string xml = Template
            .Replace("@HEADING@", Hex(theme, "TextBrush", "#18181B"))
            .Replace("@CODE@", Hex(theme, "WarningBrush", "#B45309"))
            .Replace("@CODEBG@", Hex(theme, "SurfaceAltBrush", "#F4F4F5"))
            .Replace("@MARKER@", Hex(theme, "HintTextBrush", "#A1A1AA"))
            .Replace("@LINK@", Hex(theme, "LinkBrush", "#3B6EA5"))
            .Replace("@QUOTE@", Hex(theme, "SecondaryTextBrush", "#71717A"));

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string Hex(FrameworkElement theme, string key, string fallback)
    {
        object? value = theme.TryFindResource(key);
        return value switch
        {
            Color c => c.ToString(),
            SolidColorBrush b => b.Color.ToString(),
            _ => fallback,
        };
    }

    /// <summary>高亮规则（<c>@XXX@</c> 是主题色占位）。规则顺序即优先级：粗体在斜体前，围栏块在最前。</summary>
    private const string Template = """
        <SyntaxDefinition name="Markdown" extensions=".md" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Heading" foreground="@HEADING@" fontWeight="bold"/>
          <Color name="Strong" foreground="@HEADING@" fontWeight="bold"/>
          <Color name="Emphasis" foreground="@HEADING@" fontStyle="italic"/>
          <Color name="Code" foreground="@CODE@"/>
          <Color name="CodeBlock" foreground="@CODE@" background="@CODEBG@"/>
          <Color name="Marker" foreground="@MARKER@"/>
          <Color name="Link" foreground="@LINK@"/>
          <Color name="Quote" foreground="@QUOTE@"/>
          <RuleSet>
            <Span color="CodeBlock" multiline="true" begin="^\s*```[^`]*$" end="^\s*```\s*$"/>
            <Rule color="Heading">^\s{0,3}\#{1,6}(\s.*)?$</Rule>
            <Rule color="Marker">^\s{0,3}(-{3,}|\*{3,}|_{3,})\s*$</Rule>
            <Rule color="Quote">^\s{0,3}&gt;.*$</Rule>
            <Rule color="Marker">^\s*([-*+]|\d+[.)])\s</Rule>
            <Rule color="Link">!?\[[^\]\n]*\]\([^)\n]*\)</Rule>
            <Rule color="Strong">(\*\*|__)[^\n]+?(\*\*|__)</Rule>
            <Rule color="Emphasis">(\*|_)[^*_\n]+?(\*|_)</Rule>
            <Rule color="Code">`[^`\n]+`</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
