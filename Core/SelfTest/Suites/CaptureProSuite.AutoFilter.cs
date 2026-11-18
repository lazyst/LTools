using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;

namespace LTools.Core.SelfTest;

/// <summary>
/// 自动打码（AutoFilter）的区域检测：固定夹具 + 与上游逐区域对账的期望值。
/// </summary>
/// <remarks>
/// 夹具是代码画出来的合成界面（面板 / 标题 / 消息框 / 照片 / 头像 / 图标 / 三点菜单 /
/// 段落），期望值是上游 <c>visual-region-detector</c>（Rust，commit 637f8ca）跑同一份
/// 像素得到的完整列表——顺序、坐标、类型都要一致，否则就是移植走样。
/// 对账步骤见 <c>docs/CAPTURE_PARITY.md</c>。
/// </remarks>
internal static partial class CaptureProSuite
{
    /// <summary>合成夹具：460×340 的 BGR 图，像素完全由固定种子决定。</summary>
    internal static BgrImg AutoFilterFixture()
    {
        const int W = 460;
        const int H = 340;
        var image = new BgrImg(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                image.Set(x, y, 242, 242, 242);

        uint rng = 0x5EED_1234;

        void Fill(int x, int y, int w, int h, byte b, byte g, byte r)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                    image.Set(xx, yy, b, g, r);
        }

        void Glyphs(int x, int y, int w, int h, byte ink)
        {
            int cx = x;
            while (cx < x + w)
            {
                int cw = 5 + Next(ref rng, 3);
                for (int yy = y; yy < y + h; yy++)
                    for (int xx = cx; xx < Math.Min(cx + cw, x + w); xx++)
                    {
                        bool edge = xx == cx || xx == cx + cw - 1 || yy == y || yy == y + h - 1;
                        if (edge || Next(ref rng, 100) < 45) image.Set(xx, yy, ink, ink, ink);
                    }

                cx += cw + 1 + Next(ref rng, 3);
            }
        }

        // 面板（浅色卡片 + 1px 描边）
        Fill(120, 40, 300, 190, 252, 252, 252);
        Fill(120, 40, 300, 1, 205, 205, 205);
        Fill(120, 229, 300, 1, 205, 205, 205);
        Fill(120, 40, 1, 190, 205, 205, 205);
        Fill(419, 40, 1, 190, 205, 205, 205);

        // 卡片标题
        Glyphs(132, 52, 180, 15, 40);

        // 三点菜单
        for (int i = 0; i < 3; i++) Fill(300 + i * 10, 56, 3, 3, 40, 40, 40);

        // 消息框（框内两行文字）
        Fill(150, 140, 170, 64, 224, 224, 224);
        Glyphs(160, 150, 120, 12, 30);
        Glyphs(160, 168, 140, 12, 30);

        // 照片块（噪声）
        for (int y = 245; y < 317; y++)
            for (int x = 330; x < 410; x++)
            {
                byte v = (byte)Next(ref rng, 256);
                image.Set(x, y, (byte)(v / 2 + 30), v, (byte)(255 - v));
            }

        // 头像（实心圆）
        for (int y = 126; y <= 174; y++)
            for (int x = 28; x <= 76; x++)
            {
                int dx = x - 52, dy = y - 150;
                if (dx * dx + dy * dy <= 24 * 24) image.Set(x, y, 58, 167, 109);
            }

        // 小图标
        Fill(330, 215, 18, 18, 150, 80, 20);

        // 左对齐三行 → 段落
        Glyphs(20, 250, 200, 13, 40);
        Glyphs(20, 274, 170, 13, 40);
        Glyphs(20, 298, 190, 13, 40);

        return image;
    }

    private static int Next(ref uint state, int max)
    {
        state = state * 1664525u + 1013904223u;
        return (int)((state >> 16) % (uint)max);
    }

