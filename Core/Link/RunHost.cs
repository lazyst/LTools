using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 跨进程试运行的**执行器侧**：宿主里的编辑器点「▶ 测试」，这里真跑一次、把轨迹按批推回去。
/// </summary>
/// <remarks>
/// <para>
/// 试运行必须在这里跑：它要注入键鼠、和真实运行共用同一把互斥、停止热键也只在这一侧——
/// 宿主那边只有界面。一次只接一个运行（<c>AutomationRunner</c> 本身就是单实例）。
/// </para>
/// <para>
/// 轨迹**攒 120 ms 推一批**（最多 200 条）：循环几千步的动作逐步发会把链路塞满。
/// 结果一定在最后一批之后发（同一个发送队列，顺序保得住），宿主据此收尾。
/// </para>
/// </remarks>
internal static class RunHost
{
    private const int FlushMs = 120;
    private const int MaxBatch = 200;

    private static readonly object Gate = new();
    private static string? _token;
    private static List<TraceEntryDto> _batch = new();
    private static Timer? _timer;

    /// <summary>处理一条请求；不是这两个方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method == RunMethods.Stop)
        {
            try
            {
                // 只停试运行：宿主里的「停止」属于编辑器那一次，别连带停掉用户正在跑的动作。
                LTools.Features.Running.RunCoordinator.CancelTrials();
                return Task.FromResult(LinkMessage.Response(request, new RunProgramReply(true, null)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Run", $"停止试运行失败：{ex.Message}");
                return Task.FromResult(LinkMessage.Response(request, new RunProgramReply(false, ex.Message)));
            }
        }

        if (request.Method != RunMethods.Program) return null;

        RunProgramRequest? req = request.Read<RunProgramRequest>();
        if (req?.Program == null || string.IsNullOrWhiteSpace(req.Token))
            return Task.FromResult(LinkMessage.Response(request, new RunProgramReply(false, "试运行请求缺参数")));

        string token = req.Token;
        lock (Gate)
        {
            if (_token != null)
                return Task.FromResult(LinkMessage.Response(request,
                    new RunProgramReply(false, "已有动作正在运行，先等它结束或按停止")));

            _token = token;
            _batch = new List<TraceEntryDto>();
            _timer = new Timer(_ => Flush(token), null, FlushMs, FlushMs);
        }

        bool started;
        string? error = null;
        try
        {
            started = AutomationRunner.TryRunProgram(req.Name, req.Program, req.StartPath,
                trace => Enqueue(token, trace),
                result => Finish(token, result),
                req.ResourceRoot, req.DryRun, ParseSource(req.Source),
                slow => SendSlow(token, slow));
        }
        catch (Exception ex)
        {
            started = false;
            error = ex.Message;
        }

        if (!started)
        {
            Reset(token);
            return Task.FromResult(LinkMessage.Response(request,
                new RunProgramReply(false, error ?? "已有动作正在运行，先等它结束或按停止")));
        }

        return Task.FromResult(LinkMessage.Response(request, new RunProgramReply(true, null)));
    }

    private static RunSource ParseSource(string? text) =>
        Enum.TryParse<RunSource>(text, ignoreCase: true, out var source) ? source : RunSource.Editor;

    private static void Enqueue(string token, StepTrace trace)
    {
        List<TraceEntryDto>? ready = null;
        lock (Gate)
        {
            if (_token != token) return;   // 上一轮残留
            _batch.Add(TraceEntryDto.From(trace));
            if (_batch.Count >= MaxBatch)
            {
                ready = _batch;
                _batch = new List<TraceEntryDto>();
            }
        }

        if (ready != null) SendTraces(token, ready);
    }

    private static void Flush(string token)
    {
        List<TraceEntryDto>? ready = null;
        lock (Gate)
        {
            if (_token != token || _batch.Count == 0) return;
            ready = _batch;
            _batch = new List<TraceEntryDto>();
        }

        SendTraces(token, ready);
    }

    private static void SendTraces(string token, List<TraceEntryDto> entries)
    {
        try { _ = LinkWorker.NotifyAsync(RunMethods.EventTrace, new TraceBatchEvent(token, entries)); }
        catch (Exception ex) { AppLog.Warn("Run", $"推送试运行轨迹失败：{ex.Message}"); }
    }

    private static void SendSlow(string token, SlowStepInfo info)
    {
        try
        {
            _ = LinkWorker.NotifyAsync(RunMethods.EventSlow,
                new RunSlowEvent(token, info.Path, info.Label, info.UnitKey, info.UnitName,
                    (long)info.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) { AppLog.Warn("Run", $"推送慢步骤失败：{ex.Message}"); }
    }

    private static void Finish(string token, ActionRunResult result)
    {
        // 最后一批先落地：宿主按「先轨迹、后结果」上屏。
        Flush(token);

        var dto = new RunResultDto(result.Completed, result.Stopped, result.Error,
            result.FailedStepLabel, result.ErrorKind.ToString(), result.Steps,
            (long)result.Elapsed.TotalMilliseconds,
            new Dictionary<string, Value>(result.Variables));

        try { _ = LinkWorker.NotifyAsync(RunMethods.EventDone, new RunDoneEvent(token, dto)); }
        catch (Exception ex) { AppLog.Warn("Run", $"推送试运行结果失败：{ex.Message}"); }

        Reset(token);
    }

    private static void Reset(string token)
    {
        Timer? timer;
        lock (Gate)
        {
            if (_token != token) return;
            _token = null;
            _batch = new List<TraceEntryDto>();
            timer = _timer;
            _timer = null;
        }

        try { timer?.Dispose(); } catch { /* 已释放 */ }
    }
}
