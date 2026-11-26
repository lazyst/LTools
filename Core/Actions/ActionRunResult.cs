namespace LTools.Core.Actions;

/// <summary>一次运行的结果。失败不抛异常，由调用方按 <see cref="Error"/> 处理。</summary>
public sealed class ActionRunResult
{
    public bool Completed { get; private init; }

    /// <summary>被用户停止 / <c>flow.stop</c> 正常结束。</summary>
    public bool Stopped { get; private init; }

    public string? Error { get; private init; }

    /// <summary>失败发生在哪一行（<c>第 3 行 › 否则 › 第 1 行</c>）；不是步骤失败时为 null。</summary>
    public string? FailedStepLabel { get; private init; }

    /// <summary>失败的性质（P5）：提示文案用；成功 / 停止时为 <see cref="ActionErrorKind.unknown"/>。</summary>
    public ActionErrorKind ErrorKind { get; private init; }

    public Exception? Exception { get; private init; }

    public IReadOnlyDictionary<string, Value> Variables { get; private init; } =
        new Dictionary<string, Value>();

    public int Steps { get; private init; }

    public TimeSpan Elapsed { get; private init; }

    public bool Ok => Completed && !Stopped && Error == null;

    internal static ActionRunResult Success(
        IReadOnlyDictionary<string, Value> variables, int steps, TimeSpan elapsed, bool stopped = false) =>
        new()
        {
            Completed = true,
            Stopped = stopped,
            Variables = variables,
            Steps = steps,
            Elapsed = elapsed,
        };

    internal static ActionRunResult Failure(
        IReadOnlyDictionary<string, Value> variables, int steps, TimeSpan elapsed,
        string error, Exception? exception, bool stopped, string? failedStepLabel = null,
        ActionErrorKind kind = ActionErrorKind.unknown) =>
        new()
        {
            Completed = false,
            Stopped = stopped,
            Variables = variables,
            Steps = steps,
            Elapsed = elapsed,
            Error = error,
            Exception = exception,
            FailedStepLabel = failedStepLabel,
            ErrorKind = kind,
        };
}
