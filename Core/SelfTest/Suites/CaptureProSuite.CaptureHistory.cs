using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集历史的保留策略（照 Snow Shot 的 <c>capture_history/*</c>）：
/// 默认值 / 归一范围 / 「永久保留」不裁剪 / 磁盘上限从最旧开始丢。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void CaptureHistoryCases()
    {
        // ============ 默认值（逐字照 configurationschema.cpp） ============
        var policy = CaptureHistoryPolicy.Default;
        Check.True(policy.Enabled, "默认记录新采集");
        Check.False(policy.KeepPermanently, "默认不永久保留");
        Check.Equal(7, policy.RetentionDays, "默认保留 7 天");
        Check.Equal(100, policy.MaxEntries, "默认最多 100 条");
        Check.Equal(1024, policy.MaxDiskMiB, "默认磁盘上限 1024 MB");
        Check.Equal(1024L * 1024 * 1024, policy.MaxDiskBytes, "磁盘上限换成字节");
        Check.True(policy.IsDefault, "默认策略认得出是默认");

        // ============ 归一范围（1–365 天 / 1–1000 条 / 128–10240 MB） ============
        var normalized = CaptureHistoryStore.Normalize(true, false, 0, 0, 0);
        Check.Equal(7, normalized.RetentionDays, "天数 0 = 没配 → 默认 7");
        Check.Equal(100, normalized.MaxEntries, "条数 0 = 没配 → 默认 100");
        Check.Equal(1024, normalized.MaxDiskMiB, "磁盘 0 = 没配 → 默认 1024");
        Check.Equal(1, CaptureHistoryStore.Normalize(true, false, 1, 1, 1).RetentionDays,
            "天数下限 1");
        Check.Equal(7, CaptureHistoryStore.Normalize(true, false, -5, 1, 128).RetentionDays,
            "负数当没配 → 默认 7");
        Check.Equal(365, CaptureHistoryStore.Normalize(true, false, 9999, 1, 128).RetentionDays,
            "天数上限 365");
        Check.Equal(1000, CaptureHistoryStore.Normalize(true, false, 7, 9999, 128).MaxEntries,
            "条数上限 1000");
        Check.Equal(128, CaptureHistoryStore.Normalize(true, false, 7, 1, 1).MaxDiskMiB,
            "磁盘下限 128");
        Check.Equal(10240, CaptureHistoryStore.Normalize(true, false, 7, 1, 99999).MaxDiskMiB,
            "磁盘上限 10240");
        Check.Equal(1, CaptureHistoryStore.MinRetentionDays, "下限常量 1 天");
        Check.Equal(365, CaptureHistoryStore.MaxRetentionDays, "上限常量 365 天");
        Check.Equal(1000, CaptureHistoryStore.MaxEntries, "条数上限常量 1000");
        Check.Equal(10240, CaptureHistoryStore.MaxDiskMiB, "磁盘上限常量 10240");

        // ============ 按策略裁剪 ============
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00");
        var items = new List<CaptureEntry>
        {
            new() { Id = "a", CreatedAt = "2026-11-20T10:00:00" },
            new() { Id = "b", CreatedAt = "2026-11-18T10:00:00" },
            new() { Id = "c", CreatedAt = "2026-11-10T10:00:00" },   // 超 7 天
        };

        var (keep, drop) = CaptureCatalog.PlanPrune(items, now,
            CaptureHistoryPolicy.Default with { RetentionDays = 7, MaxEntries = 10 });
        Check.Equal(2, keep.Count, "超 7 天的丢一条");
        Check.Equal("c", drop.Single().Id, "丢掉的是最旧那条");

        var (keepByCount, dropByCount) = CaptureCatalog.PlanPrune(items, now,
            CaptureHistoryPolicy.Default with { RetentionDays = 365, MaxEntries = 1 });
        Check.Equal(1, keepByCount.Count, "条数上限 1 只留一条");
        Check.Equal(2, dropByCount.Count, "其余两条丢掉");

        var (keepAll, dropNone) = CaptureCatalog.PlanPrune(items, now,
            CaptureHistoryPolicy.Default with { KeepPermanently = true });
        Check.Equal(3, keepAll.Count, "永久保留：一条都不丢");
        Check.Equal(0, dropNone.Count, "永久保留：没有要丢的");

        // ============ 磁盘上限：从最旧开始丢 ============
        var sizes = new Dictionary<string, long> { ["a"] = 40, ["b"] = 40, ["c"] = 40 };
        var (within, over) = CaptureCatalog.PlanDiskPrune(items, 100, e => sizes[e.Id]);
        Check.Equal(2, within.Count, "上限 100、每条 40 → 留两条");
        Check.Equal("c", over.Single().Id, "丢掉最旧的");
        Check.Equal("a", within[0].Id, "保留的仍按时间倒序");

        var (one, rest) = CaptureCatalog.PlanDiskPrune(items, 50, e => sizes[e.Id]);
        Check.Equal(1, one.Count, "上限 50 → 只留一条（新的那条总留着）");
        Check.Equal(2, rest.Count, "其余全丢");

        var (unlimited, nothing) = CaptureCatalog.PlanDiskPrune(items, 0, e => sizes[e.Id]);
        Check.Equal(3, unlimited.Count, "上限 0 = 不限");
        Check.Equal(0, nothing.Count, "上限 0 不丢任何东西");
    }
}
