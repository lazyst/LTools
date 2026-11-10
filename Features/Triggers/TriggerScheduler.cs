using System;
using System.Collections.Generic;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 自动触发的后台排程器：一条 <see cref="System.Threading.Timer"/> 每秒检查一次到期规则，
/// 不碰 WPF、不用 DispatcherTimer 干重活（见 <c>docs/AUTO_TRIGGER.md</c> §6）。
/// </summary>
/// <remarks>
/// <para>
/// 一期只排「定时（固定间隔 / 每天时刻）」与「启动后（延迟一次性）」两类；事件源由
/// <see cref="ITriggerSource"/> 投递，不进这张表。<see cref="TriggerRegistry.Active"/> 已按优先级排好，
/// 到期列表沿用该顺序。
/// </para>
/// <para>
/// <c>SkipFurther</c>（命中后不再判断后续规则）是一期不参与：定时 / 启动后各自独立到期，
/// Quicker 也把「定时重复」列为不适合该选项的事件源（规格待决项 3），留给二期事件源再定。
/// </para>
/// </remarks>
internal sealed class TriggerScheduler
{
    private sealed class Entry
    {
        public required TriggerRule Rule { get; init; }
        public DateTime Next { get; set; }
        public bool OneShot { get; init; }   // 启动后：命中一次即从表移除
    }

    private const int TickMs = 1000;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private System.Threading.Timer? _timer;
    private Action<TriggerRule>? _fire;

    // 本次进程的启动状态（进程级常量）：「启动后」规则的两个启动选项按它们过滤。
    private readonly bool _firstStartToday;
    private readonly bool _launchedByAutoStart;

    public TriggerScheduler(bool firstStartToday, bool launchedByAutoStart)
    {
        _firstStartToday = firstStartToday;
        _launchedByAutoStart = launchedByAutoStart;
    }

    /// <summary>启动排程；<paramref name="fire"/> 是统一投递回调。</summary>
    public void Start(Action<TriggerRule> fire)
    {
        _fire = fire;
        Rebuild();
        _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
    }

    /// <summary>停止排程：Dispose Timer 不等后台回调（别在 UI 线程上等关停）。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _fire = null;
        lock (_gate) _entries.Clear();
    }

    /// <summary>按当前规则表重算所有下次触发时间（设置页保存 / 导入备份后调用）。</summary>
    public void Rebuild()
    {
        var now = DateTime.Now;
        var list = new List<Entry>();
        foreach (var rule in TriggerRegistry.Active)
        {
            // 「启动后」的两个启动选项在本次启动不满足时整条不排程（不是命中时再丢）。
            if (!StartupAllowed(rule)) continue;
            var next = FirstDue(rule, now);
            if (next == null) continue;
            list.Add(new Entry
            {
                Rule = rule,
                Next = next.Value,
                OneShot = rule.Kind == TriggerKind.startup,
            });
        }
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(list);
        }
    }

    /// <summary>
    /// 「启动后」规则的两个启动选项：本次启动不满足就整条不排程。
    /// 两条件同时勾选时是 AND（都要满足）；定时 / 事件规则不受影响。
    /// </summary>
    private bool StartupAllowed(TriggerRule r)
    {
        if (r.Kind != TriggerKind.startup) return true;
        if (r.OnlyFirstStartToday && !_firstStartToday) return false;
        if (r.OnlyOnAutoStart && !_launchedByAutoStart) return false;
        return true;
    }

    /// <summary>一条规则相对 <paramref name="now"/> 的下次触发时间；无有效时间返回 null（不排程）。</summary>
    private static DateTime? FirstDue(TriggerRule r, DateTime now)
    {
        switch (r.Kind)
        {
            case TriggerKind.startup:
                return now.AddSeconds(Math.Max(0, r.DelaySeconds));

            case TriggerKind.timer:
                if (r.IntervalMs is > 0) return now.AddMilliseconds(r.IntervalMs.Value);
                if (TryDaily(r.DailyAt, out int hh, out int mm))
                {
                    DateTime today = now.Date.AddHours(hh).AddMinutes(mm);
                    return today > now ? today : today.AddDays(1);
                }
                // 只有 Cron（一期不做，规格待决项 1）或没填任何时间：不排程
                return null;

            default:
                return null;   // 事件源由 ITriggerSource 投递
        }
    }

    /// <summary>命中后推进到再下一次（一次性规则不走这里）。</summary>
    private static DateTime Advance(TriggerRule r, DateTime now)
    {
        if (r.IntervalMs is > 0) return now.AddMilliseconds(r.IntervalMs.Value);
        if (TryDaily(r.DailyAt, out int hh, out int mm))
        {
            DateTime today = now.Date.AddHours(hh).AddMinutes(mm);
            return today > now ? today : today.AddDays(1);
        }
        return now.AddDays(1);   // 兜底：FirstDue 已排除这种规则
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var due = new List<Entry>();
        lock (_gate)
        {
            if (_timer == null) return;
            foreach (var e in _entries)
                if (now >= e.Next) due.Add(e);
        }
        if (due.Count == 0) return;

        foreach (var e in due)
        {
            lock (_gate)
            {
                if (!_entries.Contains(e)) continue;   // 期间被 Rebuild 换掉
                if (e.OneShot) _entries.Remove(e);
                else e.Next = Advance(e.Rule, now);
            }
            try { _fire?.Invoke(e.Rule); }
            catch (Exception ex) { AppLog.Warn("Triggers", $"调度投递失败：{ex.Message}"); }
        }
    }

    /// <summary>解析 <c>HH:mm</c>（24 小时本地时间）。</summary>
    private static bool TryDaily(string? text, out int hh, out int mm)
    {
        hh = mm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hh) || !int.TryParse(parts[1], out mm)) return false;
        return hh is >= 0 and <= 23 && mm is >= 0 and <= 59;
    }
}
