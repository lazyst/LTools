namespace LTools.Core.Actions;

/// <summary>一步的执行结果（调试轨迹用）。</summary>
public enum StepTraceStatus
{
    /// <summary>正常执行完。</summary>
    ok,

    /// <summary>抛错并中止了整个动作。</summary>
    failed,

    /// <summary>抛错但被「失败后继续」吞掉。</summary>
    continued,

    /// <summary>执行途中被停止（停止热键 / <c>flow.stop</c>）。</summary>
    aborted,
}

/// <summary>
/// 一步的执行轨迹：给编辑器「试运行」面板和运行记录用。
/// </summary>
/// <remarks>
/// <see cref="Path"/> 是编辑器里的**步骤路径**（<c>/steps/2/then/0</c>），与运行时步数无关：
/// 循环跑十轮，同一行的路径始终一样，所以面板能直接按它定位到左侧的步骤行。
/// <see cref="Label"/> 是给人看的同义文本（<c>第 3 行 › 否则 › 第 1 行</c>），失败提示里用它。
/// 轨迹在**后台线程**上同步回调，接收方不要在这里做重活（面板会自己攒批再上屏）。
/// </remarks>
public sealed record StepTrace(
    string Path,
    string Label,
    int Depth,
    string UnitKey,
    string UnitName,
    StepTraceStatus Status,
    long ElapsedMs,
    string? Error,
    IReadOnlyDictionary<string, Value> Outputs);
