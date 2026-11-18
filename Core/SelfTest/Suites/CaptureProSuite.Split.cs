using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Split;

namespace LTools.Core.SelfTest;

/// <summary>
/// 切图窗的纯逻辑：标称布局（等分 / 精确像素 / 百分比 / 宫格）、重叠矩形、智能找线、
/// 文档的加线 / 删线 / 拖线 / 撤销、表单草案的单位换算与错误文案、导出命名与落盘。
/// </summary>
/// <remarks>
/// 照上游 <c>calculateImageSplitLayout</c> / <c>imagesplitsmartlayout.cpp</c> /
/// <c>imagesplitexport.cpp</c>。窗口本体（真实鼠标拖线、标尺、按钮可用性）在仓库外冒烟里跑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void SplitCalculatorCases()
    {
        // ---- 等分：多出来的像素先给前面的段（10 宽切 3 → 4/3/3）----
        var equal = SplitLayoutCalculator.Calculate(10, 10, new SplitSettings
        {
            Direction = SplitDirection.Vertical,
            Type = SplitType.Equal,
            Count = 3,
        });
        SplitLinesAre(equal.Vertical, new[] { 4, 7 }, "10 宽等分 3 张：线在 4 / 7");
        Check.Equal(3L, equal.OutputCount, "等分 3 张 → 3 份");
        Check.True(equal.IsValid, "等分 3 张是一份有效布局");

        var wide = SplitLayoutCalculator.Calculate(1000, 600, new SplitSettings { Count = 3 });
        SplitLinesAre(wide.Vertical, new[] { 334, 667 }, "1000 宽等分 3 张：334 / 667（先宽后窄）");

        var single = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings { Count = 1 });
        SplitLinesAre(single.Vertical, Array.Empty<int>(), "切 1 张 = 一条线都没有");
        Check.Equal(1L, single.OutputCount, "1 张的份数是 1");

        var zero = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings { Count = 0 });
        Check.True(zero.Errors.ContainsKey("count"), "张数 0 → count 报错");
        Check.False(zero.IsValid, "张数 0 的布局无效");
        Check.Equal(0L, zero.OutputCount, "无效布局的份数是 0（按钮置灰）");

        var tooMany = SplitLayoutCalculator.Calculate(4, 100, new SplitSettings { Count = 5 });
        Check.True(tooMany.Errors.TryGetValue("count", out var tooManyText) &&
                   tooManyText.Contains("1 像素"), "张数比边长还多 → 每张至少 1 像素");

        // ---- 精确像素：线 = pixels 的整数倍，数量 (边长-1)/pixels（可能为 0）----
        var precise = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Pixels,
            Pixels = 16,
        });
        SplitLinesAre(precise.Vertical, new[] { 16, 32, 48, 64, 80, 96 }, "每张 16 像素：16…96 六条线");
        Check.Equal(7L, precise.OutputCount, "100 宽每张 16 → 7 份");

        var smaller = SplitLayoutCalculator.Calculate(16, 20, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Pixels,
            Pixels = 16,
        });
        SplitLinesAre(smaller.Vertical, Array.Empty<int>(), "图比每张尺寸还小 → 不切（不是报错）");
        Check.True(smaller.IsValid, "不切也是一份有效布局");

        var tiny = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Pixels,
            Pixels = 15,
        });
        Check.True(tiny.Errors.TryGetValue("pixels", out var tinyText) && tinyText.Contains("16"),
            "每张 < 16 像素 → pixels 报错");

        // ---- 精确百分比：十进制精确 + 首位被丢小数 ≥5 进位（照上游）----
        var half = SplitLayoutCalculator.Calculate(1000, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "50",
        });
        SplitLinesAre(half.Vertical, new[] { 500 }, "50% 的 1000 宽 → 线在 500");

        var third = SplitLayoutCalculator.Calculate(1000, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "33",
        });
        SplitLinesAre(third.Vertical, new[] { 330, 660, 990 }, "33% 的 1000 宽 → 330 / 660 / 990");

        var decimals = SplitLayoutCalculator.Calculate(1000, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "12.5",
        });
        SplitLinesAre(decimals.Vertical, new[] { 125, 250, 375, 500, 625, 750, 875 },
            "12.5% 的 1000 宽 → 每 125 一条");

        var carrying = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "16.67",
        });
        SplitLinesAre(carrying.Vertical, new[] { 17, 33, 50, 67, 83 },
            "16.67% 的 100 宽：0.67 → 16.67 进 17、33.34 进 33…（首位小数进位）");

        var clamped = SplitLayoutCalculator.Calculate(1000, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "4",
        });
        Check.True(clamped.Errors.ContainsKey("percentage"), "低于 5% → percentage 报错");

        var tooBig = SplitLayoutCalculator.Calculate(1000, 100, new SplitSettings
        {
            Type = SplitType.Precise,
            Unit = SplitUnit.Percentage,
            Percentage = "101",
        });
        Check.True(tooBig.Errors.ContainsKey("percentage"), "高于 100% → percentage 报错");

        Check.Equal("500", SplitLayoutCalculator.PercentagePixels(1000, "50")?.ToString(), "50% → 500 像素");
        Check.Equal("17", SplitLayoutCalculator.PercentagePixels(100, "16.67")?.ToString(), "16.67% → 17 像素");
        Check.Equal("50.00", SplitLayoutCalculator.PixelsToPercentage(1000, 500), "500 / 1000 → 50.00%");
        Check.Equal("16.67", SplitLayoutCalculator.PixelsToPercentage(1200, 200), "200 / 1200 → 16.67%");

        // ---- 宫格：两条轴各自等分 ----
        var grid = SplitLayoutCalculator.Calculate(300, 200, new SplitSettings
        {
            Direction = SplitDirection.Grid,
            Rows = 2,
            Columns = 3,
        });
        SplitLinesAre(grid.Vertical, new[] { 100, 200 }, "宫格 3 列 → 100 / 200");
        SplitLinesAre(grid.Horizontal, new[] { 100 }, "宫格 2 行 → 100");
        Check.Equal(6L, grid.OutputCount, "3 列 × 2 行 = 6 份");

        var gridBad = SplitLayoutCalculator.Calculate(300, 200, new SplitSettings
        {
            Direction = SplitDirection.Grid,
            Rows = 0,
            Columns = 2,
        });
        Check.True(gridBad.Errors.ContainsKey("rows"), "宫格行数 0 → rows 报错");

        // ---- 重叠 / 图片不可用 ----
        var overlapBad = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings { Overlap = -1 });
        Check.True(overlapBad.Errors.ContainsKey("overlap"), "重叠为负 → overlap 报错");

        var empty = SplitLayoutCalculator.Calculate(0, 0, new SplitSettings());
        Check.True(empty.Errors.ContainsKey("image"), "没有图 → image 报错");
        Check.Equal(0L, empty.OutputCount, "没有图时份数为 0");
    }

    private static void SplitRegionCases()
    {
        var settings = new SplitSettings { Direction = SplitDirection.Vertical, Count = 2 };
        var layout = SplitLayoutCalculator.Calculate(100, 100, settings);
        Check.Equal(new Int32Rect(0, 0, 50, 100), layout.BaseRegionAt(0), "第 1 张原始格子 0..50");
        Check.Equal(new Int32Rect(50, 0, 50, 100), layout.BaseRegionAt(1), "第 2 张原始格子 50..100");
        Check.Equal(Int32Rect.Empty, layout.BaseRegionAt(2), "越界 = 空矩形");

        layout.Overlap = 10;
        Check.Equal(new Int32Rect(0, 0, 60, 100), layout.RegionAt(0), "重叠：第 1 张右边外扩到 60");
        Check.Equal(new Int32Rect(40, 0, 60, 100), layout.RegionAt(1), "重叠：第 2 张左边外扩到 40");

        layout.Overlap = 80;
        Check.Equal(new Int32Rect(0, 0, 100, 100), layout.RegionAt(0), "重叠超过图宽时夹回整张");

        // 宫格的格子顺序：先横着走完一行，再走下一行
        var grid = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings
        {
            Direction = SplitDirection.Grid,
            Rows = 2,
            Columns = 2,
        });
        Check.Equal(new Int32Rect(0, 0, 50, 50), grid.BaseRegionAt(0), "宫格第 1 张在左上");
        Check.Equal(new Int32Rect(50, 0, 50, 50), grid.BaseRegionAt(1), "宫格第 2 张在右上");
        Check.Equal(new Int32Rect(0, 50, 50, 50), grid.BaseRegionAt(2), "宫格第 3 张在左下");
        Check.Equal(new Int32Rect(50, 50, 50, 50), grid.BaseRegionAt(3), "宫格第 4 张在右下");
    }

    private static void SplitSmartCases()
    {
        // 200×50：x = 95..105 是一段纯色（列差为 0），其余列黑白交替（列差很大）。
        // 纵向智能切 2 张 → 标称 100，域 79..121，最平的候选正好是 100 → 线落在 100。
        var image = SplitImage(200, 50, (x, y) =>
            x is >= 95 and <= 105
                ? Color.FromRgb(0x80, 0x80, 0x80)
                : (x % 2 == 0 ? Colors.Black : Colors.White));

        var prepared = SplitSmart.Prepare(image, new SplitSettings
        {
            Direction = SplitDirection.Vertical,
            Type = SplitType.Smart,
            Count = 2,
        });
        Check.True(prepared != null, "智能切分能算出结果");
        SplitLinesAre(prepared!.Layout.Vertical, new[] { 100 }, "智能切分避开交替纹理，落在纯色段中间");

        // 横向同理：纯色带落在 y = 95..105
        var turned = SplitImage(50, 200, (x, y) =>
            y is >= 95 and <= 105
                ? Color.FromRgb(0x80, 0x80, 0x80)
                : (y % 2 == 0 ? Colors.Black : Colors.White));
        var horizontal = SplitSmart.Prepare(turned, new SplitSettings
        {
            Direction = SplitDirection.Horizontal,
            Type = SplitType.Smart,
            Count = 2,
        });
        SplitLinesAre(horizontal!.Layout.Horizontal, new[] { 100 }, "横向智能切分也落在纯色段中间");

        // 非智能：智能只是「标称布局 + 找线」，别的类型原样返回标称值
        var equal = SplitSmart.Prepare(image, new SplitSettings { Type = SplitType.Equal, Count = 2 });
        SplitLinesAre(equal!.Layout.Vertical, new[] { 100 }, "等分不找线，直接用标称线");

        // 取消：返回 null，不当作失败
        Check.True(SplitSmart.Prepare(image, new SplitSettings { Count = 2 }, () => true) == null,
            "算之前就被取消 → 返回 null");

        // 图太窄、只有一条线时不用投影，直接给标称位置
        var narrow = SplitImage(3, 5, (x, y) => Colors.White);
        var narrowLine = SplitSmart.Prepare(narrow, new SplitSettings { Count = 2 });
        SplitLinesAre(narrowLine!.Layout.Vertical, new[] { 2 }, "3 宽切 2 → 线在 2（先宽后窄）");
    }

    private static void SplitDocumentCases()
    {
        var image = SplitImage(200, 100, (x, y) => Colors.White);
        var settings = new SplitSettings { Direction = SplitDirection.Vertical, Count = 2 };
        var prepared = new SplitPrepared(settings,
            SplitLayoutCalculator.Calculate(200, 100, settings));

        var document = new SplitDocument();
        Check.False(document.SetPreparedImage(SplitImage(10, 10, (x, y) => Colors.White), prepared),
            "布局尺寸和图片对不上 → 拒绝");
        Check.True(document.SetPreparedImage(image, prepared), "装图成功");
        SplitIntsAre(document.State.Vertical, new[] { 100 }, "初始一条竖线");
        Check.False(document.CanUndo, "刚装好图没有可撤销的东西");

        Check.True(document.CreateLine(true, 50), "在 50 加一条竖线");
        SplitIntsAre(document.State.Vertical, new[] { 50, 100 }, "新线按坐标排好");
        Check.True(document.State.ManuallyEdited, "手加线后标记为手改");
        Check.False(document.CreateLine(true, 50), "同一像素不能加第二条线");
        Check.False(document.CreateLine(false, 50), "纵向切分不认横向线");
        Check.False(document.CreateLine(true, 0), "线的坐标必须在 1..边长-1");

        Check.True(document.RemoveLine(true, 0), "删掉第一条线");
        SplitIntsAre(document.State.Vertical, new[] { 100 }, "删完只剩原来那条");

        Check.True(document.CanUndo, "改过就能撤销");
        document.Undo();
        SplitIntsAre(document.State.Vertical, new[] { 50, 100 }, "撤销回到加线之后");
        document.Redo();
        SplitIntsAre(document.State.Vertical, new[] { 100 }, "重做回到删线之后");

        // ---- 拖线：跨过相邻线要换成新下标，松手才进撤销栈 ----
        var drag = new SplitDocument();
        drag.SetPreparedImage(image, prepared);
        drag.CreateLine(true, 50);
        drag.CreateLine(true, 150);
        SplitIntsAre(drag.State.Vertical, new[] { 50, 100, 150 }, "三条线排好");

        Check.True(drag.BeginLineDrag(true, 0), "按下第一条线");
        Check.True(drag.Dragging, "拖动中");
        Check.True(drag.MoveDraggedLine(160), "拖到 160（跨过另外两条）");
        SplitIntsAre(drag.State.Vertical, new[] { 100, 150, 160 }, "跨线后按坐标重排");
        Check.Equal(2, drag.DragIndex, "被拖的那条现在排到最后");
        Check.False(drag.CanUndo, "拖动过程中不进撤销栈");
        drag.EndLineDrag();
        SplitIntsAre(drag.State.Vertical, new[] { 100, 150, 160 }, "松手保留结果");
        Check.True(drag.CanUndo, "松手后算一步撤销");

        drag.Undo();
        SplitIntsAre(drag.State.Vertical, new[] { 50, 100, 150 }, "一次拖动只算一步撤销");

        // Esc 取消：整次拖动作废
        drag.BeginLineDrag(true, 0);
        drag.MoveDraggedLine(120);
        drag.CancelLineDrag();
        SplitIntsAre(drag.State.Vertical, new[] { 50, 100, 150 }, "取消拖动恢复原样");

        // 没有真的挪过就什么也不留
        long revision = drag.Revision;
        drag.BeginLineDrag(true, 1);
        drag.EndLineDrag();
        Check.Equal(revision + 2, drag.Revision, "原地松手只让修订号动两次（按下 / 松手），不写历史");

        // ---- 换设置：只改重叠当场生效；需要重新找线就交给后台 ----
        var overlapOnly = settings.Clone();
        overlapOnly.Overlap = 12;
        Check.Equal(SplitApply.Applied, drag.TryApplySettings(overlapOnly), "只改重叠 → 当场生效");
        Check.Equal(12, drag.Layout.Overlap, "重叠写进布局");
        SplitIntsAre(drag.State.Vertical, new[] { 50, 100, 150 }, "只改重叠不动用户摆好的线");

        var more = new SplitSettings { Direction = SplitDirection.Vertical, Count = 4 };
        Check.Equal(SplitApply.NeedsPrepare, drag.TryApplySettings(more), "改张数 → 需要后台重新找线");

        var impossible = new SplitSettings { Direction = SplitDirection.Vertical, Count = 0 };
        Check.Equal(SplitApply.Unchanged, drag.TryApplySettings(impossible), "算不出标称布局 → 不动文档");

        // ---- 后台结果 CAS：修订号 / 图上尺寸 / 设置任一对不上就丢掉 ----
        var prepared4 = new SplitPrepared(more,
            SplitLayoutCalculator.Calculate(200, 100, more));
        Check.False(drag.ApplyPrepared(prepared4, drag.Revision - 1), "修订号过期 → 丢掉");
        Check.True(drag.ApplyPrepared(prepared4, drag.Revision), "修订号对得上 → 落地");
        SplitIntsAre(drag.State.Vertical, new[] { 50, 100, 150 }, "新设置下交回的线以结果为准");

        // ---- 宫格两条轴都能加线 ----
        var gridDocument = new SplitDocument();
        var gridSettings = new SplitSettings { Direction = SplitDirection.Grid, Rows = 2, Columns = 2 };
        gridDocument.SetPreparedImage(image, new SplitPrepared(gridSettings,
            SplitLayoutCalculator.Calculate(200, 100, gridSettings)));
        Check.True(gridDocument.AllowsAxis(true) && gridDocument.AllowsAxis(false), "宫格两条轴都能编辑");
        Check.True(gridDocument.CreateLine(false, 40), "宫格能加横线");

        // ---- 重置回空态 ----
        gridDocument.Reset();
        Check.True(gridDocument.Image == null, "重置后没有图");
        Check.Equal(0, gridDocument.State.Vertical.Length, "重置后竖线清空");
        Check.Equal(0, gridDocument.State.Horizontal.Length, "重置后横线清空");
        Check.False(gridDocument.CanUndo, "重置后撤销栈清空");
    }

    private static void SplitFormDraftCases()
    {
        var committed = new SplitSettings();
        var draft = SplitFormDraft.From(committed);
        Check.Equal("vertical", draft.Direction, "默认方向是纵向");
        Check.Equal("smart", draft.Type, "默认方式是智能");
        Check.Equal("percentage", draft.Unit, "默认单位是百分比");

        var (settings, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal(0, errors.Count, "默认草案能直接算出来");
        Check.Equal(2, settings.Count, "默认切 2 张");

        // 逐字段错误文案
        draft.Count = "abc";
        (_, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal("请输入正整数", errors.GetValueOrDefault("count"), "张数不是整数 → count 报错");
        draft.Count = "0";
        (_, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal("请输入正整数", errors.GetValueOrDefault("count"), "张数 0 → count 报错");
        draft.Count = "2";

        draft.Overlap = "-1";
        (_, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal("请输入非负整数", errors.GetValueOrDefault("overlap"), "重叠为负 → overlap 报错");
        draft.Overlap = "0";

        draft.Type = "precise";
        draft.Unit = "pixels";
        draft.Pixels = "10";
        (_, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal("请输入不小于 16 的整数", errors.GetValueOrDefault("pixels"),
            "每张像素 < 16 → pixels 报错");
        draft.Pixels = "16";

        draft.Unit = "percentage";
        draft.Percentage = "4";
        (_, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal("请输入 5% 到 100% 之间的百分比", errors.GetValueOrDefault("percentage"),
            "百分比低于 5% → percentage 报错");
        draft.Percentage = "50";

        // 宫格时「张数」是隐藏字段：写坏了也不该报错
        draft.Direction = "grid";
        draft.Count = "abc";
        (settings, errors) = draft.Resolve(committed, 1000, 600);
        Check.Equal(0, errors.Count, "宫格不读张数，隐藏字段的错字不算错");
        Check.Equal(SplitDirection.Grid, settings.Direction, "方向按原文解析");

        // 单位换算（照上游 changeUnit）
        var convert = SplitFormDraft.From(committed);
        Check.Equal("500", convert.ConvertSize(true, 1000), "百分比 50 + 1000 宽 → 500 像素");
        convert.Pixels = "500";
        Check.Equal("50.00", convert.ConvertSize(false, 1000), "500 像素 + 1000 宽 → 50.00%");
        convert.Percentage = "abc";
        Check.Equal("", convert.ConvertSize(true, 1000), "百分比写坏 → 换算给空串");
        convert.Pixels = "16";
        Check.Equal("1.60", convert.ConvertSize(false, 1000), "16 / 1000 → 1.60%");
    }

    private static void SplitExportCases()
    {
        Check.Equal("切图_a_b__c", SplitExport.DirectoryName("a<b>:c"), "非法字符换成下划线");
        Check.Equal("切图_abc", SplitExport.DirectoryName("abc. "), "结尾的点与空白去掉");
        Check.Equal("切图_截图", SplitExport.DirectoryName(""), "没有名字时叫「截图」");
        Check.Equal("切图_x", SplitExport.DirectoryName("  x  "), "首尾空白去掉");

        string dir = Path.Combine(Path.GetTempPath(),
            "ltools-selftest-split-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            string? first = SplitExport.CreateDirectory(dir, "shot");
            string? second = SplitExport.CreateDirectory(dir, "shot");
            Check.Equal(Path.Combine(dir, "切图_shot"), first, "第一次建「切图_shot」");
            Check.Equal(Path.Combine(dir, "切图_shot (2)"), second, "重名依次加 (2)");

            // 2×2 宫格：4 张，每张 50×50，逐张认得出来源
            var image = SplitImage(100, 100, (x, y) =>
                Color.FromRgb((byte)x, (byte)y, 0));
            var layout = SplitLayoutCalculator.Calculate(100, 100, new SplitSettings
            {
                Direction = SplitDirection.Grid,
                Rows = 2,
                Columns = 2,
            });

            var result = SplitExport.Export(image, layout, dir, "grid");
            Check.True(result.Ok, "导出成功");
            Check.Equal(4, (int)result.Saved, "2×2 宫格导出 4 张");
            var files = SplitExport.Files(result.Directory);
            Check.Equal(4, files.Count, "目录里有 4 个 png");
            Check.True(File.Exists(Path.Combine(result.Directory, "1.png")), "文件从 1.png 起编号");

            var one = BitmapFrame.Create(new Uri(Path.Combine(result.Directory, "1.png")));
            Check.Equal(50, one.PixelWidth, "第 1 张宽 50");
            Check.Equal(50, one.PixelHeight, "第 1 张高 50");
            var pixel = SplitPixelAt(one, 0, 0);
            Check.Equal(Color.FromRgb(0, 0, 0), pixel, "第 1 张左上角取自原图 (0,0)");

            var last = BitmapFrame.Create(new Uri(Path.Combine(result.Directory, "4.png")));
            var corner = SplitPixelAt(last, 0, 0);
            Check.Equal(Color.FromRgb(50, 50, 0), corner, "第 4 张左上角取自原图 (50,50)");

            // 取消：一张也不写
            var cancelled = SplitExport.Export(image, layout, dir, "cancel", () => true);
            Check.False(cancelled.Ok, "取消后不算成功");
            Check.Equal("导出已取消", cancelled.Error, "取消的文案");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清不干净不影响断言 */ }
        }
    }

    // ============ 小工具 ============

    private static void SplitLinesAre(SplitLines lines, IReadOnlyList<int> expected, string what) =>
        Check.True(lines.ToArray().SequenceEqual(expected),
            $"{what}（实际 [{string.Join(", ", lines.ToArray())}]）");

    private static void SplitIntsAre(IReadOnlyList<int> actual, IReadOnlyList<int> expected, string what) =>
        Check.True(actual.SequenceEqual(expected),
            $"{what}（实际 [{string.Join(", ", actual)}]）");

    private static BitmapSource SplitImage(int width, int height, Func<int, int, Color> color)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = color(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Color SplitPixelAt(BitmapSource image, int x, int y)
    {
        var one = new byte[4];
        var source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        source.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return Color.FromArgb(one[3], one[2], one[1], one[0]);
    }
}
