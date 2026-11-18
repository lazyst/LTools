using System;
using System.Collections.Generic;
using System.Text;

namespace LTools.Features.CapturePro;

/// <summary>
/// LaTeX 源码页：从识别行里挑出「像公式」的行，拼成可复制的源码。
/// </summary>
/// <remarks>
/// <para>
/// 上游的 LaTeX 模式是**视觉模型**直接吐源码（`streamingLatexSource` 还会剥掉 ```` ```latex ```` 围栏），
/// 再交给内置 **MicroTeX** 渲染成预览图。本地两样都没有：没有公式视觉模型，也没有可离线的 TeX 排版引擎。
/// </para>
/// <para>
/// 所以这一页只做**源码**，绝不假渲染：按 TeX 特征（命令名 / 上下标 / `$…$` / `\begin{…}`）从当前的识别行里
/// 挑出公式行；一行都不像公式时退回「整段识别文字」，并在页脚说明这是整理前的原文。
/// 渲染留作可配端点（写进 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </para>
/// <para>纯函数，不碰 WPF：<c>--selftest</c> 直接断言。</para>
/// </remarks>
internal static class LatexSource
{
    /// <summary>TeX 命令名白名单（剥掉反斜杠后比对）。</summary>
    /// <remarks>
    /// 用白名单而不是「反斜杠 + 字母」：Windows 路径 <c>C:\Users\x</c> 也长那样，会把普通文字当成公式。
    /// </remarks>
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "frac", "dfrac", "tfrac", "cfrac", "sqrt", "binom", "overline", "underline", "hat", "widehat",
        "bar", "vec", "dot", "ddot", "underbrace", "overbrace", "stackrel",
        "sum", "prod", "int", "iint", "iiint", "oint", "lim", "partial", "nabla", "infty", "ell",
        "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta",
        "vartheta", "iota", "kappa", "lambda", "mu", "nu", "xi", "pi", "rho", "sigma", "tau",
        "upsilon", "phi", "varphi", "chi", "psi", "omega",
        "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon", "Phi", "Psi", "Omega",
        "times", "cdot", "cdots", "ldots", "dots", "div", "pm", "mp", "ast", "star", "circ",
        "leq", "le", "geq", "ge", "neq", "ne", "approx", "equiv", "sim", "simeq", "propto",
        "ll", "gg", "prec", "succ", "subset", "supset", "subseteq", "supseteq", "in", "notin",
        "cup", "cap", "setminus", "emptyset", "varnothing", "forall", "exists", "nexists",
        "to", "mapsto", "rightarrow", "leftarrow", "Rightarrow", "Leftarrow", "leftrightarrow",
        "Leftrightarrow", "longrightarrow", "xrightarrow", "uparrow", "downarrow",
        "left", "right", "big", "Big", "bigg", "Bigg", "bigl", "bigr",
        "begin", "end", "text", "textrm", "textbf", "textit", "mathrm", "mathbf", "mathit",
        "mathsf", "mathtt", "mathcal", "mathbb", "mathfrak", "operatorname",
        "quad", "qquad", "hspace", "space", "label", "tag", "nonumber",
        "matrix", "pmatrix", "bmatrix", "vmatrix", "Vmatrix", "cases", "aligned", "array",
        "exp", "log", "ln", "lg", "sin", "cos", "tan", "cot", "sec", "csc",
        "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "mod", "bmod", "pmod",
        "angle", "triangle", "degree", "prime", "because", "therefore",
    };

    /// <summary>这一行像不像公式（TeX 命令 / 上下标 / <c>$…$</c> / <c>\begin{…}</c>）。</summary>
    public static bool LooksLikeFormula(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        string text = line.Trim();
        if (text.Contains('$')) return true;
        if (HasScript(text)) return true;

        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '\\') continue;

            int start = i + 1;
            int end = start;
            while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
            if (end - start < 2) continue;
            if (Commands.Contains(text[start..end])) return true;

            i = end - 1;
        }

        return false;
    }

    /// <summary>上下标：<c>x^2</c> / <c>a_{n}</c> / <c>(n+1)^k</c>——两侧都得有东西才认。</summary>
    private static bool HasScript(string text)
    {
        for (int i = 1; i + 1 < text.Length; i++)
        {
            char c = text[i];
            if (c != '^' && c != '_') continue;

            char before = text[i - 1];
            char after = text[i + 1];
            bool left = char.IsLetterOrDigit(before) || before is '}' or ')' or ']';
            bool right = char.IsLetterOrDigit(after) || after is '{' or '(' or '\\';
            if (left && right) return true;
        }

        return false;
    }

    /// <summary>剥掉 Markdown 围栏（照上游 <c>streamingLatexSource</c> 只认 latex / tex 标签）。</summary>
    public static string Unfence(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        string result = text.Replace("\r\n", "\n").Trim();
        if (!result.StartsWith("```", StringComparison.Ordinal)) return result;

        int firstBreak = result.IndexOf('\n');
        if (firstBreak < 0) return result;

        string tag = result[3..firstBreak].Trim();
        if (tag.Length > 0 &&
            !(tag.StartsWith("latex", StringComparison.OrdinalIgnoreCase) ||
              tag.StartsWith("tex", StringComparison.OrdinalIgnoreCase)))
        {
            return result;   // ```python 这种不是公式围栏，原样留着
        }

        string body = result[(firstBreak + 1)..];
        int close = body.LastIndexOf("```", StringComparison.Ordinal);
        if (close >= 0) body = body[..close];
        return body.Trim();
    }

    /// <summary>整行被 <c>$$…$$</c> / <c>$…$</c> 包住时脱掉一层（行内混排的不动）。</summary>
    public static string TrimDelimiters(string text)
    {
        string value = text.Trim();
        if (value.Length >= 4 && value.StartsWith("$$", StringComparison.Ordinal) &&
            value.EndsWith("$$", StringComparison.Ordinal))
        {
            return value[2..^2].Trim();
        }

        if (value.Length >= 2 && value[0] == '$' && value[^1] == '$')
            return value[1..^1].Trim();

        return value;
    }

    /// <summary>挑出公式行（照当前识别顺序，剥围栏与定界符）。</summary>
    public static IReadOnlyList<string> Extract(IReadOnlyList<string> lines)
    {
        var result = new List<string>();
        foreach (string line in lines)
        {
            if (!LooksLikeFormula(line)) continue;

            string source = TrimDelimiters(Unfence(line));
            if (source.Length > 0) result.Add(source);
        }

        return result;
    }

    /// <summary>
    /// 这一页要显示 / 复制的源码。
    /// </summary>
    /// <param name="text">多段之间空一行。</param>
    /// <param name="formulaOnly">
    /// true = 真的挑出了公式行；false = 一行都不像公式，退回整段识别文字（页脚要说明）。
    /// </param>
    public static (string Text, bool FormulaOnly, int Lines) Build(IReadOnlyList<string> lines)
    {
        IReadOnlyList<string> formulas = Extract(lines);
        if (formulas.Count > 0) return (Join(formulas), true, formulas.Count);

        var plain = new List<string>();
        foreach (string line in lines)
        {
            string value = line?.Trim() ?? "";
            if (value.Length > 0) plain.Add(value);
        }

        return (Join(plain), false, plain.Count);
    }

    /// <summary>多段源码之间空一行（照 LaTeX 的段落习惯）。</summary>
    public static string Join(IReadOnlyList<string> sources)
    {
        var builder = new StringBuilder();
        foreach (string source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(source.Trim());
        }

        return builder.ToString();
    }

    /// <summary>页脚那句说明（挑到公式 / 退回原文两种）。</summary>
    public static string Describe(bool formulaOnly, int count)
    {
        if (count == 0) return "这张截图里没有识别到文字，没有可整理的源码。";

        return formulaOnly
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                "按识别文字整理出 {0} 段公式源码 · 上游由视觉模型直接给源码并渲染，本地只给源码", count)
            : "没认出公式特征，下面是整段识别文字（可自行整理成 LaTeX）";
    }
}
