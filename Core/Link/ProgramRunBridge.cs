using System;
using System.Collections.Generic;
using LTools.Core.Actions;

namespace LTools.Core.Link;

/// <summary>
/// 跨进程试运行的**宿主侧**：把编辑器这次试运行交给提权进程，再把轨迹 / 结果摊回本地回调。
/// </summary>
/// <remarks>
/// <para>
/// 回调签名与 <c>AutomationRunner.TryRunProgram</c> 完全一致，所以编辑器只换调用名，其余不动。
/// 执行器那边一次只接一个运行；这边用 <c>Token</c> 丢掉上一轮迟到的轨迹（点得快的时候会有）。
/// </para>
/// <para>
/// 事件在**链路线程**上到，与执行器「引擎线程」同性质（都不是 UI 线程）：编辑器本来就把
/// 轨迹攒进锁队列、完成 / 慢步骤自己 <c>Dispatcher.BeginInvoke</c>，所以这里不额外排队。
/// </para>
/// </remarks>
internal static class ProgramRunBridge
{
    private const int StartTimeoutMs = 5_000;

    private static LinkServer? _link;
    private static string? _token;
    private static Action<StepTrace>? _trace;
    private static Action<ActionRunResult>? _done;
    private static Action<SlowStepInfo>? _slow;

    /// <summary>宿主启动链路服务端后挂上（与 <c>PanelBridge</c> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>把这次试运行交给执行器；<c>false</c> 时 <paramref name="error"/> 是可显示的原因。</summary>
    public static bool TryStart(string name, ActionProgram program, string? startPath,
        Action<StepTrace>? trace, Action<ActionRunResult>? completed, string? resourceRoot,
        bool dryRun, RunSource source, Action<SlowStepInfo>? slowStep, out string? error)
    {
        error = null;
        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "界面宿主没连上主程序，试运行要主程序在跑";
            return false;
        }

        string token = "ed-" + Guid.NewGuid().ToString("N")[..10];
        _token = token;
        _trace = trace;
        _done = completed;
        _slow = slowStep;

        try
        {
            LinkMessage reply = link.RequestAsync(RunMethods.Program,
                    new RunProgramRequest(token, name, program, startPath, resourceRoot, dryRun, source.ToString()),
                    timeoutMs: StartTimeoutMs)
                .GetAwaiter().GetResult();

            if (reply.Ok && reply.Read<RunProgramReply>() is { Started: true }) return true;

            error = reply.Ok
                ? reply.Read<RunProgramReply>()?.Error ?? "主程序没能启动试运行"
                : reply.Error;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        Clear(token);
        return false;
    }

    /// <summary>停止当前运行（编辑器「停止」按钮）。</summary>
    public static void Stop()
    {
        try { _ = _link?.RequestAsync(RunMethods.Stop, timeoutMs: 3_000); }
        catch (Exception ex) { AppLog.Warn("Run", $"发送停止请求失败：{ex.Message}"); }
    }

    /// <summary>处理执行器推来的试运行事件（链路线程）。</summary>
    public static void OnEvent(LinkMessage message)
    {
        if (message.Method == RunMethods.EventTrace)
        {
            TraceBatchEvent? batch = message.Read<TraceBatchEvent>();
            if (batch == null || batch.Token != _token) return;

            Action<StepTrace>? trace = _trace;
            if (trace == null) return;

            foreach (TraceEntryDto entry in batch.Entries) trace(entry.ToTrace());
            return;
        }

        if (message.Method == RunMethods.EventSlow)
        {
            RunSlowEvent? slow = message.Read<RunSlowEvent>();
            if (slow == null || slow.Token != _token) return;

            _slow?.Invoke(new SlowStepInfo(slow.Path, slow.Label, slow.UnitKey, slow.UnitName,
                TimeSpan.FromMilliseconds(slow.ElapsedMs)));
            return;
        }

        if (message.Method != RunMethods.EventDone) return;

        RunDoneEvent? done = message.Read<RunDoneEvent>();
        if (done == null || done.Token != _token) return;

        string token = done.Token;
        Action<ActionRunResult>? completed = _done;
        Clear(token);
        completed?.Invoke(ToResult(done.Result));
    }

    private static ActionRunResult ToResult(RunResultDto dto)
    {
        var kind = Enum.TryParse<ActionErrorKind>(dto.ErrorKind, ignoreCase: true, out var parsed)
            ? parsed
            : ActionErrorKind.unknown;

        if (dto.Completed)
            return ActionRunResult.Success(dto.Variables, dto.Steps, TimeSpan.FromMilliseconds(dto.ElapsedMs),
                dto.Stopped);

        return ActionRunResult.Failure(dto.Variables, dto.Steps, TimeSpan.FromMilliseconds(dto.ElapsedMs),
            dto.Error ?? "执行失败", exception: null, dto.Stopped, dto.FailedStepLabel, kind);
    }

    private static void Clear(string token)
    {
        if (_token != token) return;
        _token = null;
        _trace = null;
        _done = null;
        _slow = null;
    }
}
