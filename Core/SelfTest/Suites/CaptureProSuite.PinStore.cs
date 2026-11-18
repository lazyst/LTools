using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图历史（照上游 <c>storage::PinnedWindowRepository</c> 的摘要 / 保留 / 来源）：
/// 剪枝、缩略图、来源归一与筛选项、摘要文案。纯函数，不碰磁盘。
/// </summary>
/// <remarks>
/// 落盘（<c>pins/pins.json</c> + 两张图）、关闭时补时间、管理页列表与批量操作由仓库外
/// <c>capture-overlay-smoke</c> 端到端验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinStoreCases()
    {
        static PinRecord Record(string id, string createdAt, string source = PinStore.SourceScreenshot,
            string group = PinGroups.Default) => new()
        {
            Id = id,
            CreatedAt = createdAt,
            Source = source,
            Group = group,
            Width = 1200,
            Height = 800,
            AreaX = 100,
            AreaY = 50,
            AreaW = 1200,
            AreaH = 800,
            Scale = 1.25,
        };

        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            Record("a", "2026-11-16T11:00:00"),
            Record("b", "2026-11-16T10:00:00"),
            Record("c", "2026-11-15T10:00:00"),
            Record("d", "2026-11-10T10:00:00"),
            Record("e", "2026-09-01T10:00:00"),   // 超过 30 天
        };

        var (keep, drop) = PinStore.PlanPrune(items, now, 3, 30);
        Check.Equal(3, keep.Count, "贴图最多保留 3 条");
        Check.Equal("a,b,c", string.Join(",", keep.Select(e => e.Id)), "按创建时间倒序保留最新的三条");
        Check.Equal("d,e", string.Join(",", drop.Select(e => e.Id)), "超量 / 超龄都进丢弃");

        var (keepAll, dropOld) = PinStore.PlanPrune(items, now, PinStore.MaxEntries, 30);
        Check.Equal(4, keepAll.Count, "没超量时只丢超龄的");
        Check.Equal("e", string.Join(",", dropOld.Select(e => e.Id)), "9 月那条超龄");

        var (_, dropNone) = PinStore.PlanPrune(items, now, PinStore.MaxEntries, 0);
        Check.Equal(0, dropNone.Count, "天数为 0 = 不按天数剪");

        // 缩略图：与采集历史同一套规则（长边 240，小图不放大）
        var big = PinStore.ThumbnailSize(1200, 800, PinStore.ThumbSide);
        Check.Equal(240, big.Width, "横图缩到长边 240");
        Check.Equal(160, big.Height, "高度按比例");
        var small = PinStore.ThumbnailSize(120, 80, PinStore.ThumbSide);
        Check.Equal(120, small.Width, "小图不放大");
        Check.Equal(0, PinStore.ThumbnailSize(0, 80, PinStore.ThumbSide).Width, "空尺寸返回 0");

        // 来源：五个已知值 + 认不出的落「其它」
        Check.Equal("截图", PinStore.SourceLabel(PinStore.SourceScreenshot), "截图的来源名");
        Check.Equal("采集历史", PinStore.SourceLabel(PinStore.SourceHistory), "采集历史的来源名");
        Check.Equal("剪贴板", PinStore.SourceLabel(PinStore.SourceClipboard), "剪贴板的来源名");
        Check.Equal("选中的文件", PinStore.SourceLabel(PinStore.SourceFiles), "选中的文件的来源名");
        Check.Equal("其它", PinStore.SourceLabel(PinStore.SourceOther), "其它的来源名");
        Check.Equal("其它", PinStore.SourceLabel("garbage"), "认不出的来源显示「其它」");
        Check.Equal(PinStore.SourceOther, PinStore.NormalizeSource("garbage"), "认不出的来源归一成 other");
        Check.Equal(PinStore.SourceClipboard, PinStore.NormalizeSource(PinStore.SourceClipboard),
            "已知来源原样保留");
        Check.Equal(PinStore.SourceOther, PinStore.NormalizeSource(null), "空来源归一成 other");

        IReadOnlyList<(string Key, string Label)> options = PinStore.SourceOptions();
        Check.Equal(5, options.Count, "来源筛选 5 项（不含「全部」，全部由空值表示）");
        Check.Equal("截图", options[0].Label, "筛选项第一项是截图");
        Check.Equal("其它", options[4].Label, "筛选项最后一项是其它");

        // 摘要
        var record = Record("x", "2026-11-16T11:00:00", PinStore.SourceClipboard, "外部");
        Check.Equal("1200 × 800", record.SizeText, "尺寸文案");
        Check.Equal("剪贴板", record.SourceText, "来源文案");
        Check.Equal("外部", record.GroupText, "分组文案");
        Check.True(record.IsOpen, "没有关闭时间 = 还开着");
        Check.Equal("—", record.ClosedText, "开着时关闭时间显示 —");

        record.ClosedAt = "2026-11-16T12:00:00";
        Check.False(record.IsOpen, "有关闭时间 = 已关闭");
        Check.True(record.ClosedText.Length > 0 && record.ClosedText != "—", "关闭时间有文案");

        // 保留上限与上游一致（分组 128 只是常数，落盘时由管理器收）
        Check.Equal(200, PinStore.MaxEntries, "最多 200 条");
        Check.Equal(30, PinStore.MaxAgeDays, "最多 30 天");
        Check.Equal(128, PinStore.MaxGroups, "分组上限 128");

        // 管理页筛选：来源多选 + 日期两端
        var shot = Record("s", "2026-11-16T11:00:00", PinStore.SourceScreenshot);
        var clip = Record("c", "2026-11-10T09:00:00", PinStore.SourceClipboard);
        Check.True(PinStore.Matches(shot, null, null, null), "没有筛选条件时全部命中");
        Check.True(PinStore.Matches(shot, new List<string>(), null, null), "空来源集合 = 全部来源");
        Check.True(PinStore.Matches(shot, new[] { PinStore.SourceScreenshot }, null, null), "来源命中");
        Check.False(PinStore.Matches(clip, new[] { PinStore.SourceScreenshot }, null, null), "来源不命中");
        Check.True(PinStore.Matches(shot, new[] { PinStore.SourceScreenshot, PinStore.SourceClipboard },
            null, null), "来源多选任一命中即可");
        Check.True(PinStore.Matches(shot, null, new DateTime(2026, 11, 16), null), "只给开始日期：同日命中");
        Check.False(PinStore.Matches(clip, null, new DateTime(2026, 11, 16), null), "只给开始日期：更早的不命中");
        Check.True(PinStore.Matches(clip, null, null, new DateTime(2026, 11, 16)), "只给结束日期：更早的命中");
        Check.False(PinStore.Matches(shot, null, null, new DateTime(2026, 11, 15)), "只给结束日期：更晚的不命中");
        Check.True(PinStore.Matches(shot, null, new DateTime(2026, 11, 16), new DateTime(2026, 11, 16)),
            "同一天的起止都命中（含两端）");

        // 分页：区间与总页数
        var (first, last) = PinStore.PageRange(2, 20, 45);
        Check.Equal(20, first, "第 2 页从第 21 条开始（0 基）");
        Check.Equal(40, last, "第 2 页到第 40 条（右开）");
        var (tailFirst, tailLast) = PinStore.PageRange(3, 20, 45);
        Check.Equal(40, tailFirst, "最后一页起点");
        Check.Equal(45, tailLast, "最后一页到末尾，不超出总数");
        Check.Equal((0, 0), PinStore.PageRange(1, 20, 0), "没有记录时区间为空");
        Check.Equal(3, PinStore.PageCount(45, 20), "45 条 / 每页 20 = 3 页");
        Check.Equal(1, PinStore.PageCount(0, 20), "没有记录也算 1 页");
        Check.Equal(1, PinStore.PageCount(5, 0), "页大小非法时不崩，按 1 页");
    }
}
