using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Core;

/// <summary>这次运行是谁发起的（只用于运行历史的「来源」列，不参与任何判定）。</summary>
public enum RunSource
{
    /// <summary>手动（托盘 / 其它没有更具体来源的入口）。</summary>
    Manual,

    /// <summary>面板磁贴。</summary>
    Panel,

    /// <summary>快捷键绑定。</summary>
    Hotkey,

    /// <summary>桌面悬浮按钮。</summary>
    Floating,

    /// <summary>自动触发（定时 / 事件）。</summary>
    Trigger,

    /// <summary>动作编辑器试运行。</summary>
    Editor,

    /// <summary>AI 工具面（MCP <c>ltools_staging_run</c>）。</summary>
    Mcp,

    /// <summary>子动作库试运行。</summary>
    SubAction,
}

/// <summary>运行历史的一行（<c>state/run-history.jsonl</c>，见 <c>docs/ACTION_EDITOR_REVIEW.md</c> §6.17）。</summary>
internal sealed class RunRecord
{
    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>（本地时间）。</summary>
    public string Time { get; set; } = "";

    /// <summary>本次运行的短 id（与日志行首的 <c>[run-3f2a]</c> 同一个）。</summary>
    public string RunId { get; set; } = "";

    public string Action { get; set; } = "";

    /// <summary>来源键（<see cref="RunHistory.SourceKey"/>，如 <c>panel</c>）。</summary>
    public string Source { get; set; } = "";

    /// <summary><c>ok</c> / <c>failed</c> / <c>stopped</c>。</summary>
    public string Status { get; set; } = "";

    public long ElapsedMs { get; set; }

    /// <summary>执行到第几步（与轨迹条数不同：循环只算一轮）。</summary>
    public int Steps { get; set; }

    /// <summary>失败位置的人话标签（<c>第 3 行 › 否则 › 第 1 行</c>）。</summary>
    public string Step { get; set; } = "";

    /// <summary>失败那一步的路径（<c>/steps/2/then/0</c>）。</summary>
    public string Path { get; set; } = "";

    /// <summary>失败那一步的动作单元名（失败步骤分布按它汇总）。</summary>
    public string Unit { get; set; } = "";

    public string Error { get; set; } = "";
}

/// <summary>失败步骤分布的一组：同一个动作的同一步骤失败了几次。</summary>
public sealed class FailureGroup
{
    public string Action { get; init; } = "";
    public string Step { get; init; } = "";
    public string Unit { get; init; } = "";
    public int Count { get; init; }

    /// <summary>给人看的一行：<c>「截图并识字」第 3 行「文字点击」</c>。</summary>
    public string Describe => Step.Length == 0
        ? $"「{Action}」（没走到步骤就失败）"
        : $"「{Action}」{Step}" + (Unit.Length == 0 ? "" : $"「{Unit}」");
}

/// <summary>一段运行记录的统计（纯计算结果，界面直接显示）。</summary>
public sealed class RunStats
{
    public int Total { get; init; }
    public int Ok { get; init; }
    public int Failed { get; init; }
    public int Stopped { get; init; }
    public long AverageMs { get; init; }
    public long SlowestMs { get; init; }

    /// <summary>失败步骤分布（次数多的在前，最多 <see cref="RunHistory.DistributionTop"/> 组）。</summary>
    public IReadOnlyList<FailureGroup> Failures { get; init; } = Array.Empty<FailureGroup>();
}

/// <summary>
/// 运行历史：每次运行结束后往 <c>state/run-history.jsonl</c> 记一行（时间 / 动作 / 来源 / 结果 /
/// 耗时 / 失败位置），并在设置页给出统计与失败步骤分布（P2-2）。
/// </summary>
/// <remarks>
/// <para>
/// 以前只有一份 <c>logs/last-failure.log</c>：它答的是「这一次为什么失败」，
/// 答不了「最近老在哪儿失败」「这个动作多久跑一次、要跑多久」。
/// 历史是**薄**记录（几百字节一行），完整轨迹仍然只在失败报告里，别把两件事混成一份。
/// </para>
/// <para>
/// 写盘照 <see cref="TriggerHistory"/>：整份重写（<c>.tmp</c> 再替换）、上限
/// <see cref="MaxRecords"/> 条、按时间倒序。放在 <c>state/</c>（备份域），
/// **别放 <c>logs/</c>**——那里只认 <c>ltools-*</c> / <c>action-*</c> 且会被
/// <c>LogFiles.Roll</c> 滚动清理。
/// </para>
/// <para>
/// 干跑（dry-run）不记：它不注入键鼠、不代表「真的跑过一次」，混进来会把统计搅乱。
/// </para>
/// </remarks>
internal static class RunHistory
{
    /// <summary>最多保留多少条（再多就丢最旧的）。</summary>
    public const int MaxRecords = 500;

