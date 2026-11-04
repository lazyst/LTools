using System.Collections.Generic;
using System.Linq;

namespace LTools.Features;

/// <summary>
/// 旧「组合动作」步骤（<see cref="ActionDto.Steps"/>）→ 新流程（<see cref="ActionDto.Flow"/>）的单向迁移。
/// </summary>
/// <remarks>
/// 旧配置不需要手工改：执行、编辑器打开、备份导入后的首次保存都会走这里。
/// 迁移只做结构映射（每步 → action 节点，保留 DelayMs / OnFail），不猜测用户意图。
/// </remarks>
internal static class FlowMigration
{
    /// <summary>取动作的流程；旧配置自动迁移（不修改传入对象）。</summary>
    public static List<FlowNode> Ensure(ActionDto action) =>
        action.Flow ?? FromSteps(action.Steps);

    /// <summary>把旧步骤列表转换成扁平的 action 节点列表。</summary>
    public static List<FlowNode> FromSteps(List<StepDto>? steps)
    {
        if (steps == null) return new List<FlowNode>();
        return steps.Select(s => new FlowNode
        {
            Kind = FlowNodeKind.action,
            Action = s.Action?.Clone(),
            DelayMs = s.DelayMs,
            OnFail = s.OnFail,
        }).ToList();
    }
}
