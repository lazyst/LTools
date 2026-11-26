namespace LTools.Core.Actions;

/// <summary>
/// 步骤失败的内部信号：消息里已经带好「第 3 行 › 否则 › 第 1 行」前缀。
/// </summary>
/// <remarks>
/// 单独一个类型是为了让外层步骤区分「自己这一步失败」和「子步骤已经报过位置」——
/// 否则循环 / 分支会把前缀套成「第 2 行「如果」失败：第 2 行 › 满足时 › 第 1 行「提示弹窗」失败：…」。
/// 引擎对外的结果仍然把内层异常放在 <see cref="Exception.InnerException"/> 上。
/// </remarks>
internal sealed class StepFailureException : Exception
{
    public StepFailureException(string message, Exception inner) : base(message, inner)
        => Kind = ActionErrors.KindOf(inner);

    public StepFailureException(string message, Exception inner, ActionErrorKind kind) : base(message, inner)
        => Kind = kind;

    /// <summary>这一步失败的性质（从内层异常带上来，供提示文案与重试条件使用）。</summary>
    public ActionErrorKind Kind { get; }
}

