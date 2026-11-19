using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集历史页（P7-f，照上游 <c>screenshothistorypagewidget</c>）：来源分组、日期范围、
/// 关键词、分页与批量可用性，以及卡片上的那几行文案。
/// </summary>
/// <remarks>
/// 纯逻辑，不读磁盘也不开窗口；真窗口的筛选 / 分页 / 批量贴回 / 查看器由仓库外
/// <c>capture-overlay-smoke</c> 的 <c>HistoryPageSection</c> 端到端验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void HistoryPageCases()
    {
        // ============ 来源：Kind 优先，其次看完成动作 ============
        Check.Equal(CaptureHistorySource.Record,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindRecord, "复制"), "录屏优先于完成动作");
        Check.Equal(CaptureHistorySource.Recognize,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindRecognize, "识别"), "识别是独立来源");
        Check.Equal(CaptureHistorySource.Clipboard,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "复制"), "复制 = 剪贴板");
        Check.Equal(CaptureHistorySource.Clipboard,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "识别并复制"), "识别并复制也落在剪贴板");
        Check.Equal(CaptureHistorySource.File,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "另存为"), "另存为 = 文件");
        Check.Equal(CaptureHistorySource.File,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "快速保存"), "快速保存 = 文件");
        Check.Equal(CaptureHistorySource.Pinned,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "贴图"), "贴图 = 贴到屏幕");
        Check.Equal(CaptureHistorySource.Pinned,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "实时贴图"), "实时贴图 = 贴到屏幕");
        Check.Equal(CaptureHistorySource.CurrentMonitor,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "当前显示器"), "当前显示器");
        Check.Equal(CaptureHistorySource.FocusedWindow,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "前台窗口"), "前台窗口");
        Check.Equal(CaptureHistorySource.Other,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, ""), "没有完成动作 = 其它");
        Check.Equal(CaptureHistorySource.Other,
            CaptureHistorySource.KeyOf(CaptureCatalog.KindShot, "打印"), "认不出的完成动作 = 其它");

        foreach (var (key, label) in CaptureHistorySource.All)
        {
            Check.Equal(label, CaptureHistorySource.LabelOf(key), $"来源「{key}」有中文名");
        }
        Check.Equal("clipboard-x", CaptureHistorySource.LabelOf("clipboard-x"), "陌生键原样返回");
        Check.Equal(CaptureHistorySource.Other, CaptureHistorySource.LabelOf(null), "空键按「其它」");

        // ============ 日期范围：含两端、起点终点可以反着点 ============
        var day1 = new DateTime(2026, 11, 1);
        var day10 = new DateTime(2026, 11, 10);
        var day20 = new DateTime(2026, 11, 20);

        var range = new CaptureDateRange(day1, day10).Ordered();
        Check.True(range.Contains(day1), "起始日算在范围内（含左端）");
        Check.True(range.Contains(day10), "结束日算在范围内（含右端）");
        Check.False(range.Contains(day20), "范围外不算");

        var swapped = new CaptureDateRange(day10, day1).Ordered();
        Check.Equal(day1, swapped.Start!.Value, "先点结束再点开始：起终点交换");
        Check.Equal(day10, swapped.End!.Value, "交换后终点正确");

        var openEnd = new CaptureDateRange(day1, null);
        Check.True(openEnd.Contains(day20), "只有起点 = 往后的都算");
        Check.False(openEnd.Contains(new DateTime(2026, 10, 31)), "只有起点时更早的不算");

        var openStart = new CaptureDateRange(null, day10);
        Check.True(openStart.Contains(new DateTime(2026, 1, 1)), "只有终点 = 更早的也算");
        Check.False(openStart.Contains(day20), "只有终点时更晚的不算");

        Check.Equal("2026-11-01 ~ 2026-11-10",
            new CaptureDateRange(day1, day10).Describe(), "两端都填的文案");
        Check.Equal("2026-11-01 起", new CaptureDateRange(day1, null).Describe(), "只有起点的文案");
        Check.Equal("截至 2026-11-10", new CaptureDateRange(null, day10).Describe(), "只有终点的文案");
        Check.Equal("", CaptureDateRange.Empty.Describe(), "都不填没有文案");
        Check.True(CaptureDateRange.Empty.IsEmpty, "都不填 = 未筛选");

        // ============ 时间解析：只认 catalog.json 的固定格式 ============
        Check.True(CaptureHistoryQuery.TryParseTime("2026-11-19T08:30:05", out var parsed),
            "标准时间戳解析成功");
        Check.Equal(new DateTime(2026, 11, 19, 8, 30, 5), parsed, "解析出正确的本地时间");
        Check.False(CaptureHistoryQuery.TryParseTime("2026-11-19 08:30:05", out _),
            "空格分隔不是我们的存法");
        Check.False(CaptureHistoryQuery.TryParseTime(null, out _), "空时间戳解析失败");

        // ============ 筛选 ============
        var shot = Entry("a", CaptureCatalog.KindShot, "复制", "2026-11-19T08:30:05", text: "hello world");
        var saved = Entry("b", CaptureCatalog.KindShot, "另存为", "2026-11-10T09:00:00");
        var record = Entry("c", CaptureCatalog.KindRecord, "录屏", "2026-10-01T23:00:00");
        var plain = Entry("d", CaptureCatalog.KindShot, "复制", "坏时间");
        var all = new List<CaptureEntry> { shot, saved, record, plain };

        Check.True(CaptureHistoryQuery.All.Matches(shot), "不筛时全部命中");
        Check.False(CaptureHistoryQuery.All.IsFiltered, "不筛时不算「已筛选」");

        var bySource = new CaptureHistoryQuery { Sources = new[] { CaptureHistorySource.File } };
        Check.Equal(1, bySource.Apply(all).Count, "按来源筛选只留一条");
        Check.Equal("b", bySource.Apply(all)[0].Id, "留下的是「另存为」那条");
        Check.True(bySource.IsFiltered, "选了来源 = 已筛选");

        var byDate = new CaptureHistoryQuery { Range = new CaptureDateRange(day1, day10) };
        var dated = byDate.Apply(all);
        Check.Equal(1, dated.Count, "日期范围内只有一条");
        Check.Equal("b", dated[0].Id, "时间戳解析不了的不进日期筛选结果");

        var byText = new CaptureHistoryQuery { Text = "WORLD" };
        Check.Equal(1, byText.Apply(all).Count, "关键词忽略大小写命中识别文字");
        Check.Equal(1, new CaptureHistoryQuery { Text = "另存" }.Apply(all).Count, "关键词也能命中完成动作");
        Check.Equal(1, new CaptureHistoryQuery { Text = "录屏" }.Apply(all).Count, "关键词能命中类型名");
        Check.Equal(0, new CaptureHistoryQuery { Text = "不存在" }.Apply(all).Count, "关键词没有命中就是空");
        Check.Equal(4, new CaptureHistoryQuery { Text = "  " }.Apply(all).Count, "只有空白 = 不按文字筛");

        var both = new CaptureHistoryQuery
        {
            Sources = new[] { CaptureHistorySource.File, CaptureHistorySource.Record },
            Text = "另存",
        };
        Check.Equal(1, both.Apply(all).Count, "来源与关键词是 AND");

        // ============ 分页 ============
        Check.Equal(20, CaptureHistoryPaging.NormalizePageSize(0), "每页条数 0 回到默认 20");
        Check.Equal(20, CaptureHistoryPaging.NormalizePageSize(7), "认不出的每页条数回默认");
        Check.Equal(10, CaptureHistoryPaging.NormalizePageSize(10), "10 是合法值");
        Check.Equal(50, CaptureHistoryPaging.NormalizePageSize(50), "50 是合法值");

        Check.Equal(1, CaptureHistoryPaging.PageCount(0, 20), "空表也算 1 页");
        Check.Equal(1, CaptureHistoryPaging.PageCount(20, 20), "正好一页");
        Check.Equal(2, CaptureHistoryPaging.PageCount(21, 20), "多一条就翻页");
        Check.Equal(4, CaptureHistoryPaging.PageCount(37, 10), "37 条按每页 10 = 4 页");

        Check.Equal(1, CaptureHistoryPaging.ClampPage(0, 37, 20), "页码 0 夹到第 1 页");
        Check.Equal(2, CaptureHistoryPaging.ClampPage(9, 37, 20), "页码超出夹到最后一页");

        Check.Equal((0, 20), CaptureHistoryPaging.Range(1, 37, 20), "第 1 页取前 20 条");
        Check.Equal((20, 37), CaptureHistoryPaging.Range(2, 37, 20), "第 2 页取剩下 17 条");
        Check.Equal((0, 0), CaptureHistoryPaging.Range(1, 0, 20), "空表没有区间");
        Check.Equal((20, 37), CaptureHistoryPaging.Range(99, 37, 20), "超界页码按最后一页取");

        Check.Equal("1-20 / 共 37 条", CaptureHistoryPaging.RangeText(1, 37, 20), "底栏区间文案");
        Check.Equal("21-37 / 共 37 条", CaptureHistoryPaging.RangeText(2, 37, 20), "最后一页的区间文案");
        Check.Equal("还没有记录", CaptureHistoryPaging.RangeText(1, 0, 20), "空表底栏文案");

        Check.True(CaptureHistoryPaging.PageNumbers(1, 1).Count == 0, "只有一页时不列页码");
        Check.Equal("1,2,3", string.Join(",", CaptureHistoryPaging.PageNumbers(2, 3)), "页数少就全列");
        Check.Equal("1,2,3,4,5,6,7", string.Join(",", CaptureHistoryPaging.PageNumbers(1, 10)),
            "第 1 页从 1 开始，窗口 7 个");
        Check.Equal("4,5,6,7,8,9,10", string.Join(",", CaptureHistoryPaging.PageNumbers(10, 10)),
            "最后一页贴住末页");
        Check.Equal("4,5,6,7,8,9,10", string.Join(",", CaptureHistoryPaging.PageNumbers(7, 10)),
            "中间页以当前页居中");
        Check.Equal("1,2,3", string.Join(",", CaptureHistoryPaging.PageNumbers(2, 3, 7)),
            "窗口比页数大时全列");

        // ============ 批量可用性 ============
        var pageIds = new[] { "a", "b" };
        var selected = new HashSet<string>(StringComparer.Ordinal);
        Check.False(CaptureHistoryPaging.AllSelected(pageIds, selected), "没选时不算全选");
        selected.Add("a");
        Check.False(CaptureHistoryPaging.AllSelected(pageIds, selected), "只选一条不算全选");
        selected.Add("b");
        Check.True(CaptureHistoryPaging.AllSelected(pageIds, selected), "整页都选上才算全选");
        Check.False(CaptureHistoryPaging.AllSelected(Array.Empty<string>(), selected), "空页不算全选");

        var onlyRecordSelected = new HashSet<string>(StringComparer.Ordinal) { "c" };
        Check.False(CaptureHistoryPaging.AnyPinnable(all, onlyRecordSelected), "录屏不能贴回屏幕");
        Check.False(CaptureHistoryPaging.AnyStitchable(all, onlyRecordSelected), "录屏不能拼接");
        var imageSelected = new HashSet<string>(StringComparer.Ordinal) { "a", "c" };
        Check.True(CaptureHistoryPaging.AnyPinnable(all, imageSelected), "选中有图就能贴回");
        Check.True(CaptureHistoryPaging.AnyStitchable(all, imageSelected), "选中有图就能拼接");

        var picked = CaptureHistoryPaging.Selected(all, imageSelected);
        Check.Equal(2, picked.Count, "选中项按列表顺序取出");
        Check.Equal("a", picked[0].Id, "先取最新的那条");
        Check.Equal("c", picked[1].Id, "再取较旧的");

        // ============ 压到几块屏（照上游 record.displays.size()） ============
        var screens = new (int Left, int Top, int Width, int Height)[]
        {
            (0, 0, 1920, 1080),
            (1920, 0, 1920, 1080),
        };

        Check.Equal(1, CaptureCatalog.CountDisplays(100, 100, 800, 600, screens), "整块在屏内 = 1 块");
        Check.Equal(2, CaptureCatalog.CountDisplays(1800, 100, 400, 600, screens), "跨两块屏 = 2 块");
        Check.Equal(1, CaptureCatalog.CountDisplays(5000, 5000, 100, 100, screens),
            "完全不在屏幕上也算 1 块（别写 0）");
        Check.Equal(1, CaptureCatalog.CountDisplays(0, 0, 0, 0, screens), "空矩形 = 1 块");

        // ============ 卡片文案 ============
        var card = new CaptureHistoryRow(shot, null, 0);
        Check.Equal("2026-11-19  08:30:05", card.DateText, "卡片时间是「日期  时分秒」（两个空格）");
        Check.Equal("复制到剪贴板", card.SourceLabel, "卡片来源徽标");
        Check.Equal("100 × 50 px  ·  1 块屏", card.SummaryText, "卡片摘要写尺寸 + 块屏");
        Check.Equal("位置 10, 20", card.MetaText, "记过落位就写位置（没有文件大小时不跟空串）");
        Check.True(card.HasText, "有识别文字时卡片多一行摘要");
        Check.Equal("hello world", card.TextExcerpt, "卡片上的文字摘要");
        Check.True(card.CanCopy, "截图卡片可以复制");
        Check.False(card.CanPlay, "截图卡片没有播放");

        var sized = new CaptureHistoryRow(shot, null, 1024);
        Check.Equal("位置 10, 20  ·  1.0 KB", sized.MetaText, "有文件大小时跟在位置后面");

        var noPlace = new CaptureHistoryRow(
            Entry("e", CaptureCatalog.KindShot, "复制", "2026-11-19T08:30:05", x: 0, w: 0), null, 0);
        Check.Equal("未记录位置", noPlace.MetaText, "老记录没落位时写「未记录位置」");

        var badTime = new CaptureHistoryRow(plain, null, 0);
        Check.Equal("坏时间", badTime.DateText, "时间戳解析不了就原样显示");

        var clip = Entry("r", CaptureCatalog.KindRecord, "录屏", "2026-11-19T08:30:05");
        clip.DurationMs = 65_000;
        clip.File = "records/r.mp4";
        var recordCard = new CaptureHistoryRow(clip, null, 0);
        Check.Equal("100 × 50 px  ·  01:05  ·  MP4", recordCard.SummaryText,
            "录屏摘要写尺寸 + 时长 + 格式");
        Check.True(recordCard.CanPlay, "录屏卡片是「播放」");
        Check.False(recordCard.CanCopy, "录屏卡片不复制封面");
        Check.False(recordCard.HasText, "录屏不显示识别文字");
        Check.Equal("没有封面帧", recordCard.PreviewHint, "录屏没有封面时的提示");

        // 摘要压两行：换行 / 多余空白都当分隔，超过 90 字截断
        Check.Equal("第一行 第二行", CaptureHistoryRow.Excerpt("第一行\n\n  第二行  "), "换行折成空格");
        string longText = new string('x', 120);
        Check.Equal(91, CaptureHistoryRow.Excerpt(longText).Length, "超长文字截到 90 字加省略号");
        Check.True(CaptureHistoryRow.Excerpt(longText).EndsWith('…'), "截断后有省略号");
    }

    private static CaptureEntry Entry(string id, string kind, string source, string createdAt,
        string text = "", int x = 10, int w = 100) => new()
    {
        Id = id,
        Kind = kind,
        Source = source,
        CreatedAt = createdAt,
        Width = 100,
        Height = 50,
        Text = text,
        SelectionX = x,
        SelectionY = 20,
        SelectionW = w,
        SelectionH = 50,
        Displays = 1,
    };
}

