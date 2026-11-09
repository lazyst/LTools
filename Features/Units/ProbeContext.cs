using LTools.Core.Actions;

namespace LTools.Features.Units;

/// <summary>
/// 把执行上下文（<see cref="StepContext"/>）转成取值实现使用的 <see cref="RunContext"/>。
/// </summary>
/// <remarks>
/// <c>Features/Flow</c> 下的取值实现（<c>ScreenshotProbe</c> / <c>OcrProbe</c> /
/// <c>ImageProbe</c> / <c>TextLocator</c> / <c>NoteActions</c>）接收 <see cref="RunContext"/>。
/// 每次调用前把变量摊平同步过去；这些实现只读变量，
/// 因此不必回写（回写反而会把所有变量降级成对象，破坏新值模型的类型）。
/// </remarks>
internal static class ProbeContext
{
    /// <summary>按当前步骤上下文构造取值上下文（变量、取消、光标坐标一致）。</summary>
    public static RunContext Of(StepContext ctx)
    {
        var run = new RunContext(ctx.Token, ctx.CursorX, ctx.CursorY)
        {
            ResourceRoot = ctx.ResourceRoot,
        };
        foreach (var kv in ctx.Scope.Flatten())
            run.Vars[kv.Key] = UnitValues.ToVarValue(kv.Value);
        return run;
    }

    /// <summary>执行一个取值实现，并把结果转成新值。</summary>
    public static Value Evaluate(StepContext ctx, Func<RunContext, VarValue> probe)
    {
        var run = Of(ctx);
        return UnitValues.FromVarValue(probe(run));
    }
}
