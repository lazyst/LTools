using System.Text.RegularExpressions;

namespace LTools.Core.Actions;

/// <summary>
/// 参数显示条件（<see cref="InputSpec.VisibleWhen"/>）的唯一解释器。
/// </summary>
/// <remarks>
/// <para>
/// 参数对话框用它决定哪一行收起，运行时用它决定「必填」是否真的要求填——
/// 两处必须同一套判断，否则会出现「编辑器里隐藏、运行时却报缺参数」。
/// </para>
/// <para>
/// 支持 <c>key == 'value'</c>、多个条件用 <c>||</c> 连接、值可不带引号（<c>capture == false</c>）。
/// 一句都解析不了（写法不认识）时按可见处理——宁可多显示一行，也别把参数悄悄藏起来。
/// </para>
/// </remarks>
internal static class VisibleWhen
{
    private static readonly Regex Rule = new(
        @"^\s*(?<key>\w+)\s*==\s*(?:'(?<value>[^']*)'|(?<value>[^\s']+))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>这一行现在该不该显示。</summary>
    public static bool IsVisible(InputSpec input, IReadOnlyDictionary<string, StepArg> args, UnitSpec spec)
    {
        string? rule = input.VisibleWhen;
        if (string.IsNullOrWhiteSpace(rule)) return true;

        bool parsed = false;
        foreach (string part in rule.Split("||"))
        {
            if (!TryParse(part, out string key, out string expected)) continue;
            parsed = true;

            string actual = args.TryGetValue(key, out var arg)
                ? arg.Literal.AsText()
                : (spec.Inputs.FirstOrDefault(i =>
                        string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))
                    ?.Default?.AsText() ?? "");

            if (string.Equals(actual, expected, StringComparison.Ordinal)) return true;
        }

        return !parsed;
    }

    /// <summary>解析第一条 <c>key == 'value'</c>（框选后切「固定区域」用它找驱动键）。</summary>
    public static bool TryGetRule(InputSpec input, out string key, out string value)
    {
        key = "";
        value = "";
        if (string.IsNullOrWhiteSpace(input.VisibleWhen)) return false;

        return TryParse(input.VisibleWhen.Split("||")[0], out key, out value);
    }

    private static bool TryParse(string text, out string key, out string value)
    {
        key = "";
        value = "";

        Match match = Rule.Match(text);
        if (!match.Success) return false;

        key = match.Groups["key"].Value;
        value = match.Groups["value"].Value;
        return true;
    }
}
