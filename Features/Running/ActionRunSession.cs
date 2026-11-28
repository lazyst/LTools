using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Running;

/// <summary>一次运行要做的事（动作 / 试运行共用；参数已经由调用方定好）。</summary>
internal sealed class RunJob
{
    public required ActionProgram Program { get; init; }
    public required string Name { get; init; }
    public string ActionId { get; init; } = "";
    public string? StartPath { get; init; }
    public Action<StepTrace>? Trace { get; init; }
    public bool Notify { get; init; }
    public RunNoticeMode? NoticeOverride { get; init; }
    public Action<ActionRunResult>? Completed { get; init; }
    public string? ResourceRoot { get; init; }
    public IReadOnlyDictionary<string, Value>? InitialVariables { get; init; }
    public bool DryRun { get; init; }
    public RunSource Source { get; init; } = RunSource.Manual;
    public Action<SlowStepInfo>? SlowStep { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }

    /// <summary>运行前要不要问参数（运行开始那一刻才问，排队期间动作可能已经被改）。</summary>
    public bool NeedsAsk { get; init; }
}

/// <summary>
/// 一次可并存的运行：身份（<c>runId</c>）、取消、按住键、线程、显示用的副本号。
/// </summary>
internal sealed class ActionRunSession
{
    public ActionRunSession(RunJob job, string runId, int copyIndex, bool exclusiveInput)
    {
        Job = job;
        RunId = runId;
        CopyIndex = copyIndex;
        ExclusiveInput = exclusiveInput;
        Cts = new CancellationTokenSource();
        StartedTick = Environment.TickCount64;
    }

    public RunJob Job { get; }

    public string RunId { get; }

    /// <summary>同一动作的第几份：1 = 不带后缀，2+ = 显示成「动作名 #2」。</summary>
    public int CopyIndex { get; }

    public RunSource Source => Job.Source;

    public string ActionId => Job.ActionId;

    public string Name => Job.Name;

    /// <summary>显示名（指示器 / toast / 运行列表用）。</summary>
    public string Label => CopyIndex > 1 ? $"{Job.Name} #{CopyIndex}" : Job.Name;

    public CancellationTokenSource Cts { get; }

    /// <summary>本运行「按住不放」的键；收尾只放自己这一份（见 <see cref="InputHoldState"/>）。</summary>
    public InputHoldState Hold { get; } = new();

    /// <summary>试运行 = 整段独占输入。</summary>
    public bool ExclusiveInput { get; }

    public long StartedTick { get; }

    public Thread? Thread { get; set; }

    /// <summary>指示器 / 运行列表显示用（只写一个 int，读不需要锁）。</summary>
    public int Step { get; set; }

    public volatile bool StopRequested;

    /// <summary>这次运行自己的失败报告路径（失败时才有）。</summary>
    public string? ReportPath { get; set; }
}
