using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LTools.Core;

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
/// 触发记录：按时间倒序、上限 <see cref="MaxRecords"/> 条，写穿用 <c>.tmp</c> 替换
/// （照 <c>SubActionStore.Save</c>）。放在 <c>state/</c>（是备份域），
/// **别放 <c>logs/</c>**——那里只认 <c>ltools-*</c> / <c>action-*</c> 且会被 <c>LogFiles.Roll</c> 滚动清理。
/// </summary>
/// <remarks>
/// 高频事件（剪贴板 / 文件系统）要限流：同一规则**同一秒**同结果同原因只记一条汇总，
/// 避免一次文件保存产生的十几个 <c>changed</c> 把文件写爆。
/// </remarks>
internal static class TriggerHistory
{
    public const int MaxRecords = 500;

    private static readonly object Gate = new();
    private static readonly List<TriggerRecord> _items = new();   // 倒序：最新在前
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.StateDir, "trigger-history.jsonl");

    /// <summary>当前记录（倒序快照）。UI 只读展示用。</summary>
    public static IReadOnlyList<TriggerRecord> Load()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _items.ToArray();
        }
    }

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

        lock (Gate)
        {
            EnsureLoaded();
            if (_items.Count > 0)
            {
                var head = _items[0];
                if (head.RuleId == rec.RuleId && head.Time == rec.Time
                    && head.Ran == rec.Ran && head.Reason == rec.Reason)
                {
                    // 同秒汇总：只更新内存里那条的耗时，不再写盘（避免写放大）。
                    head.ElapsedMs = Math.Max(head.ElapsedMs, rec.ElapsedMs);
                    return;
                }
            }
            _items.Insert(0, rec);
            if (_items.Count > MaxRecords) _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
            WriteAll();
        }
    }

    /// <summary>清空（设置页「清空」按钮）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _items.Clear();
            _loaded = true;
            WriteAll();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<TriggerRecord>(line);
                    if (r != null) _items.Add(r);
                }
                catch { /* 坏行跳过，别让一条坏记录毁掉整份历史 */ }
            }
            if (_items.Count > MaxRecords) _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
        }
        catch (Exception ex) { AppLog.Warn("Triggers", "读触发记录失败：" + ex.Message); }
    }

    private static void WriteAll()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = FilePath + ".tmp";
            var sb = new StringBuilder();
            foreach (var r in _items) sb.AppendLine(JsonSerializer.Serialize(r));
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, FilePath, true);   // 写穿：先 .tmp 再替换，不留半个文件
        }
        catch (Exception ex) { AppLog.Warn("Triggers", "写触发记录失败：" + ex.Message); }
    }
}
