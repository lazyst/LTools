using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 参数行的取值来源：「当前值」（步骤里存的，取不到就用声明的默认值）与
/// 「从列表选…」的候选（按键 / 宏 / 子动作）。冻结处理见 <see cref="ArgFreeze"/>。
/// </summary>
public partial class StepFormView
{
    /// <summary>
    /// 「从列表选…」的候选来源：按键（按键盘分区）、已录制的宏、子动作库。
    /// 返回 null = 这个参数不是候选型。
    /// </summary>
    private Func<IEnumerable<StepChoice>>? ChoiceSourceFor(InputSpec input)
    {
        if (input.Editor == InputEditor.key)
            return () => KeyCatalog.Groups.SelectMany(group =>
                group.Keys.Select(key =>
                    new StepChoice(key.Value, key.Label, group.Name)));

        if (input.Editor == InputEditor.macro)
            return () => MacroStore.List().Select(m =>
                new StepChoice(m.Id, m.Name, $"{m.DurationText} · {m.EventCount} 事件"));

        if (_step.Unit == "sub.run" && string.Equals(input.Key, "subAction", StringComparison.OrdinalIgnoreCase))
            return () => SubActionChoices().Select(c => new StepChoice(c.Id, c.Display, SubActionSource(c.Id)));

        return null;
    }

    /// <summary>子动作在哪儿定义的（副标题用）。</summary>
    private static string SubActionSource(string id) =>
        SubActionStore.Load(id) == null ? "未找到" : "全局子动作库";

    /// <summary>这一行现在该填什么：步骤里存的值，没有就用声明的默认值。</summary>
    private StepArg? CurrentArg(InputSpec input)
    {
        if (_step.In.TryGetValue(input.Key, out var arg)) return arg;
        return input.Default != null ? StepArg.FromValue(input.Default) : null;
    }
}
