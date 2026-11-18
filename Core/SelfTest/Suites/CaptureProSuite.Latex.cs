using System.Collections.Generic;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// LaTeX 源码页（照上游 <c>screenshotlatexrenderer</c> 的源码输入侧；渲染是平台差异）。
/// </summary>
/// <remarks>
/// 纯函数，不碰 WPF：<c>LatexSource</c> 的判定 / 剥围栏 / 挑行 / 退回原文由这里断言，
/// 结果窗的页签与复制由仓库外 <c>capture-overlay-smoke</c> 端到端验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void LatexSourceCases()
    {
        LatexFormulaCases();
        LatexUnfenceCases();
        LatexExtractCases();
    }

    private static void LatexFormulaCases()
    {
        Check.True(LatexSource.LooksLikeFormula(@"\frac{1}{2}"), "反斜杠命令像公式");
        Check.True(LatexSource.LooksLikeFormula(@"E = mc^2"), "上标像公式");
        Check.True(LatexSource.LooksLikeFormula("a_{n+1} = a_n + d"), "下标像公式");
        Check.True(LatexSource.LooksLikeFormula("$x + y$"), "行内 $…$ 像公式");
        Check.True(LatexSource.LooksLikeFormula(@"\begin{aligned} x &= 1 \end{aligned}"),
            "环境写法像公式");
        Check.True(LatexSource.LooksLikeFormula(@"\alpha \cdot \beta"), "希腊字母命令像公式");

        // 白名单的意义：Windows 路径也是「反斜杠 + 字母」，不能认成公式
        Check.False(LatexSource.LooksLikeFormula(@"C:\Users\name\Desktop"), "路径不当公式");
        Check.False(LatexSource.LooksLikeFormula("这是一段普通的中文说明"), "普通文字不当公式");
        Check.False(LatexSource.LooksLikeFormula("hello world"), "英文句子不当公式");
        Check.False(LatexSource.LooksLikeFormula(""), "空行不当公式");
        Check.False(LatexSource.LooksLikeFormula(null), "null 不当公式");
    }

    private static void LatexUnfenceCases()
    {
        Check.Equal("x^2", LatexSource.Unfence("```latex\nx^2\n```"), "剥掉 latex 围栏");
        Check.Equal("x^2", LatexSource.Unfence("```tex\r\nx^2\r\n```"), "剥掉 tex 围栏（CRLF）");
        Check.Equal("```python\nprint(1)\n```", LatexSource.Unfence("```python\nprint(1)\n```"),
            "别的语言围栏原样留着");
        Check.Equal("x^2", LatexSource.Unfence("  x^2  "), "没有围栏只是去空白");
        Check.Equal("", LatexSource.Unfence(""), "空文本仍是空");

        Check.Equal("x^2", LatexSource.TrimDelimiters("$$x^2$$"), "脱掉整行 $$…$$");
        Check.Equal("x^2", LatexSource.TrimDelimiters("$x^2$"), "脱掉整行 $…$");
        Check.Equal("a $x$ b", LatexSource.TrimDelimiters("a $x$ b"), "行内混排的定界符不动");
        Check.Equal("x", LatexSource.TrimDelimiters("  x  "), "没有定界符只是去空白");
    }

    private static void LatexExtractCases()
    {
        var lines = new List<string>
        {
            "下面是一个公式：",
            @"$$E = mc^2$$",
            @"\frac{a}{b} + \sqrt{c}",
            "普通说明文字",
            "",
            @"C:\Users\name\Desktop",
        };

        IReadOnlyList<string> formulas = LatexSource.Extract(lines);
        Check.Equal(2, formulas.Count, "只挑出两行公式");
        Check.Equal("E = mc^2", formulas[0], "第一行脱掉了 $$ 定界符");
        Check.Equal(@"\frac{a}{b} + \sqrt{c}", formulas[1], "第二行原样保留命令");

        (string text, bool formulaOnly, int count) = LatexSource.Build(lines);
        Check.True(formulaOnly, "挑到公式时 FormulaOnly 为真");
        Check.Equal(2, count, "段数是 2");
        Check.Equal("E = mc^2\n\n\\frac{a}{b} + \\sqrt{c}", text, "两段之间空一行");

        // 一行都不像公式：退回整段识别文字（页脚要说明这是原文）
        var prose = new List<string> { "第一行说明", "", "  第二行说明  " };
        (string plain, bool plainFormula, int plainCount) = LatexSource.Build(prose);
        Check.False(plainFormula, "没有公式时 FormulaOnly 为假");
        Check.Equal(2, plainCount, "空行不算一段");
        Check.Equal("第一行说明\n\n第二行说明", plain, "退回原文时也去空白");
        Check.True(LatexSource.Describe(false, 2).Contains("整段识别文字"), "退回原文的说明写明是原文");

        (string empty, bool emptyFormula, int emptyCount) = LatexSource.Build(new List<string>());
        Check.Equal("", empty, "空文本没有源码");
        Check.False(emptyFormula, "空文本不是公式模式");
        Check.Equal(0, emptyCount, "空文本段数为 0");
        Check.True(LatexSource.Describe(true, 0).Contains("没有识别到文字"), "空态说明");

        Check.True(LatexSource.Describe(true, 1).Contains("上游"), "公式模式的说明点出平台差异");
        Check.Equal("a\n\nb", LatexSource.Join(new[] { "a", " ", "b" }), "拼接跳过空白段");
    }
}
