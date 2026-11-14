using System.Collections.Generic;

namespace LTools.Core.Actions;

/// <summary>
/// 参数显示规则的唯一入口：先按驱动键的 <see cref="InputSpec.OptionParams"/>
/// （选项可见参数）做门槛，再按参数自己的 <see cref="InputSpec.VisibleWhen"/> 做附加条件。
/// 规格见 <c>docs/PARAM_FORM.md</c>。
/// </summary>
/// <remarks>
/// <para>
/// 参数对话框、运行期必填判断、静态校验、保存校验、框选后切「固定区域」全调
/// <see cref="IsVisible"/>，别再各写一套。
/// </para>
/// <para>
/// 真正的解析与求值在 <see cref="VisibilityModel"/>（P4）：每个动作单元只解析一次，
/// 运行期只做字符串比较。这里保持原来的方法签名不变，调用方按 <see cref="UnitSpec"/> 传进来即可。
/// </para>
/// <para>
/// <b>写声明时</b>：枚举驱动的可见性一律写 <see cref="InputSpec.OptionParams"/>
/// （有结构、能导出成知识包表格），<see cref="InputSpec.VisibleWhen"/> 只留给它表达不了的
/// 附加条件——布尔开关（<c>capture == false</c>）或跨键 OR
/// （<c>gradual == true || operation == 'drag'</c>）。两者按 AND 求值；
/// 引用的键不存在、枚举字面量不在 <see cref="InputSpec.Options"/> 里，
/// <see cref="UnitSpec.Validate"/> 会在注册时就报出来。
/// </para>
/// </remarks>
internal static class VisibleWhen
{
    /// <summary>这一行现在该不该显示：选项门槛 AND 附加条件。</summary>
    public static bool IsVisible(InputSpec input, IReadOnlyDictionary<string, StepArg> args, UnitSpec spec) =>
        spec.Visibility.IsVisible(input, args);

    /// <summary>解析附加条件的第一条规则（框选后切「固定区域」用它找驱动键）。</summary>
    public static bool TryGetRule(InputSpec input, out string key, out string value)
    {
        key = "";
        value = "";

        // 旧语义：只看第一段（<c>a == 'x' || …</c> 里的 <c>a == 'x'</c>），第一段解析不了就是没有。
        List<VisibilityTerm> terms = VisibilityModel.ParseRule(
            (input.VisibleWhen ?? "").Split("||")[0], out _);
        if (terms.Count == 0) return false;

        VisibilityTerm term = terms[0];
        key = term.Key;
        value = term.Values[0];
        return true;
    }

    /// <summary>
    /// 找到「哪个枚举选项会显示这个参数」：参数迁到 <see cref="InputSpec.OptionParams"/> 后没有
    /// <see cref="InputSpec.VisibleWhen"/>，框选后要靠它把配套枚举切到该选项（如 <c>rect</c>）。
    /// </summary>
    public static bool TryGetOptionFor(InputSpec input, UnitSpec spec, out string key, out string value) =>
        spec.Visibility.TryGetOptionFor(input.Key, out key, out value);
}