    /// <summary>失败步骤分布最多列几组。</summary>
    public const int DistributionTop = 8;

    /// <summary>统计窗口：默认看最近多少次（设置页显示与「复制摘要」共用一个口径）。</summary>
    public const int StatsWindow = 50;

    /// <summary>错误文本的存盘上限（几百字节够定位，几百 KB 的异常栈不进历史）。</summary>
    private const int ErrorLimit = 400;

    private static readonly object Gate = new();
    private static readonly List<RunRecord> _items = new();   // 倒序：最新在前
    private static bool _loaded;

    /// <summary>历史文件的路径（设置页「打开目录」按它所在目录走）。</summary>
    public static string FilePath => Path.Combine(AppPaths.StateDir, "run-history.jsonl");

    /// <summary>当前记录（倒序快照）。界面只读展示用。</summary>
    public static IReadOnlyList<RunRecord> Load()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _items.ToArray();
        }
    }

    /// <summary>
    /// 记一次运行（后台线程在 <c>AutomationRunner.RunCore</c> 的 finally 里调用）。
    /// 写不出来只记一条警告，绝不影响运行结果。
    /// </summary>
    /// <param name="result">引擎结果；启动阶段就崩了时为 null（那时 <paramref name="fatalError"/> 有值）。</param>
    public static void Record(string runId, string action, RunSource source, ActionRunResult? result,
        IReadOnlyList<StepTrace> traces, long elapsedMs, int steps, string? fatalError = null)
    {
        try
        {
            StepTrace? failed = LastFailed(traces);
            var rec = new RunRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                RunId = runId ?? "",
                Action = action ?? "",
                Source = SourceKey(source),
                Status = StatusOf(result),
                ElapsedMs = result != null ? (long)result.Elapsed.TotalMilliseconds : elapsedMs,
                Steps = result?.Steps ?? steps,
                Step = result?.FailedStepLabel ?? failed?.Label ?? "",
                Path = failed?.Path ?? "",
                Unit = failed?.UnitName ?? "",
                Error = Limit(result?.Error ?? fatalError ?? ""),
            };

            lock (Gate)
            {
                EnsureLoaded();
                _items.Insert(0, rec);
                if (_items.Count > MaxRecords) _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
                WriteAll();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", "写运行历史失败：" + ex.Message);
        }
    }

    /// <summary>清空（设置页「清空」按钮；只删历史，不动失败报告与日志）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _items.Clear();
            _loaded = true;
            WriteAll();
        }
    }

    /// <summary>统计 + 失败步骤分布（纯函数：给一组记录就能算，冒烟直接喂假数据）。</summary>
    public static RunStats Summarize(IReadOnlyList<RunRecord> records)
    {
        if (records.Count == 0) return new RunStats();

        int ok = 0, failed = 0, stopped = 0;
        long total = 0, slowest = 0;
        foreach (var r in records)
        {
            switch (r.Status)
            {
                case "ok": ok++; break;
                case "stopped": stopped++; break;
                default: failed++; break;
            }
            total += r.ElapsedMs;
            if (r.ElapsedMs > slowest) slowest = r.ElapsedMs;
        }

        var groups = records
            .Where(r => r.Status != "ok" && r.Status != "stopped")
            .GroupBy(r => (r.Action, r.Step, r.Unit))
            .Select(g => new FailureGroup
            {
                Action = g.Key.Action,
                Step = g.Key.Step,
                Unit = g.Key.Unit,
                Count = g.Count(),
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Action, StringComparer.Ordinal)
            .ThenBy(g => g.Step, StringComparer.Ordinal)
            .Take(DistributionTop)
            .ToList();

        return new RunStats
        {
            Total = records.Count,
            Ok = ok,
            Failed = failed,
            Stopped = stopped,
            AverageMs = total / records.Count,
            SlowestMs = slowest,
            Failures = groups,
        };
    }

    /// <summary>
    /// 一段可直接贴给 AI 或提 issue 的 markdown：统计 + 失败分布 + 最近若干条明细。
    /// </summary>
    public static string BuildSummaryText(IReadOnlyList<RunRecord> records, int take = StatsWindow)
    {
        var recent = records.Take(Math.Max(take, 0)).ToArray();
        var stats = Summarize(recent);
        var sb = new StringBuilder();

        sb.AppendLine($"## LTools 运行历史（最近 {recent.Length} 次）");
        if (recent.Length == 0)
        {
            sb.AppendLine("（还没有运行过）");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"- 成功 {stats.Ok} · 失败 {stats.Failed} · 停止 {stats.Stopped} · " +
                      $"平均 {stats.AverageMs} ms · 最慢 {stats.SlowestMs} ms");

        if (stats.Failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 失败步骤分布");
            foreach (var g in stats.Failures) sb.AppendLine($"- {g.Count} 次 · {g.Describe}");
        }

        sb.AppendLine();
        sb.AppendLine("### 明细（最新在前）");
        foreach (var r in recent)
        {
            sb.Append("- ").Append(r.Time).Append(" · ").Append(StatusText(r.Status))
              .Append(" · ").Append(r.ElapsedMs).Append(" ms · 「").Append(r.Action).Append('」')
              .Append(" · ").Append(SourceLabel(r.Source));
            if (r.Step.Length > 0) sb.Append(" · ").Append(r.Step);
            if (r.Error.Length > 0) sb.Append(" · ").Append(r.Error);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ============ 纯判定（冒烟直接调，别把语义埋进 UI） ============

    /// <summary>结果状态：引擎没跑起来（启动阶段崩了）也算失败。</summary>
    public static string StatusOf(ActionRunResult? result) =>
        result == null ? "failed"
        : result.Stopped ? "stopped"
        : result.Ok ? "ok"
        : "failed";

    /// <summary>轨迹里最后一条失败的那步（失败步骤分布靠它拿路径与动作单元）。</summary>
    public static StepTrace? LastFailed(IReadOnlyList<StepTrace> traces)
    {
        for (int i = traces.Count - 1; i >= 0; i--)
        {
            if (traces[i].Status == StepTraceStatus.failed) return traces[i];
        }
        return null;
    }

    /// <summary>来源键（存盘用英文键，显示另走 <see cref="SourceLabel"/>）。</summary>
    public static string SourceKey(RunSource source) => source switch
    {
        RunSource.Panel => "panel",
        RunSource.Hotkey => "hotkey",
        RunSource.Floating => "floating",
        RunSource.Trigger => "trigger",
        RunSource.Editor => "editor",
        RunSource.Mcp => "mcp",
        RunSource.SubAction => "subaction",
        _ => "manual",
    };

    /// <summary>来源的中文名（表格与摘要共用一份，别在 XAML 里另拼）。</summary>
    public static string SourceLabel(string? key) => key switch
    {
        "panel" => "面板",
        "hotkey" => "快捷键",
        "floating" => "悬浮",
        "trigger" => "自动触发",
        "editor" => "编辑器",
        "mcp" => "AI",
        "subaction" => "子动作",
        _ => "手动",
    };

    /// <summary>状态的中文名。</summary>
    public static string StatusText(string? status) => status switch
    {
        "ok" => "成功",
        "stopped" => "停止",
        _ => "失败",
    };

    // ============ 读写 ============

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
                    var r = JsonSerializer.Deserialize<RunRecord>(line);
                    if (r != null) _items.Add(r);
                }
                catch { /* 坏行跳过，别让一条坏记录毁掉整份历史 */ }
            }
            if (_items.Count > MaxRecords) _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
        }
        catch (Exception ex) { AppLog.Warn("Flow", "读运行历史失败：" + ex.Message); }
    }

    private static void WriteAll()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StateDir);
            string tmp = FilePath + ".tmp";
            var sb = new StringBuilder();
            foreach (var r in _items) sb.AppendLine(JsonSerializer.Serialize(r));
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, FilePath, true);   // 写穿：先 .tmp 再替换，不留半个文件
        }
        catch (Exception ex) { AppLog.Warn("Flow", "写运行历史失败：" + ex.Message); }
    }

    private static string Limit(string text)
    {
        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= ErrorLimit ? flat : flat[..ErrorLimit] + "…";
    }
}