    /// <summary>检测：夹具上的区域列表与上游逐一对应（顺序 / 坐标 / 类型）。</summary>
    private static void AutoFilterDetect()
    {
        var expected = new (int X, int Y, int W, int H, string Kind)[]
        {
            (17, 248, 206, 66, RegionCategories.TextBlock),
            (17, 247, 206, 19, RegionCategories.Text),
            (129, 49, 197, 21, RegionCategories.Text),
            (17, 295, 195, 19, RegionCategories.Text),
            (17, 271, 176, 19, RegionCategories.Text),
            (150, 140, 170, 64, RegionCategories.MessageBox),
            (157, 165, 146, 18, RegionCategories.TextInBox),
            (157, 147, 126, 18, RegionCategories.TextInBox),
            (329, 244, 82, 74, RegionCategories.Image),
            (26, 124, 53, 53, RegionCategories.Avatar),
            (330, 215, 18, 18, RegionCategories.Icon),
        };

        var regions = AutoFilterDetector.Detect(AutoFilterFixture());
        Check.Equal(expected.Length, regions.Count, "区域数量");
        for (int i = 0; i < Math.Min(expected.Length, regions.Count); i++)
        {
            var e = expected[i];
            var a = regions[i];
            Check.Equal($"{e.X},{e.Y},{e.W},{e.H} {e.Kind}",
                $"{a.Bounds.X},{a.Bounds.Y},{a.Bounds.W},{a.Bounds.H} {a.Category}", $"第 {i + 1} 个区域");
            Check.Equal(i + 1, a.Id, $"第 {i + 1} 个区域的 id 是输出序号");
        }

        // 类别顺序就是上游 C ABI 的枚举顺序（下拉与 id 都按它）
        Check.Equal("text,text_in_box,image,avatar,icon,message_box,text_block",
            string.Join(',', RegionCategories.All), "类别顺序");

        // 平坦底图不该报出任何可打码区域；空图直接给空列表
        var flat = new BgrImg(200, 160);
        for (int i = 0; i < flat.Data.Length; i += 3)
        {
            flat.Data[i] = 240;
            flat.Data[i + 1] = 240;
            flat.Data[i + 2] = 240;
        }

        Check.Equal(0, AutoFilterDetector.Detect(flat).Count, "纯色图不出区域");
        Check.Equal(0, AutoFilterDetector.Detect(new BgrImg(0, 0)).Count, "空图不出区域");
    }

