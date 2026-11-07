namespace LTools.Core.Actions;

/// <summary>
/// 遍历一个动作体（<see cref="ActionProgram"/>）里的全部步骤与子动作。
/// </summary>
/// <remarks>
/// 步骤不只是在 <see cref="ActionProgram.Steps"/> 里：动作内子动作带自己的步骤，
/// 参数里的内联取值单元也是步骤（<see cref="StepArg.InlineStep"/>）。
/// 资源回收（模板图 / 宏文件）与「引用统计」都要看全，漏掉任何一层就会误删还在用的文件。
/// </remarks>
internal static class ProgramWalker
{
    /// <summary>动作体里所有步骤（含动作内子动作、嵌套块、内联取值步骤）。</summary>
    public static IEnumerable<Step> AllSteps(ActionProgram? program)
    {
        if (program == null) yield break;

        foreach (var step in Walk(program.Steps)) yield return step;
        foreach (var sub in program.SubActions)
            foreach (var step in Walk(sub.Steps)) yield return step;
    }

    /// <summary>动作体里所有子动作（含嵌套子动作）。</summary>
    public static IEnumerable<SubActionDef> AllSubActions(ActionProgram? program)
    {
        if (program == null) yield break;
        foreach (var sub in program.SubActions)
            foreach (var def in WalkSubActions(sub))
                yield return def;
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

    private static IEnumerable<SubActionDef> WalkSubActions(SubActionDef sub)
    {
        yield return sub;
        foreach (var child in sub.SubActions)
            foreach (var def in WalkSubActions(child))
                yield return def;
    }
}
