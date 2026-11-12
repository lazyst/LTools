using System.Collections.Generic;
using System.Linq;
using LTools.Core;

namespace LTools.Config;

/// <summary>触发方式（存英文键；界面显示名由本地表翻译，别往配置里写中文）。</summary>
public enum TriggerKind
{
    /// <summary>按固定间隔 / 每天某时刻 / Cron 运行。</summary>
    timer,

    /// <summary>LTools 启动后延迟运行。</summary>
    startup,

    /// <summary>系统里发生的事（由窗口 / 剪贴板 / 文件三个事件源产生）。</summary>
    @event,
}

/// <summary>
/// 一条自动触发规则（见 <c>docs/AUTO_TRIGGER.md</c> §4）。
/// 三种触发方式（定时 / 启动后 / 事件）共用这一份字段，按类型分流、不按类型分表。
/// </summary>
public sealed class TriggerRule
{
    /// <summary>稳定 Id（界面定位 / 引用回收用）。</summary>
    public string Id { get; set; } = "";

    /// <summary>说明（列表里的显示名）。</summary>
    public string Note { get; set; } = "";

    /// <summary>单条启用；总开关在 <see cref="TriggersFile.Enabled"/>。</summary>
    public bool Enabled { get; set; } = true;

    public TriggerKind Kind { get; set; } = TriggerKind.timer;

    // —— 定时 ——
    /// <summary>固定间隔（毫秒）；与 <see cref="DailyAt"/> / <see cref="Cron"/> 三选一。</summary>
    public int? IntervalMs { get; set; }

    /// <summary>每天 HH:mm（24 小时，本地时区）。</summary>
    public string? DailyAt { get; set; }

    /// <summary>完整 Cron 表达式（5 段或 6 段含秒，见 docs/AUTO_TRIGGER.md §13.8）；与间隔 / 每天时刻三选一。</summary>
    public string? Cron { get; set; }

    // —— 启动后 ——
    /// <summary>启动后延迟秒数。</summary>
    public int DelaySeconds { get; set; }

    /// <summary>仅当每日第一次启动 LTools 时运行。</summary>
    public bool OnlyFirstStartToday { get; set; }

    /// <summary>仅当由开机自启任务拉起时运行。</summary>
    public bool OnlyOnAutoStart { get; set; }

    // —— 事件 ——
    public string? EventType { get; set; }

    /// <summary>事件源各自的参数（窗口切换、剪贴板变化、文件系统变化）。</summary>
    public Dictionary<string, string>? Params { get; set; }

    // —— 目标与条件 ——
    /// <summary>动作 Id 或内置功能引用（<c>id#key=value</c>）；只存引用，不存动作本体。</summary>
    public string ActionRef { get; set; } = "";

    /// <summary>传给动作的参数模板（${键} 插值成 triggerParam，见 docs/AUTO_TRIGGER.md §13.3）。</summary>
    public string? ActionParam { get; set; }

    /// <summary>作用范围；null / 空 = 不过滤。复用快捷键的 <see cref="AppFilter"/>。</summary>
    public AppFilter? Scope { get; set; }

    // —— 执行设置 ——
    /// <summary>命中后延迟执行（毫秒）。</summary>
    public int DelayMs { get; set; }

    /// <summary>防抖：这段时间内重复命中只算一次（毫秒）。</summary>
    public int DebounceMs { get; set; }

    /// <summary>节流：两次执行之间的最短间隔（毫秒）。</summary>
    public int ThrottleMs { get; set; }

    /// <summary>命中后不再判断后续规则（规则顺序 = 优先级）。</summary>
    public bool SkipFurther { get; set; }

    public long? LastEditTimeUtc { get; set; }

    public TriggerRule Clone() => new()
    {
        Id = Id,
        Note = Note,
        Enabled = Enabled,
        Kind = Kind,
        IntervalMs = IntervalMs,
        DailyAt = DailyAt,
        Cron = Cron,
        DelaySeconds = DelaySeconds,
        OnlyFirstStartToday = OnlyFirstStartToday,
        OnlyOnAutoStart = OnlyOnAutoStart,
        EventType = EventType,
        Params = Params == null ? null : new Dictionary<string, string>(Params),
        ActionRef = ActionRef,
        ActionParam = ActionParam,
        Scope = Scope?.Clone(),
        DelayMs = DelayMs,
        DebounceMs = DebounceMs,
        ThrottleMs = ThrottleMs,
        SkipFurther = SkipFurther,
        LastEditTimeUtc = LastEditTimeUtc,
    };
}

/// <summary><c>configs/triggers.json</c>：自动触发规则（见 <c>docs/AUTO_TRIGGER.md</c>）。</summary>
public sealed class TriggersFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    /// <summary>全局总开关，默认关：用户主动开才生效。</summary>
    public bool Enabled { get; set; }

    /// <summary>规则列表（顺序 = 匹配优先级）。</summary>
    public List<TriggerRule> Rules { get; set; } = new();

    public TriggersFile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Enabled = Enabled,
        Rules = (Rules ?? new List<TriggerRule>()).Select(r => r.Clone()).ToList(),
    };

    public void Normalize()
    {
        Rules ??= new List<TriggerRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Rules)
        {
            // Id 是界面定位 / 引用回收 / 调度状态的键：空或重复就地补一个，跟 HotkeyFile.Normalize 一致。
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id)) r.Id = NewId();
            r.Note = (r.Note ?? "").Trim();
            r.ActionRef = (r.ActionRef ?? "").Trim();
            r.Scope?.Normalize();
            if (r.Scope is { IsEmpty: true }) r.Scope = null;   // 空名单 = 不过滤
            if (r.IntervalMs is <= 0) r.IntervalMs = null;
            if (string.IsNullOrWhiteSpace(r.DailyAt)) r.DailyAt = null;
            if (string.IsNullOrWhiteSpace(r.Cron)) r.Cron = null;
            if (string.IsNullOrWhiteSpace(r.EventType)) r.EventType = null;
            if (r.DelaySeconds < 0) r.DelaySeconds = 0;
            if (r.DelayMs < 0) r.DelayMs = 0;
            if (r.DebounceMs < 0) r.DebounceMs = 0;
            if (r.ThrottleMs < 0) r.ThrottleMs = 0;
        }
    }

    /// <summary>生成不与现有规则冲突的新 Id（ULID，见 <c>Core/Ids.cs</c>）。</summary>
    public string NewId() => IdFactory.NewId(id =>
        (Rules ?? new List<TriggerRule>()).Any(r => string.Equals(r.Id, id, System.StringComparison.Ordinal)));
}
