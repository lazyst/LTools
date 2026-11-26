using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LTools.Core;
using LTools.Core.Storage;

namespace LTools.Features;

/// <summary>触发记录的一行（<c>state/trigger-history.jsonl</c>，见 <c>docs/AUTO_TRIGGER.md</c> §13.9）。</summary>
internal sealed class TriggerRecord
{
    public string Time { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Note { get; set; } = "";
    public string EventType { get; set; } = "";   // 空 = 定时 / 启动后
    public bool Ran { get; set; }
    public string Reason { get; set; } = "";
    public long ElapsedMs { get; set; }
}

/// <summary>
/// 触发记录：按时间倒序、上限 <see cref="MaxRecords"/> 条（<see cref="JsonlHistory{T}"/>）。
/// </summary>
/// <remarks>
/// 高频事件（剪贴板 / 文件系统）要限流：同一规则**同一秒**同结果同原因只记一条汇总，
/// 避免一次文件保存产生的十几个 <c>changed</c> 把文件写爆。
/// </remarks>
internal static class TriggerHistory
{
    public const int MaxRecords = 500;

    private static readonly JsonlHistory<TriggerRecord> History = new(
        Path.Combine(AppPaths.StateDir, "trigger-history.jsonl"), MaxRecords, "Triggers", "触发记录");

    /// <summary>当前记录（倒序快照）。UI 只读展示用。</summary>
    public static IReadOnlyList<TriggerRecord> Load() => History.Load();

    /// <summary>记一次命中结果（运行 / 丢弃 + 原因）。后台线程调用，不阻塞。</summary>
    public static void Record(string ruleId, string note, string? eventType,
        bool ran, string reason, long elapsedMs)
    {
        var rec = new TriggerRecord
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            RuleId = ruleId ?? "",
            Note = note ?? "",
            EventType = eventType ?? "",
            Ran = ran,
            Reason = reason ?? "",
            ElapsedMs = elapsedMs,
        };

        History.Edit(items =>
        {
            if (items.Count > 0)
            {
                TriggerRecord head = items[0];
                if (head.RuleId == rec.RuleId && head.Time == rec.Time
                    && head.Ran == rec.Ran && head.Reason == rec.Reason)
                {
                    // 同秒汇总：只更新内存里那条的耗时，不再写盘（避免写放大）。
                    head.ElapsedMs = Math.Max(head.ElapsedMs, rec.ElapsedMs);
                    return false;
                }
            }

            items.Insert(0, rec);
            return true;
        });
    }

    /// <summary>清空（设置页「清空」按钮）。</summary>
    public static void Clear() => History.Edit(items =>
    {
        items.Clear();
        return true;
    });
}