    /// <summary>
    /// 自动打码的画布逻辑：坐标换算 / 命中 / 框选 / 填充事务 / 绘制顺序归一。
    /// 每一条都能在上游 <c>auto_filter.rs</c> + <c>auto_filter_workflow.rs</c> 里找到对应实现。
    /// </summary>
    private static void AutoFilterLogicChecks()
    {
        // 检测图的像素 → 窗口 DIP：按选区宽高比缩放，再与选区求交（上游 finish() 那段换算）
        var bounds = new Rect(100, 50, 400, 300);
        var detected = new List<DetectedRegion>
        {
            new(1, new RRect(0, 0, 200, 150), RegionCategories.Text),
            new(2, new RRect(190, 140, 40, 40), RegionCategories.Image),   // 右下角超出像素范围：被选区裁掉一部分
            new(3, new RRect(300, 300, 20, 20), RegionCategories.Icon),    // 完全在外：丢掉
        };
        var projected = AutoFilterLogic.Project(detected, bounds, 200, 150);
        Check.Equal(2, projected.Count, "越界的区域被丢掉");
        Check.Equal("100,50,400,300",
            $"{projected[0].Bounds.X:0},{projected[0].Bounds.Y:0},{projected[0].Bounds.Width:0},{projected[0].Bounds.Height:0}",
            "整块区域铺满选区");
        Check.Equal("500,350", $"{projected[1].Bounds.Right:0},{projected[1].Bounds.Bottom:0}", "贴边的区域被裁到选区内");
        Check.Equal(1, projected[0].Id, "id 按上游输出顺序保留");

        // 命中：最小面积优先，同面积取 id 小的那块（上游 region_at）
        var regions = new List<AutoRegion>
        {
            new(1, new Rect(0, 0, 200, 200), RegionCategories.TextBlock),   // 整页正文块：面积最大
            new(2, new Rect(20, 20, 10, 10), RegionCategories.Icon),
            new(3, new Rect(60, 20, 10, 10), RegionCategories.Image),
            new(4, new Rect(20, 20, 10, 10), RegionCategories.Avatar),      // 与 2 完全重叠、面积相同
        };
        Check.Equal(2, AutoFilterLogic.RegionAt(regions, new Point(25, 25))?.Id, "光标下取面积最小的那块");
        Check.Equal(2, AutoFilterLogic.RegionAt(regions, new Point(20, 20))?.Id, "同面积取 id 小的");
        Check.Equal(1, AutoFilterLogic.RegionAt(regions, new Point(150, 150))?.Id, "只有大块覆盖时取大块");
        Check.Equal(null, AutoFilterLogic.RegionAt(regions, new Point(400, 400))?.Id, "没命中 = 没有");
        Check.Equal(null, AutoFilterLogic.RegionAt(regions, new Point(200, 20))?.Id, "右边不进（半开区间）");

        // 框选：按「相交」算，不是包含；贴边不算相交
        var small = new List<AutoRegion>
        {
            new(2, new Rect(30, 30, 10, 10), RegionCategories.Icon),
            new(3, new Rect(60, 60, 10, 10), RegionCategories.Image),
            new(4, new Rect(65, 30, 10, 10), RegionCategories.Avatar),      // 左边正好贴住框的右边
        };
        Check.Equal("2,3", string.Join(',', AutoFilterLogic.RegionsIn(small, new Rect(35, 35, 30, 30))),
            "框选命中所有相交的区域");
        Check.False(AutoFilterLogic.RegionsIn(small, new Rect(35, 35, 30, 30)).Contains(4),
            "贴边不算相交（左边界 65 = 框右边 65）");
        Check.Equal(1, AutoFilterLogic.RegionsIn(small, new Rect(0, 0, 35, 35)).Count, "只擦到一角也算命中");

        // 填充事务：toggle 同类型 = 删掉，不同类型 = 替换；toggle=false 只补不删
        var marks = new List<Mark>();
        var one = new[] { regions[1] };
        Check.True(AutoFilterLogic.Fill(marks, regions, new[] { 2 }, FilterKind.Mosaic, 50, toggle: true), "第一次填充");
        Check.Equal(1, marks.Count, "落了一条滤镜");
        Check.Equal(2, ((Mark.Filter)marks[0]).AutoRegionId, "填充记住了区域 id");
        Check.False(AutoFilterLogic.Fill(marks, regions, new[] { 2 }, FilterKind.Mosaic, 50, toggle: false),
            "同类型同强度再填一次：没有变化（不刷撤销栈）");
        Check.True(AutoFilterLogic.Fill(marks, regions, new[] { 2 }, FilterKind.Mosaic, 50, toggle: true), "同类型 toggle：删掉");
        Check.Equal(0, marks.Count, "删掉了");

        AutoFilterLogic.Fill(marks, regions, one.Select(r => r.Id).ToArray(), FilterKind.Blur, 30, toggle: false);
        AutoFilterLogic.Fill(marks, regions, one.Select(r => r.Id).ToArray(), FilterKind.Mosaic, 60, toggle: false);
        Check.Equal(1, marks.Count, "不同类型 = 原地替换，不是再加一条");
        Check.Equal(FilterKind.Mosaic, ((Mark.Filter)marks[0]).Kind, "换成新类型");
        Check.True(AutoFilterLogic.FilledWith(marks, 2, FilterKind.Mosaic), "同类型算「已填」（高亮绿）");
        Check.False(AutoFilterLogic.FilledWith(marks, 2, FilterKind.Blur), "不同类型不算（高亮红）");

        // 绘制顺序归一：智能擦除 → 自动填充（按 id）→ 其余，其余保持原序
        var doc = new List<Mark>
        {
            new Mark.Shape(new Point(0, 0), new Point(10, 10), Colors.Red, 2, false, ellipse: false),
            new Mark.Filter(new Point(0, 0), new Point(5, 5), FilterKind.Mosaic, 50, 3),
            new Mark.Filter(new Point(0, 0), new Point(5, 5), FilterKind.SmartErase, 50),
            new Mark.Filter(new Point(0, 0), new Point(5, 5), FilterKind.Mosaic, 50, 1),
            new Mark.Arrow(new Point(0, 0), new Point(10, 10), Colors.Red, 2),
        };
        AutoFilterLogic.NormalizeOrder(doc);
        Check.True(doc[0] is Mark.Filter { Kind: FilterKind.SmartErase }, "智能擦除排最前面");
        Check.Equal(1, ((Mark.Filter)doc[1]).AutoRegionId, "自动填充按区域 id 升序");
        Check.Equal(3, ((Mark.Filter)doc[2]).AutoRegionId, "id 小的在前");
        Check.True(doc[3] is Mark.Shape, "其余标注排在自动填充之后");
        Check.True(doc[4] is Mark.Arrow, "其余标注保持原序");

        // 自动打码用的滤镜类型：类型停在智能擦除时回落成马赛克（上游 style.rs 的同一条规则）
        var style = new ToolStyle();
        style.FilterMode = FilterKind.SmartErase;
        Check.Equal(FilterKind.Mosaic, style.FilterForAutoFill, "智能擦除不给自动打码 → 回落马赛克");
        style.FilterMode = FilterKind.Blur;
        Check.Equal(FilterKind.Blur, style.FilterForAutoFill, "其它类型照用");

        // 马赛克滤镜：块边长 = (2 + 20 × 强度²) × 缩放，块内取平均
        var pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 4 + x) * 4;
                byte v = (byte)(x < 2 ? 0 : 255);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        MarkRenderer.Process(FilterKind.Mosaic, 50, pixels, 4, 4, 1);
        Check.Equal((byte)127, pixels[0], "整块取平均（左半黑右半白 → 127）");
        Check.Equal((byte)255, pixels[3], "alpha 不动");
        Check.Equal(pixels[0], pixels[(3 * 4 + 3) * 4], "同一块里像素一致");

