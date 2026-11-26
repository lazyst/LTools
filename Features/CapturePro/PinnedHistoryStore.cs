using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图历史的保留策略（照 Snow Shot 的 <c>pinned_history/*</c>）：开关 / 永久保留 /
/// 保留天数 / 条数上限 / 磁盘上限。
/// </summary>
/// <remarks>
/// 默认值沿用 LTools 既有口径（30 天 / 200 条 / 1024 MB），**不与上游的 7 天 / 100 条对齐**：
/// 升级时按上游默认剪一次会把用户已有的贴图历史删掉。范围照上游（1–365 / 1–1000 / 128–10240）。
/// </remarks>
internal sealed record PinnedHistoryPolicy(bool Enabled, bool KeepPermanently, int RetentionDays,
    int MaxEntries, int MaxDiskMiB)
{
    public static readonly PinnedHistoryPolicy Default = new(true, false, 30, 200, 1024);

    public long MaxDiskBytes => (long)MaxDiskMiB * 1024 * 1024;

    public bool IsDefault => this == Default;
}

/// <summary>贴图历史策略的读写（只写 <c>settings.json</c> 的 <c>PinnedHistory</c> 段）。</summary>
internal static class PinnedHistoryStore
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
    public const int MinEntries = 1;
    public const int MaxEntries = 1000;
    public const int MinDiskMiB = 128;
    public const int MaxDiskMiB = 10240;

    private static readonly object Gate = new();
    private static PinnedHistoryPolicy? _cached;

    public static PinnedHistoryPolicy Current => Snapshot();
    public static bool Enabled => Snapshot().Enabled;

    public static PinnedHistoryPolicy Normalize(bool enabled, bool keepPermanently, int retentionDays,
        int maxEntries, int maxDiskMiB) => new(
        enabled,
        keepPermanently,
        Math.Clamp(retentionDays <= 0 ? PinnedHistoryPolicy.Default.RetentionDays : retentionDays,
            MinRetentionDays, MaxRetentionDays),
        Math.Clamp(maxEntries <= 0 ? PinnedHistoryPolicy.Default.MaxEntries : maxEntries,
            MinEntries, MaxEntries),
        Math.Clamp(maxDiskMiB <= 0 ? PinnedHistoryPolicy.Default.MaxDiskMiB : maxDiskMiB,
            MinDiskMiB, MaxDiskMiB));

    public static void SetEnabled(bool on) => Save(p => p with { Enabled = on });
    public static void SetKeepPermanently(bool on) => Save(p => p with { KeepPermanently = on });
    public static void SetRetentionDays(int days) => Save(p => p with { RetentionDays = days });
    public static void SetMaxEntries(int entries) => Save(p => p with { MaxEntries = entries });
    public static void SetMaxDiskMiB(int mib) => Save(p => p with { MaxDiskMiB = mib });

    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    private static void Save(Func<PinnedHistoryPolicy, PinnedHistoryPolicy> change)
    {
        PinnedHistoryPolicy next = NormalizePolicy(change(Snapshot()));
        lock (Gate) _cached = next;

        try
        {
            void Apply(SettingsFile settings)
            {
                settings.PinnedHistory ??= new PinnedHistoryFile();
                settings.PinnedHistory.Enabled = next.Enabled;
                settings.PinnedHistory.KeepPermanently = next.KeepPermanently;
                settings.PinnedHistory.RetentionDays = next.RetentionDays;
                settings.PinnedHistory.MaxEntries = next.MaxEntries;
                settings.PinnedHistory.MaxDiskMiB = next.MaxDiskMiB;
            }

            SettingsStore.Modify(ConfigStore.Anchor, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinHistory", "保存贴图历史策略失败：" + ex.Message);
        }

        // 调小上限 / 关掉永久保留之后立刻生效
        PinStore.Maintain();
    }

    private static PinnedHistoryPolicy NormalizePolicy(PinnedHistoryPolicy policy) =>
        Normalize(policy.Enabled, policy.KeepPermanently, policy.RetentionDays, policy.MaxEntries,
            policy.MaxDiskMiB);

    private static PinnedHistoryPolicy Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        PinnedHistoryFile file;
        try
        {
            file = SettingsStore.Read(ConfigStore.Anchor).PinnedHistory ?? new PinnedHistoryFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinHistory", "读取贴图历史策略失败：" + ex.Message);
            file = new PinnedHistoryFile();
        }

        PinnedHistoryPolicy policy = Normalize(file.Enabled, file.KeepPermanently, file.RetentionDays,
            file.MaxEntries, file.MaxDiskMiB);

        lock (Gate) _cached = policy;
        return policy;
    }
}
