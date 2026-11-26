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
            HideOwnWindows = ctx.HideOwnWindows,
        };
        foreach (var kv in ctx.Scope.Flatten())
            run.Vars[kv.Key] = UnitValues.ToVarValue(kv.Value);
        return run;
    }

    /// <summary>执行一个取值实现，并把结果转成新值。</summary>
    public static Value Evaluate(StepContext ctx, Func<RunContext, VarValue> probe) =>
        UnitValues.FromVarValue(Run(ctx, probe));

    /// <summary>
    /// 执行一个取值实现，保留它自己的结果类型（多命中列表这类结构化结果用）。
    /// 异常翻译与 <see cref="Evaluate"/> 同口径，别在动作单元里各写一遍 try/catch。
    /// </summary>
    public static T Run<T>(StepContext ctx, Func<RunContext, T> probe)
    {
        var run = Of(ctx);
        try
        {
            return probe(run);
        }
        catch (FlowAbortException ex)
        {
            // 取值实现里按停止热键会抛它：翻译成引擎认识的「已停止」，别记成步骤失败（也不进重试）。
            throw new ActionAbortedException(ex.Message, ex.UserCancelled);
        }
        catch (FlowException ex)
        {
            // 取值实现的用户可读错误统一成 ActionException：提示文案与「失败继续」口径一致。
            throw new ActionException(ex.Message, ex);
        }
    }
}
