using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 采集历史的保留策略（照 Snow Shot 的 <c>capture_history/*</c>）：开关 / 永久保留 /
/// 保留天数 / 条数上限 / 磁盘上限。纯数据 + 归一，读写走 <c>settings.json</c> 的
/// <c>CaptureHistory</c> 段。
/// </summary>
/// <remarks>
/// 上游默认值逐字照 <c>storage/configurationschema.cpp</c>：enabled = true、
/// keep_permanently = false、retention_days = 7、max_entries = 100、max_disk_mib = 1024；
/// 范围照 <c>CaptureHistoryPolicy</c>（1–365 天 / 1–1000 条 / 128–10240 MiB）。
/// 上游的 <c>compression_level</c> 不落成开关：历史资产一律存无损 png（见 §5）。
/// </remarks>
internal sealed record CaptureHistoryPolicy(bool Enabled, bool KeepPermanently, int RetentionDays,
    int MaxEntries, int MaxDiskMiB)
{
    public static readonly CaptureHistoryPolicy Default = new(true, false, 7, 100, 1024);

    /// <summary>磁盘上限（字节）。</summary>
    public long MaxDiskBytes => (long)MaxDiskMiB * 1024 * 1024;

    public bool IsDefault => this == Default;
}

/// <summary>采集历史策略的读写（只写 <c>settings.json</c> 的 <c>CaptureHistory</c> 段）。</summary>
internal static class CaptureHistoryStore
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
    public const int MinEntries = 1;
    public const int MaxEntries = 1000;
    public const int MinDiskMiB = 128;
    public const int MaxDiskMiB = 10240;

    private static readonly object Gate = new();
    private static CaptureHistoryPolicy? _cached;

    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    /// <summary>当前策略（进程内缓存；改完立刻刷新）。</summary>
    public static CaptureHistoryPolicy Current => Snapshot();

    public static bool Enabled => Snapshot().Enabled;
    public static bool KeepPermanently => Snapshot().KeepPermanently;
    public static int RetentionDays => Snapshot().RetentionDays;
    public static int MaxEntryCount => Snapshot().MaxEntries;
    public static int DiskLimitMiB => Snapshot().MaxDiskMiB;

    /// <summary>按范围归一（0 / 负数当「没配」，回默认值）。</summary>
    public static CaptureHistoryPolicy Normalize(bool enabled, bool keepPermanently, int retentionDays,
        int maxEntries, int maxDiskMiB) => new(
        enabled,
        keepPermanently,
        Math.Clamp(retentionDays <= 0 ? CaptureHistoryPolicy.Default.RetentionDays : retentionDays,
            MinRetentionDays, MaxRetentionDays),
        Math.Clamp(maxEntries <= 0 ? CaptureHistoryPolicy.Default.MaxEntries : maxEntries,
            MinEntries, MaxEntries),
        Math.Clamp(maxDiskMiB <= 0 ? CaptureHistoryPolicy.Default.MaxDiskMiB : maxDiskMiB,
            MinDiskMiB, MaxDiskMiB));

    public static void SetEnabled(bool on) => Save(p => p with { Enabled = on });
    public static void SetKeepPermanently(bool on) => Save(p => p with { KeepPermanently = on });
    public static void SetRetentionDays(int days) => Save(p => p with { RetentionDays = days });
    public static void SetMaxEntries(int entries) => Save(p => p with { MaxEntries = entries });
    public static void SetMaxDiskMiB(int mib) => Save(p => p with { MaxDiskMiB = mib });

    /// <summary>外部改了 <c>settings.json</c>（导入备份 / 冒烟换锚点）后调一次。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>改一项：归一 → 刷新缓存 → 只写 settings 域 → 立刻按新策略剪一次。</summary>
    private static void Save(Func<CaptureHistoryPolicy, CaptureHistoryPolicy> change)
    {
        CaptureHistoryPolicy current = Snapshot();
        CaptureHistoryPolicy next = change(current);

        next = Normalize(next.Enabled, next.KeepPermanently, next.RetentionDays, next.MaxEntries,
            next.MaxDiskMiB);

        lock (Gate) _cached = next;

        try
        {
            void Apply(SettingsFile settings)
            {
                settings.CaptureHistory ??= new CaptureHistoryFile();
                settings.CaptureHistory.Enabled = next.Enabled;
                settings.CaptureHistory.KeepPermanently = next.KeepPermanently;
                settings.CaptureHistory.RetentionDays = next.RetentionDays;
                settings.CaptureHistory.MaxEntries = next.MaxEntries;
                settings.CaptureHistory.MaxDiskMiB = next.MaxDiskMiB;
            }

            string path = Anchor;
            if (string.IsNullOrEmpty(path)) SettingsStore.Modify(Apply);
            else SettingsStore.Modify(path, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.History", "保存采集历史策略失败：" + ex.Message);
        }

        // 调小上限 / 关掉永久保留之后立刻生效（不用等下一次截图）
        CaptureCatalog.Maintain();
    }

    private static CaptureHistoryPolicy Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        CaptureHistoryFile file;
        try
        {
            string path = Anchor;
            file = string.IsNullOrEmpty(path)
                ? new CaptureHistoryFile()
                : SettingsStore.Read(path).CaptureHistory ?? new CaptureHistoryFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.History", "读取采集历史策略失败：" + ex.Message);
            file = new CaptureHistoryFile();
        }

        CaptureHistoryPolicy policy = Normalize(file.Enabled, file.KeepPermanently, file.RetentionDays,
            file.MaxEntries, file.MaxDiskMiB);

        lock (Gate) _cached = policy;
        return policy;
    }
}
