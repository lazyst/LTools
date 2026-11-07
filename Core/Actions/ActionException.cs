namespace LTools.Core.Actions;

/// <summary>动作执行中的用户可读错误（配置错误、参数缺失、步骤失败等）。</summary>
public sealed class ActionException : Exception
{
    public ActionException(string message) : base(message) { }
    public ActionException(string message, Exception inner) : base(message, inner) { }
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