        // 界面状态：结果配不配当前选区 / 扫描光带相位 / 闪红
        var state = new AutoFilterState();
        Check.False(state.Matches(bounds), "没有结果时不算数（「填充区域」置灰）");
        state.Set(bounds, projected);
        Check.True(state.Matches(bounds), "结果与选区对得上");
        Check.False(state.Matches(new Rect(0, 0, 400, 300)), "选区变了就不算数");
        Check.True(AutoFilterLogic.BoundsMatch(bounds, new Rect(100, 50, 400, 300)), "同一块选区");
        Check.False(AutoFilterLogic.BoundsMatch(bounds, new Rect(100, 50, 400, 301)), "差一个像素就是另一块");

        var scan = new AutoFilterState();
        scan.Set(new Rect(0, 100, 200, 400), Array.Empty<AutoRegion>());
        Check.True(Math.Abs(scan.ScanY(0) - 70) < 1e-9, "光带从选区上方 30 开始");
        Check.True(Math.Abs(scan.ScanY(700) - 300) < 1e-9, "半圈走到选区中段");
        Check.True(Math.Abs(scan.ScanY(1400) - scan.ScanY(0)) < 1e-9, "1400ms 一圈");
        Check.False(scan.Flashing(1000), "没到时间不闪");
        scan.FlashUntil = 1200;
        Check.True(scan.Flashing(1100), "结果刚到闪 300ms");
        Check.False(scan.Flashing(1200), "到点就不闪了");
    }
}
