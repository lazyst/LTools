using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LTools.Core.Actions;

/// <summary>
/// 一条「参数什么时候显示」的判定项：驱动键 + 允许的取值（任一命中即可）。
/// </summary>
/// <param name="Key">被读的参数键。</param>
/// <param name="Values">允许的取值（比较用 <see cref="StringComparison.Ordinal"/>，与旧行为一致）。</param>
/// <param name="Quoted">取值是带引号的字面量（枚举）——自检时要求它们在 <see cref="InputSpec.Options"/> 里。</param>
internal sealed record VisibilityTerm(string Key, IReadOnlyList<string> Values, bool Quoted);

/// <summary>
/// 「参数该不该显示」的内部模型（P4）：把 <see cref="InputSpec.OptionParams"/>（选项门槛）与
/// <see cref="InputSpec.VisibleWhen"/>（附加条件）解析成**一份**可复用、可自检的结构。
/// </summary>
/// <remarks>
/// <para>
/// 以前这里有两套解释：枚举驱动的「选项门槛」直接读字典，附加条件每次用正则重新解析字符串。
/// 现在两者在单元声明自检时就解析成 <see cref="VisibilityTerm"/>，运行期只做比较——
/// 参数对话框、运行期必填判断、静态校验、步骤摘要共用同一个 <see cref="IsVisible"/>。
/// </para>
/// <para>
/// 结构原则：**枚举驱动的一律写 <see cref="InputSpec.OptionParams"/>**（有结构、能导出成知识包表格），
/// <see cref="InputSpec.VisibleWhen"/> 只留给它表达不了的附加条件（布尔开关、跨键 OR，如
/// <c>gradual == true || operation == 'drag'</c>）。
/// </para>
/// <para>
/// 求值语义（保持不变）：门槛只看「参数是否被某个选项列出」，没被列出 = 始终可见；
/// 当前值不在声明里（未知旧值 / 空值）按可见处理——宁可多显示一行，也别把参数悄悄藏起来。
/// 附加条件里一句都解析不了时同样按可见处理。
/// </para>
/// </remarks>
internal sealed class VisibilityModel
{
    private static readonly Regex EqualRule = new(
        @"^\s*(?<key>\w+)\s*==\s*(?:'(?<value>[^']*)'|(?<value>[^\s']+))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InRule = new(
        @"^\s*(?<key>\w+)\s+in\s*\((?<values>[^)]*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>选项门槛的驱动键（<see cref="InputSpec.OptionParams"/> 所在的枚举参数）；没有就是 null。</summary>
    private readonly InputSpec? _driver;

    /// <summary>选项值 → 该选项启用哪些参数键（大小写不敏感，与旧行为一致）。</summary>
    private readonly Dictionary<string, string[]> _enabled;

    /// <summary>选项声明本身的行顺序（<see cref="TryGetOptionFor"/> 要按声明顺序取第一条）。</summary>
    private readonly List<(string Option, string[] Keys)> _optionRows;

    /// <summary>被某个选项列出的参数键（只有它们才受门槛管）。</summary>
    private readonly HashSet<string> _governed;

    /// <summary>参数键 → 声明（取默认值用；重名时第一个赢，与旧的 FirstOrDefault 一致）。</summary>
    private readonly Dictionary<string, InputSpec> _byKey;

    /// <summary>参数键 → 附加条件（多个 <c>||</c> 之间任意成立即可）。</summary>
    private readonly Dictionary<string, VisibilityTerm[]> _rules;

    internal VisibilityModel(UnitSpec spec)
    {
        _byKey = new Dictionary<string, InputSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in spec.Inputs)
            if (!_byKey.ContainsKey(input.Key)) _byKey[input.Key] = input;

        _driver = spec.Inputs.FirstOrDefault(i => i.OptionParams is { Count: > 0 });
        _enabled = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        _optionRows = new List<(string, string[])>();
        _governed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_driver?.OptionParams is { Count: > 0 } groups)
        {
            foreach (var pair in groups)
            {
                string[] keys = pair.Value.ToArray();
                _enabled[pair.Key] = keys;
                _optionRows.Add((pair.Key, keys));
                foreach (string key in keys) _governed.Add(key);
            }
        }

