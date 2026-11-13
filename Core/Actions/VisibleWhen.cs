using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LTools.Core.Actions;

/// <summary>
/// 参数显示规则的唯一解释器：先按驱动键的 <see cref="InputSpec.OptionParams"/>
/// （选项可见参数）做门槛，再按参数自己的 <see cref="InputSpec.VisibleWhen"/> 做附加条件。
/// 规格见 <c>docs/PARAM_FORM.md</c>。
/// </summary>
/// <remarks>
/// <para>
/// 参数对话框、运行期必填判断、静态校验、保存校验、框选后切「固定区域」全调
/// <see cref="IsVisible"/>，别再各写一套。
/// </para>
/// <para>
/// <c>VisibleWhen</c> 支持 <c>key == 'value'</c>、<c>key in ('a','b')</c>、
/// 多个条件用 <c>||</c> 连接、值可不带引号（<c>capture == false</c>）。
/// 一句都解析不了时按可见处理——宁可多显示一行，也别把参数悄悄藏起来。
/// </para>
/// <para>
/// 枚举参数只能是确定字面量，不需要「变量驱动时全显示」的分支；驱动键取不到值时
/// 门槛回落默认选项 / 按可见处理，不会把参数全藏掉。
/// </para>
/// </remarks>
internal static class VisibleWhen
{
    private static readonly Regex EqualRule = new(
        @"^\s*(?<key>\w+)\s*==\s*(?:'(?<value>[^']*)'|(?<value>[^\s']+))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InRule = new(
        @"^\s*(?<key>\w+)\s+in\s*\((?<values>[^)]*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>这一行现在该不该显示：选项门槛 AND 附加条件。</summary>
    public static bool IsVisible(InputSpec input, IReadOnlyDictionary<string, StepArg> args, UnitSpec spec) =>
        OptionGate(input, args, spec) && RuleAllows(input, args, spec);

    /// <summary>
    /// 选项可见参数门槛：找动作单元里唯一的驱动键，取它当前值命中的参数集合。
    /// 参数没被任何选项列出（不归它管）时按可见处理；当前值不在声明里也按可见处理。
    /// </summary>
    private static bool OptionGate(InputSpec input, IReadOnlyDictionary<string, StepArg> args, UnitSpec spec)
    {
        InputSpec? driver = spec.Inputs.FirstOrDefault(i => i.OptionParams is { Count: > 0 });
        var groups = driver?.OptionParams;
        if (driver == null || groups == null) return true;

        bool governed = groups.Values.Any(keys => keys.Any(k =>
            string.Equals(k, input.Key, StringComparison.OrdinalIgnoreCase)));
        if (!governed) return true;

        string actual = (args.TryGetValue(driver.Key, out var arg)
            ? arg.Literal.AsText()
            : driver.Default?.AsText() ?? "").Trim();

        IReadOnlyList<string> enabled = Array.Empty<string>();
        bool known = false;
        foreach (var pair in groups)
        {
            if (!string.Equals(pair.Key, actual, StringComparison.OrdinalIgnoreCase)) continue;
            enabled = pair.Value;
            known = true;
            break;
        }

        if (!known)
        {
            // 没写进 OptionParams 的合法选项 = 不启用任何受管参数（不列参数也不会漏显示）；
            // 未知旧值 / 空值才按可见处理，别把参数全藏掉。
            bool valid = driver.Options is { Count: > 0 } options
                && options.Any(o => string.Equals(o, actual, StringComparison.OrdinalIgnoreCase));
            if (!valid) return true;
        }

        return enabled.Any(k => string.Equals(k, input.Key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RuleAllows(InputSpec input, IReadOnlyDictionary<string, StepArg> args, UnitSpec spec)
    {
        string? rule = input.VisibleWhen;
        if (string.IsNullOrWhiteSpace(rule)) return true;

        bool parsed = false;
        foreach (string part in rule.Split("||"))
        {
            if (!TryParse(part, out string key, out string[] expected)) continue;
            parsed = true;

            string actual = args.TryGetValue(key, out var arg)
                ? arg.Literal.AsText()
                : (spec.Inputs.FirstOrDefault(i =>
                        string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))
                    ?.Default?.AsText() ?? "");

            if (expected.Any(v => string.Equals(actual, v, StringComparison.Ordinal))) return true;
        }

        return !parsed;
    }

    /// <summary>解析第一条规则的首个期望值（框选后切「固定区域」用它找驱动键）。</summary>
    public static bool TryGetRule(InputSpec input, out string key, out string value)
    {
        key = "";
        value = "";
        if (string.IsNullOrWhiteSpace(input.VisibleWhen)) return false;
        if (!TryParse(input.VisibleWhen.Split("||")[0], out key, out var expected)
            || expected.Length == 0) return false;
        value = expected[0];
        return true;
    }

    /// <summary>
    /// 找到「哪个枚举选项会显示这个参数」：参数迁到 <c>OptionParams</c> 后没有
    /// <c>VisibleWhen</c>，框选后要靠它把配套枚举切到该选项（如 <c>rect</c>）。
    /// </summary>
    public static bool TryGetOptionFor(InputSpec input, UnitSpec spec, out string key, out string value)
    {
        key = "";
        value = "";
        InputSpec? driver = spec.Inputs.FirstOrDefault(i => i.OptionParams is { Count: > 0 });
        if (driver?.OptionParams == null) return false;

        foreach (var pair in driver.OptionParams)
        {
            if (!pair.Value.Any(k => string.Equals(k, input.Key, StringComparison.OrdinalIgnoreCase)))
                continue;
            key = driver.Key;
            value = pair.Key;
            return true;
        }
        return false;
    }

    private static bool TryParse(string text, out string key, out string[] expected)
    {
        key = "";
        expected = Array.Empty<string>();

        Match equal = EqualRule.Match(text);
        if (equal.Success)
        {
            key = equal.Groups["key"].Value;
            expected = new[] { equal.Groups["value"].Value };
            return true;
        }

        Match list = InRule.Match(text);
        if (!list.Success) return false;
        key = list.Groups["key"].Value;
        expected = list.Groups["values"].Value
            .Split(',')
            .Select(s => s.Trim().Trim('\'').Trim('"'))
            .Where(s => s.Length > 0)
            .ToArray();
        return expected.Length > 0;
    }
}
