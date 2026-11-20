using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图历史的保留策略（照 Snow Shot 的 <c>pinned_history/*</c>）：
/// 默认值（沿用 LTools 既有 30 天 / 200 条）/ 归一范围 / 永久保留 / 磁盘上限。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void PinnedHistoryCases()
    {
        var policy = PinnedHistoryPolicy.Default;
        Check.True(policy.Enabled, "默认记录贴图历史");
        Check.False(policy.KeepPermanently, "默认不永久保留");
        Check.Equal(30, policy.RetentionDays, "默认保留 30 天（沿用 LTools 既有口径）");
        Check.Equal(200, policy.MaxEntries, "默认最多 200 条（沿用 LTools 既有口径）");
        Check.Equal(1024, policy.MaxDiskMiB, "默认磁盘上限 1024 MB");
        Check.True(policy.IsDefault, "默认策略认得出是默认");

        var normalized = PinnedHistoryStore.Normalize(true, false, 0, 0, 0);
        Check.Equal(30, normalized.RetentionDays, "天数 0 = 没配 → 默认 30");
        Check.Equal(200, normalized.MaxEntries, "条数 0 = 没配 → 默认 200");
        Check.Equal(1024, normalized.MaxDiskMiB, "磁盘 0 = 没配 → 默认 1024");
        Check.Equal(365, PinnedHistoryStore.Normalize(true, false, 9999, 1, 128).RetentionDays,
            "天数上限 365");
        Check.Equal(1000, PinnedHistoryStore.Normalize(true, false, 7, 9999, 128).MaxEntries,
            "条数上限 1000");
        Check.Equal(128, PinnedHistoryStore.Normalize(true, false, 7, 1, 1).MaxDiskMiB,
            "磁盘下限 128");
        Check.Equal(10240, PinnedHistoryStore.Normalize(true, false, 7, 1, 99999).MaxDiskMiB,
            "磁盘上限 10240");

        var now = DateTimeOffset.Parse("2026-11-20T12:00:00");
        var items = new List<PinRecord>
        {
            new() { Id = "a", CreatedAt = "2026-11-20T10:00:00" },
            new() { Id = "b", CreatedAt = "2026-11-18T10:00:00" },
            new() { Id = "c", CreatedAt = "2026-10-01T10:00:00" },   // 超 30 天
        };

        var (keep, drop) = PinStore.PlanPrune(items, now,
            PinnedHistoryPolicy.Default with { RetentionDays = 30, MaxEntries = 10 });
        Check.Equal(2, keep.Count, "超 30 天的丢一条");
        Check.Equal("c", drop.Single().Id, "丢掉的是最旧那条");

        var (keepAll, dropNone) = PinStore.PlanPrune(items, now,
            PinnedHistoryPolicy.Default with { KeepPermanently = true });
        Check.Equal(3, keepAll.Count, "永久保留：一条都不丢");
        Check.Equal(0, dropNone.Count, "永久保留：没有要丢的");

        var sizes = new Dictionary<string, long> { ["a"] = 40, ["b"] = 40, ["c"] = 40 };
        var (within, over) = PinStore.PlanDiskPrune(items, 100, e => sizes[e.Id]);
        Check.Equal(2, within.Count, "磁盘上限 100、每条 40 → 留两条");
        Check.Equal("c", over.Single().Id, "丢掉最旧的");

        var (one, rest) = PinStore.PlanDiskPrune(items, 50, e => sizes[e.Id]);
        Check.Equal(1, one.Count, "上限 50 → 只留一条");
        Check.Equal(2, rest.Count, "其余全丢");
    }
}
