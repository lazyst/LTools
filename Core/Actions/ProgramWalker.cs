namespace LTools.Core.Actions;

/// <summary>
/// 遍历一个动作体（<see cref="ActionProgram"/>）里的全部步骤，以及带位置标签的步骤清单。
/// </summary>
/// <remarks>
/// 步骤不只是在 <see cref="ActionProgram.Steps"/> 里：参数里的内联取值单元也是步骤
/// （<see cref="StepArg.InlineStep"/>）。资源回收（模板图 / 宏文件）、引用统计与静态校验
/// 都要看全，漏掉任何一层就会误删还在用的文件。
/// </remarks>
internal static class ProgramWalker
{
    /// <summary>动作体里所有步骤（含嵌套块、内联取值步骤）。</summary>
    public static IEnumerable<Step> AllSteps(ActionProgram? program)
    {
        if (program == null) yield break;
        foreach (var step in Walk(program.Steps)) yield return step;
    }

    /// <summary>一个全局子动作里的所有步骤（含嵌套块、内联取值步骤）。</summary>
    public static IEnumerable<Step> AllSteps(SubActionDef? def)
    {
        if (def == null) yield break;
        foreach (var step in Walk(def.Steps)) yield return step;
    }

    /// <summary>
    /// 按引擎的路径 / 标签规则收集步骤：<c>/steps/2/then/0</c> + <c>第 3 行 › 满足时 › 第 1 行</c>。
    /// 静态校验与「子动作被谁引用」共用这一份，别再各写一套遍历。
    /// </summary>
    public static void CollectSteps(IEnumerable<Step> steps, string pathPrefix, string labelPrefix, int depth,
        List<(Step Step, string Path, string Label, int Depth)> output)
    {
        int index = 0;
        foreach (var step in steps)
        {
            string path = $"{pathPrefix}/{index}";
            string label = labelPrefix.Length == 0 ? $"第 {index + 1} 行" : $"{labelPrefix} › 第 {index + 1} 行";
            output.Add((step, path, label, depth));

            CollectSteps(step.Body, path + "/body", label + " › 循环体", depth + 1, output);
            CollectSteps(step.Then, path + "/then", label + " › 满足时", depth + 1, output);
            CollectSteps(step.Else, path + "/else", label + " › 否则", depth + 1, output);
            index++;
        }
    }

    /// <summary>步骤的某个字面量参数文本；不是字面量（变量 / 内联）时返回 null。</summary>
    public static string? LiteralText(Step step, string key) =>
        step.In.TryGetValue(key, out var arg) && arg.Kind == StepArgKind.literal
            ? arg.Literal.AsText()
            : null;

    private static IEnumerable<Step> Walk(IEnumerable<Step> roots)
    {
        foreach (var root in roots)
        {
            foreach (var step in root.SelfAndDescendants()) yield return step;
            foreach (var inline in WalkInline(root)) yield return inline;
        }
    }

    private static IEnumerable<Step> WalkInline(Step step)
    {
        foreach (var arg in step.In.Values)
        {
            if (arg.Kind != StepArgKind.inline || arg.InlineStep == null) continue;

            foreach (var child in arg.InlineStep.SelfAndDescendants()) yield return child;
            foreach (var nested in WalkInline(arg.InlineStep)) yield return nested;
        }
    }
}
