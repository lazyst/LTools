using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

internal enum DiffKind
{
    added,
    removed,
    changed,
}

/// <summary>差异的一行：<c>Depth</c> 决定缩进，<c>Kind</c> 决定符号与颜色。</summary>
internal sealed record DiffLine(DiffKind Kind, int Depth, string Text);

/// <summary>
/// 两份 <see cref="ActionProgram"/> 的「给人看」差异：变量 + 步骤（参数 / 输出 / 失败策略 / 子步骤）。
/// </summary>
/// <remarks>
/// <para>
/// 两个用途同一套实现：草稿采纳前（草稿 ↔ 来源动作）与历史版本恢复前（历史 ↔ 当前）。
/// 文本复用 <see cref="StepSummary"/>，所以「单击」「拖动」这类枚举翻的还是同一份中文名。
/// </para>
/// <para>
/// 步骤的<b>对齐规则是「动作单元相同即视为同一步」</b>（LCS）：只改参数不会被报成「删一步再加一步」，
/// 中间插入一步也不会让后面全部「变红」。两个同动作单元的步骤前后调换，可能被报成参数变化——
/// 这是可接受的代价，别为此引入更复杂的匹配。
/// </para>
/// </remarks>
internal static class ProgramDiff
{
    public static List<DiffLine> Compare(ActionProgram? before, ActionProgram? after)
    {
        var lines = new List<DiffLine>();
        CompareVariables(before?.Variables, after?.Variables, lines);
        CompareSteps(before?.Steps ?? new List<Step>(), after?.Steps ?? new List<Step>(), 0, lines);
        return lines;
    }

    /// <summary>按「行」统计（不是按步骤）：新增 / 删除 / 修改各多少处。</summary>
    public static (int Added, int Removed, int Changed) Count(IEnumerable<DiffLine> lines)
    {
        int added = 0, removed = 0, changed = 0;
        foreach (DiffLine line in lines)
        {
            switch (line.Kind)
            {
                case DiffKind.added: added++; break;
                case DiffKind.removed: removed++; break;
                default: changed++; break;
            }
        }

        return (added, removed, changed);
    }

    // ============ 变量 ============

    private static void CompareVariables(List<VariableDef>? before, List<VariableDef>? after,
        List<DiffLine> lines)
    {
        var old = before ?? new List<VariableDef>();
        var now = after ?? new List<VariableDef>();

        foreach (VariableDef variable in old)
        {
            if (now.Any(v => SameKey(v.Key, variable.Key))) continue;
            lines.Add(new DiffLine(DiffKind.removed, 0, $"变量「{variable.Key}」"));
        }

        foreach (VariableDef variable in now)
        {
            VariableDef? previous = old.FirstOrDefault(v => SameKey(v.Key, variable.Key));
            if (previous == null)
            {
                lines.Add(new DiffLine(DiffKind.added, 0, $"变量「{variable.Key}」（{ValueKindText.Of(variable.Type)}）"));
                continue;
            }

            if (previous.Type != variable.Type)
                lines.Add(new DiffLine(DiffKind.changed, 0,
                    $"变量「{variable.Key}」：类型 {ValueKindText.Of(previous.Type)} → {ValueKindText.Of(variable.Type)}"));

            string was = previous.Default?.AsText() ?? "";
            string isNow = variable.Default?.AsText() ?? "";
            if (!string.Equals(was, isNow, StringComparison.Ordinal))
                lines.Add(new DiffLine(DiffKind.changed, 0,
                    $"变量「{variable.Key}」：默认值「{StepSummary.Quote(was)}」→「{StepSummary.Quote(isNow)}」"));

            if (previous.Direction != variable.Direction)
                lines.Add(new DiffLine(DiffKind.changed, 0,
                    $"变量「{variable.Key}」：方向 {DirectionName(previous.Direction)} → {DirectionName(variable.Direction)}"));
        }
    }

    // ============ 步骤 ============

    private static void CompareSteps(List<Step> before, List<Step> after, int depth, List<DiffLine> lines)
    {
        List<(int Before, int After)> pairs = Align(before, after);

        int x = 0, y = 0;
        foreach (var (bx, ay) in pairs)
        {
            for (; x < bx; x++) AddStepLine(before[x], depth, DiffKind.removed, lines);
            for (; y < ay; y++) AddStepLine(after[y], depth, DiffKind.added, lines);

            CompareStep(before[bx], after[ay], depth, lines);
            x = bx + 1;
            y = ay + 1;
        }

        for (; x < before.Count; x++) AddStepLine(before[x], depth, DiffKind.removed, lines);
        for (; y < after.Count; y++) AddStepLine(after[y], depth, DiffKind.added, lines);
    }

