using LTools.Core.Actions;

namespace LTools.Features.Units;

/// <summary>
/// 把新执行上下文包装成旧流程上下文（<see cref="RunContext"/>），供尚未搬完的旧实现使用。
/// </summary>
/// <remarks>
/// W2 的过渡层：<c>ScreenshotProbe</c> / <c>OcrProbe</c> /
/// <c>ImageProbe</c> / <c>TextLocator</c> / <c>NoteActions</c>
/// 都接收 <see cref="RunContext"/>。每次调用前把变量摊平同步过去；这些旧实现只读变量，
/// 因此不必回写（回写反而会把所有变量降级成对象，破坏新值模型的类型）。
/// </remarks>
internal static class LegacyFlow
{
    /// <summary>按当前步骤上下文构造旧上下文（变量、取消、光标坐标一致）。</summary>
    public static RunContext Create(StepContext ctx)
    {
        var legacy = new RunContext(ctx.Token, ctx.CursorX, ctx.CursorY)
        {
            ResourceRoot = ctx.ResourceRoot,
        };
        foreach (var kv in ctx.Scope.Flatten())
            legacy.Vars[kv.Key] = UnitValues.ToLegacy(kv.Value);
        return legacy;
    }

    /// <summary>执行一个旧取值实现，并把结果转成新值。</summary>
    public static Value Evaluate(StepContext ctx, Func<RunContext, VarValue> probe)
    {
        var legacy = Create(ctx);
        return UnitValues.FromLegacy(probe(legacy));
    }
}
