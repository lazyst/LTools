namespace LTools.Core.Actions;

/// <summary>动作执行中的用户可读错误（配置错误、参数缺失、步骤失败等）。</summary>
public sealed class ActionException : Exception
{
    public ActionException(string message) : base(message) { }
    public ActionException(string message, Exception inner) : base(message, inner) { }

    public ActionException(string message, ActionErrorKind kind) : base(message) => Kind = kind;

    public ActionException(string message, Exception inner, ActionErrorKind kind) : base(message, inner)
        => Kind = kind;

    /// <summary>
    /// 失败的性质（P5）：只用于「重试哪些失败」与提示文案。默认 <see cref="ActionErrorKind.unknown"/>，
    /// 语义明确的地方用 <see cref="ActionErrors.NotFound"/> / <see cref="ActionErrors.Timeout"/> /
    /// <see cref="ActionErrors.Precondition"/> 构造。
    /// </summary>
    public ActionErrorKind Kind { get; } = ActionErrorKind.unknown;
}

/// <summary>中止整个动作（用户停止 / 步数或时间超预算）。</summary>
internal sealed class ActionAbortedException : Exception
{
    public ActionAbortedException(string reason, bool userCancelled = false) : base(reason)
        => UserCancelled = userCancelled;

    /// <summary>是否由用户按停止热键触发。</summary>
    public bool UserCancelled { get; }
}

/// <summary>循环体里的 <c>flow.break</c>。</summary>
internal sealed class BreakSignal : Exception { }

/// <summary>循环体里的 <c>flow.continue</c>。</summary>
internal sealed class ContinueSignal : Exception { }

/// <summary>流程里的 <c>flow.stop</c>：正常结束，不算失败。</summary>
internal sealed class StopSignal : Exception { }

/// <summary>子动作里的 <c>flow.return</c>：结束当前子动作，已赋值的输出照常带回调用方。</summary>
internal sealed class ReturnSignal : Exception { }