        _rules = new Dictionary<string, VisibilityTerm[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in spec.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.VisibleWhen)) continue;

            List<VisibilityTerm> terms = ParseRule(input.VisibleWhen, out _);
            if (terms.Count > 0) _rules[input.Key] = terms.ToArray();
        }
    }

    /// <summary>为一个动作单元建模型（<see cref="UnitSpec"/> 会缓存它，不要每次求值都建）。</summary>
    public static VisibilityModel Build(UnitSpec spec) => new(spec);

    // ============ 求值 ============

    /// <summary>这一行现在该不该显示：选项门槛 AND 附加条件。</summary>
    public bool IsVisible(InputSpec input, IReadOnlyDictionary<string, StepArg> args) =>
        Gate(input.Key, args) && RuleAllows(input.Key, args);

    /// <summary>
    /// 选项门槛：参数被某个选项列出时才受管——当前选项没列它就不显示。
    /// 没被任何选项列出 = 不归选项管（始终可见）；当前值不在声明里按可见处理。
    /// </summary>
    private bool Gate(string key, IReadOnlyDictionary<string, StepArg> args)
    {
        if (_driver == null || !_governed.Contains(key)) return true;

        string actual = (args.TryGetValue(_driver.Key, out var arg)
            ? arg.Literal.AsText()
            : _driver.Default?.AsText() ?? "").Trim();

        if (_enabled.TryGetValue(actual, out string[]? keys))
            return keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

        // 没写进 OptionParams 的合法选项 = 不启用任何受管参数；
        // 未知旧值 / 空值才按可见处理，别把参数全藏掉。
        bool valid = _driver.Options is { Count: > 0 } options
            && options.Any(o => string.Equals(o, actual, StringComparison.OrdinalIgnoreCase));
        return !valid;
    }

    /// <summary>附加条件：<c>||</c> 之间任一成立即可；一句都解析不了时按可见处理。</summary>
    private bool RuleAllows(string key, IReadOnlyDictionary<string, StepArg> args)
    {
        if (!_rules.TryGetValue(key, out VisibilityTerm[]? terms)) return true;

        foreach (VisibilityTerm term in terms)
        {
            string actual = args.TryGetValue(term.Key, out var arg)
                ? arg.Literal.AsText()
                : (_byKey.TryGetValue(term.Key, out var spec) ? spec.Default?.AsText() ?? "" : "");

            if (term.Values.Any(v => string.Equals(actual, v, StringComparison.Ordinal))) return true;
        }

        return false;
    }

    // ============ 反向查询（框选 / 摘要 / 知识包） ============

    /// <summary>
    /// 找到「哪个枚举选项会显示这个参数」：参数迁到 <see cref="InputSpec.OptionParams"/> 后没有
    /// <see cref="InputSpec.VisibleWhen"/>，框选后要靠它把配套枚举切到该选项（如 <c>rect</c>）。
    /// </summary>
    public bool TryGetOptionFor(string inputKey, out string key, out string value)
    {
        key = "";
        value = "";
        if (_driver == null) return false;

        foreach (var (option, keys) in _optionRows)
        {
            if (!keys.Any(k => string.Equals(k, inputKey, StringComparison.OrdinalIgnoreCase))) continue;
            key = _driver.Key;
            value = option;
            return true;
        }

        return false;
    }

    // ============ 解析 / 自检 ============

    /// <summary>
    /// 解析整条 <c>VisibleWhen</c>：返回所有能解析的判定项，并给出第一段解析不了时的原文
    /// （<paramref name="unparsed"/> 非空 = 声明有错，<see cref="UnitSpec.Validate"/> 会拒绝）。
    /// </summary>
    public static List<VisibilityTerm> ParseRule(string rule, out string[] unparsed)
    {
        var bad = new List<string>();
        var terms = new List<VisibilityTerm>();

        foreach (string part in rule.Split("||"))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            VisibilityTerm? term = TryParseTerm(part);
            if (term == null || term.Values.Count == 0) bad.Add(part.Trim());
            else terms.Add(term);
        }

        unparsed = bad.ToArray();
        return terms;
    }

    private static VisibilityTerm? TryParseTerm(string text)
    {
        Match equal = EqualRule.Match(text);
        if (equal.Success)
        {
            string raw = equal.Groups["value"].Value;
            bool quoted = text.Contains('\'');
            return new VisibilityTerm(equal.Groups["key"].Value, new[] { raw }, quoted);
        }

        Match list = InRule.Match(text);
        if (!list.Success) return null;

        string[] values = list.Groups["values"].Value
            .Split(',')
            .Select(s => s.Trim().Trim('\'').Trim('"'))
            .Where(s => s.Length > 0)
            .ToArray();
        if (values.Length == 0) return null;

        return new VisibilityTerm(list.Groups["key"].Value, values, text.Contains('\''));
    }
}
