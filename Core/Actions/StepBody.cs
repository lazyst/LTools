using System.Collections.Generic;

namespace LTools.Core.Actions;

/// <summary>
/// 「这一步能不能带子步骤」的唯一判定（P4）：引擎、校验器、编辑器保存、步骤文本都调这里。
/// </summary>
/// <remarks>
/// <para>
/// 以前这三处各写一半：编辑器只能按 <see cref="BodyKind"/> 建子步骤（所以 UI 里不会有脏数据），
/// 但手改 JSON / AI 写的动作可以塞进来，而引擎会**静默忽略**它们——点一次运行什么都不发生，
/// 用户完全看不出「为什么里面那几步没跑」。
/// </para>
/// <para>
/// 规则只有三条：<see cref="BodyKind.none"/> 一个子列表都不能有；<see cref="BodyKind.body"/>
/// 只认 <c>body</c>；<see cref="BodyKind.thenElse"/> 只认 <c>then</c> / <c>else</c>。
/// 未知动作单元不判定（照读照写，交给「未知的动作单元」那条错误）。
/// </para>
/// </remarks>
internal static class StepBody
{
    /// <summary>这一步带了声明之外的子列表吗？返回给人看的说明（带位置前缀），没有就是 null。</summary>
    public static string? Problem(Step step, UnitSpec spec)
    {
        int body = step.Body.Count;
        int then = step.Then.Count;
        int otherwise = step.Else.Count;

        switch (spec.Body)
        {
            case BodyKind.none:
            {
                int stray = body + then + otherwise;
                if (stray == 0) return null;
                return $"「{spec.Name}」不带子步骤，这一步下面的 {stray} 步不会执行（分支 / 循环请用「如果」或循环步骤）";
            }

            case BodyKind.body:
            {
                int stray = then + otherwise;
                if (stray == 0) return null;
                return $"「{spec.Name}」只有循环体，没有「满足时 / 否则」分支，这里有 {stray} 步没地方执行";
            }

            default:
            {
                if (body == 0) return null;
                return $"「{spec.Name}」用「满足时 / 否则」两个分支，不认循环体，这里有 {body} 步没地方执行";
            }
        }
    }

    /// <summary>同上，按注册表解析动作单元；未知单元不判定（返回 null）。</summary>
    public static string? Problem(Step step)
    {
        if (!StepRegistry.Default.TryGet(step.Unit, out var unit)) return null;
        return Problem(step, unit.Spec);
    }

    /// <summary>递归找第一处不合规的子步骤，返回「第 2 行 › 循环体 › 第 1 行」这样的位置 + 说明。</summary>
    public static string? FirstProblem(IReadOnlyList<Step> steps) => FirstProblem(steps, "");

    private static string? FirstProblem(IReadOnlyList<Step> steps, string prefix)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            string label = prefix.Length == 0 ? $"第 {i + 1} 行" : $"{prefix} › 第 {i + 1} 行";

            string? problem = Problem(step);
            if (problem != null) return $"{label}：{problem}";

            foreach (var (list, branch) in Branches(step))
            {
                string? nested = FirstProblem(list, $"{label} › {branch}");
                if (nested != null) return nested;
            }
        }

        return null;
    }

    /// <summary>这种步骤实际支持哪几个子列表（顺序固定，报错时用它的中文名）。</summary>
    private static IEnumerable<(IReadOnlyList<Step> Steps, string Branch)> Branches(Step step)
    {
        if (step.Body.Count > 0) yield return (step.Body, "循环体");
        if (step.Then.Count > 0) yield return (step.Then, "满足时");
        if (step.Else.Count > 0) yield return (step.Else, "否则");
    }
}