    /// <summary>同一步（动作单元相同）的字段级差异 + 子步骤递归。</summary>
    private static void CompareStep(Step before, Step after, int depth, List<DiffLine> lines)
    {
        string title = StepSummary.Title(after);
        var spec = StepRegistry.Default.TryGet(after.Unit, out var unit) ? unit.Spec : null;

        foreach (string key in ArgKeys(before, after))
        {
            string was = before.In.TryGetValue(key, out StepArg? a) ? StepSummary.ArgText(a) : "（未填）";
            string isNow = after.In.TryGetValue(key, out StepArg? b) ? StepSummary.ArgText(b) : "（未填）";
            if (string.Equals(was, isNow, StringComparison.Ordinal)) continue;

            string label = spec?.Inputs.FirstOrDefault(i => SameKey(i.Key, key))?.Label ?? key;
            lines.Add(new DiffLine(DiffKind.changed, depth, $"{title}：{label} {was} → {isNow}"));
        }

        foreach (string key in before.Out.Keys.Union(after.Out.Keys, StringComparer.OrdinalIgnoreCase))
        {
            bool hasOld = before.Out.TryGetValue(key, out string? was);
            bool hasNew = after.Out.TryGetValue(key, out string? isNow);
            if (hasOld == hasNew && string.Equals(was, isNow, StringComparison.Ordinal)) continue;

            lines.Add(new DiffLine(DiffKind.changed, depth,
                $"{title}：输出 {key} → {(hasNew ? isNow : "（不写）")}" +
                (hasOld ? $"（原为 {was}）" : "")));
        }

        if (before.OnFail != after.OnFail)
            lines.Add(new DiffLine(DiffKind.changed, depth,
                $"{title}：失败策略 {FailName(before.OnFail)} → {FailName(after.OnFail)}"));

        if (before.DelayMs != after.DelayMs)
            lines.Add(new DiffLine(DiffKind.changed, depth,
                $"{title}：延迟 {before.DelayMs} → {after.DelayMs} 毫秒"));

        if (before.Disabled != after.Disabled)
            lines.Add(new DiffLine(DiffKind.changed, depth,
                $"{title}：{(after.Disabled ? "改成跳过" : "恢复执行")}"));

        if (!string.Equals(before.Note ?? "", after.Note ?? "", StringComparison.Ordinal))
            lines.Add(new DiffLine(DiffKind.changed, depth,
                $"{title}：备注「{StepSummary.Quote(before.Note)}」→「{StepSummary.Quote(after.Note)}」"));

        CompareSteps(before.Then, after.Then, depth + 1, lines);
        CompareSteps(before.Else, after.Else, depth + 1, lines);
        CompareSteps(before.Body, after.Body, depth + 1, lines);
    }

    private static void AddStepLine(Step step, int depth, DiffKind kind, List<DiffLine> lines)
    {
        int children = step.SelfAndDescendants().Count() - 1;
        string summary = StepSummary.Describe(step);
        string text = StepSummary.Title(step)
                      + (summary.Length > 0 ? $"（{summary}）" : "")
                      + (children > 0 ? $" · 含 {children} 步" : "");
        lines.Add(new DiffLine(kind, depth, text));
    }

    /// <summary>按「动作单元相同」做 LCS 对齐，返回配成对的 (旧下标, 新下标)。</summary>
    private static List<(int Before, int After)> Align(List<Step> before, List<Step> after)
    {
        int n = before.Count, m = after.Count;
        var pairs = new List<(int, int)>();
        if (n == 0 || m == 0) return pairs;

        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = SameUnit(before[i], after[j])
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (SameUnit(before[x], after[y]))
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    private static IEnumerable<string> ArgKeys(Step before, Step after)
    {
        foreach (string key in before.In.Keys)
            yield return key;

        foreach (string key in after.In.Keys)
            if (!before.In.ContainsKey(key)) yield return key;
    }

    private static bool SameUnit(Step a, Step b) =>
        string.Equals(a.Unit, b.Unit, StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string DirectionName(VariableDirection direction) => direction switch
    {
        VariableDirection.input => "输入",
        VariableDirection.output => "输出",
        _ => "本地",
    };

    private static string FailName(FailMode mode) => mode switch
    {
        FailMode.@continue => "继续",
        _ => "停止",
    };
}
