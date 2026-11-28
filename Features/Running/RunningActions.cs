using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LTools.Core;

namespace LTools.Features.Running;

/// <summary>
/// 运行注册表：按 <c>runId</c> 登记每个还在跑的运行（替代原来的 <c>_running</c> 单实例锁）。
/// </summary>
/// <remarks>
/// <para>
/// 键是 <c>runId</c> 而不是动作 Id：同一动作的 2、3 份是不同运行，各自停、各自记日志。
/// <see cref="IsAny"/> 只回答「有没有任何一个在跑」，供 <c>MacroRecorder</c> / 自动触发 /
/// 参数框这些「只关心忙不忙」的调用点用。
/// </para>
/// <para>
/// 只在这里维护「单运行时给别处线程日志」的回退 id（<see cref="AppLog.SetSingleRunFallback"/>）：
/// 有且只有一个运行时，钩子 / 窗口线程打的日志也能带上它；并发时宁可不带，也不能串号。
/// </para>
/// </remarks>
internal static class RunningActions
{
    private static readonly ConcurrentDictionary<string, ActionRunSession> Sessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Copies = new(StringComparer.Ordinal);
    private static int _count;

    /// <summary>是否有任何一个运行在跑（<c>AutomationRunner.IsRunning</c> 的实体）。</summary>
    public static bool IsAny => Volatile.Read(ref _count) > 0;

    public static int Count => Volatile.Read(ref _count);

    /// <summary>同一动作已经跑着几份。</summary>
    public static int CountOf(string actionId) =>
        string.IsNullOrEmpty(actionId) ? 0 : Copies.TryGetValue(actionId, out int n) ? n : 0;

    /// <summary>登记前先领一个副本号（1 起）；<see cref="Remove"/> 时归还。</summary>
    public static int NextCopyIndex(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return 1;
        return Copies.AddOrUpdate(actionId, 1, static (_, n) => n + 1);
    }

    /// <summary>登记一个运行；<c>false</c> = 同一个 <c>runId</c> 已经在册（理论上不会发生）。</summary>
    public static bool TryAdd(ActionRunSession session)
    {
        if (!Sessions.TryAdd(session.RunId, session)) return false;
        Interlocked.Increment(ref _count);
        RefreshLogFallback();
        return true;
    }

    /// <summary>摘掉一个运行（无论成功 / 失败 / 停止）。</summary>
    public static void Remove(ActionRunSession session)
    {
        if (!Sessions.TryRemove(session.RunId, out _)) return;
        Interlocked.Decrement(ref _count);

        if (!string.IsNullOrEmpty(session.ActionId))
        {
            int left = Copies.AddOrUpdate(session.ActionId, 0, static (_, n) => n - 1);
            if (left <= 0) Copies.TryRemove(session.ActionId, out _);
        }

        RefreshLogFallback();
    }

    /// <summary>当前的运行（按开始时间排序的快照；调用方不能改内部集合）。</summary>
    public static IReadOnlyList<ActionRunSession> Snapshot() =>
        Sessions.Values.OrderBy(x => x.StartedTick).ToArray();

    /// <summary>停某一个（按下指示器 / 运行列表里的 ✕）。</summary>
    public static bool Cancel(string runId)
    {
        if (string.IsNullOrEmpty(runId) || !Sessions.TryGetValue(runId, out ActionRunSession? session))
            return false;

        session.StopRequested = true;
        try { session.Cts.Cancel(); } catch { /* 已释放 */ }
        return true;
    }

    /// <summary>停全部（停止热键 / 退出 / 编辑器「停止」）。返回请求停掉的条数。</summary>
    public static int CancelAll()
    {
        int cancelled = 0;
        foreach (ActionRunSession session in Sessions.Values)
        {
            session.StopRequested = true;
            try { session.Cts.Cancel(); } catch { /* 已释放 */ }
            cancelled++;
        }
        return cancelled;
    }

    private static void RefreshLogFallback()
    {
        ActionRunSession[] all = Sessions.Values.ToArray();
        AppLog.SetSingleRunFallback(all.Length == 1 ? all[0].RunId : null);
    }
}
