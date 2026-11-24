using System.Collections.Generic;
using LTools.Core.Actions;

namespace LTools.Core.Link;

/// <summary>
/// 跨进程试运行的方法名（<c>docs/PROCESS_SPLIT.md</c> §15.4 第 5 步）：编辑器在宿主里，
/// 「▶ 测试」要真注入键鼠、与真实运行共用那把互斥，只能在提权进程里跑。
/// </summary>
/// <remarks>
/// <para>
/// 方向：宿主发 <see cref="Program"/>（请求，答复 = 有没有启动）、<see cref="Stop"/>（请求）；
/// 执行器把轨迹与结果按**事件**推回来（<see cref="EventTrace"/> / <see cref="EventSlow"/> /
/// <see cref="EventDone"/>），全部带 <c>Token</c>——宿主点一次就跑一次，旧运行的残事件要能丢掉。
/// </para>
/// <para>
/// 轨迹是按批送的（执行器攒 120 ms），别逐步发：循环几千步的动作会淹没链路。
/// </para>
/// </remarks>
internal static class RunMethods
{
    /// <summary>跑一份内存里的程序（编辑器试运行）。</summary>
    public const string Program = "run.program";

    /// <summary>停止当前运行（与停止热键同一个出口）。</summary>
    public const string Stop = "run.stop";

    /// <summary>一批步骤轨迹。</summary>
    public const string EventTrace = "run.trace";

    /// <summary>某一步跑太久了（编辑器状态行显示「第 N 行已运行 42 秒」）。</summary>
    public const string EventSlow = "run.slow";

    /// <summary>跑完了（成功 / 失败 / 被停）。</summary>
    public const string EventDone = "run.done";
}

/// <summary>
/// 试运行请求：<see cref="Token"/> 由宿主生成，之后的事件都带着它；
/// <see cref="Source"/> 是 <c>RunSource</c> 的名字（只写进运行历史的「来源」列）。
/// </summary>
internal sealed record RunProgramRequest(
    string Token,
    string Name,
    ActionProgram Program,
    string? StartPath,
    string? ResourceRoot,
    bool DryRun,
    string Source);

/// <summary><see cref="Started"/> = false 时 <see cref="Error"/> 说明原因（已有运行 / 宿主没连上）。</summary>
internal sealed record RunProgramReply(bool Started, string? Error);

/// <summary>一批轨迹（<see cref="TraceEntryDto"/> 与 <see cref="StepTrace"/> 一一对应）。</summary>
internal sealed record TraceBatchEvent(string Token, List<TraceEntryDto> Entries);

/// <summary>慢步骤提示。</summary>
internal sealed record RunSlowEvent(string Token, string Path, string Label, string UnitKey, string UnitName, long ElapsedMs);

/// <summary>一次运行的结果（<see cref="ActionRunResult"/> 的可传输形状）。</summary>
internal sealed record RunDoneEvent(string Token, RunResultDto Result);

/// <summary>运行结果：字段与 <c>ActionRunResult</c> 的公开属性一一对应（异常对象本身不过链路）。</summary>
internal sealed record RunResultDto(
    bool Completed,
    bool Stopped,
    string? Error,
    string? FailedStepLabel,
    string ErrorKind,
    int Steps,
    long ElapsedMs,
    Dictionary<string, Value> Variables);

/// <summary>
/// 一步的轨迹（可传输形状）。参数与输出是**展开后**的值，所以 <see cref="Value"/> 要原样过链路
/// （<see cref="LinkJson"/> 里挂了 <c>ValueJsonConverter</c>）。
/// </summary>
internal sealed record TraceEntryDto(
    string Path,
    string Label,
    int Depth,
    string UnitKey,
    string UnitName,
    string Status,
    long ElapsedMs,
    string? Error,
    Dictionary<string, Value> Outputs,
    Dictionary<string, Value> Inputs,
    int Attempt,
    int MaxAttempts)
{
    public static TraceEntryDto From(StepTrace trace) => new(
        trace.Path, trace.Label, trace.Depth, trace.UnitKey, trace.UnitName, trace.Status.ToString(),
        trace.ElapsedMs, trace.Error, new Dictionary<string, Value>(trace.Outputs),
        new Dictionary<string, Value>(trace.Inputs), trace.Attempt, trace.MaxAttempts);

    public StepTrace ToTrace() => new(
        Path, Label, Depth, UnitKey, UnitName,
        System.Enum.TryParse<StepTraceStatus>(Status, ignoreCase: true, out var status) ? status : StepTraceStatus.ok,
        ElapsedMs, Error, Outputs, Inputs, Attempt, MaxAttempts);
}
