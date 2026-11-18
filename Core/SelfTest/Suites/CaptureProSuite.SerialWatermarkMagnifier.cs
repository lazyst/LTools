using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

internal static partial class CaptureProSuite
{
    /// <summary>
    /// P2-d2 的三块纯逻辑：序号徽标的尺寸 / 写法、水印格距与模板展开、放大镜的引导线与渐变桥范围。
    /// 这些都在 <c>--selftest</c> 里跑，改公式时先看这里的断言（别只靠真机截图比对）。
    /// </summary>
    private static void SerialWatermarkMagnifier()
    {
        // ============ 序号徽标（上游 SnowCanvasSerialNumberStyle） ============

        // 直径 = 1.68 × 字号，与写法 / 位数无关；方形圆角 = 0.2 × 字号
        foreach (var size in new[] { 12d, 24d, 30d, 54d })
        {
            var spec = SerialSpec.Default with { FontSize = size };
            Check.True(Math.Abs(spec.Diameter - size * 1.68) < 1e-9, size + " 号：直径 = 1.68 × 字号");
            Check.True(Math.Abs(spec.CornerRadius - size * 0.2) < 1e-9, size + " 号：方形圆角 = 0.2 × 字号");
        }
        var wide = SerialSpec.Default with { FontSize = 24, Numbering = SerialNumbering.Roman };
        Check.Equal(wide.Diameter, (wide with { Numbering = SerialNumbering.Chinese }).Diameter,
            "换写法不改直径（VIII 不会把圆圈撑大）");

        // 五种徽标的「实心 / 方形 / 写不写数字」矩阵
        Check.True((SerialSpec.Default with { Badge = SerialBadge.OutlinedCircle }) is { Numbered: true, Solid: false, Square: false },
            "空心圆：写数字、不实心、不方");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.SolidCircle }) is { Numbered: true, Solid: true, Square: false },
            "实心圆：写数字、实心、不方");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.OutlinedSquare }) is { Numbered: true, Solid: false, Square: true },
            "空心方：写数字、不实心、方");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.SolidSquare }) is { Numbered: true, Solid: true, Square: true },
            "实心方：写数字、实心、方");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.Circle }) is { Numbered: false },
            "纯圆圈：不写数字（上游 supports_number() 只对它返回 false）");

        // 实心 = 描边色打底 + 对比字色；空心用描边色写字
        var solid = SerialSpec.Default with { Badge = SerialBadge.SolidCircle, Stroke = Colors.Black };
        Check.Equal(MarkRenderer.Contrast(Colors.Black), solid.TextColor, "实心徽标的字取对比色");
        Check.Equal(solid.Stroke, (solid with { Badge = SerialBadge.OutlinedCircle }).TextColor,
            "空心徽标的字就是描边色");

        // 徽标命中：圆按椭圆判，方按矩形判
        var round = new Mark.Serial(new Point(100, 100), 1, SerialSpec.Default with { Badge = SerialBadge.OutlinedCircle });
        Check.True(round.HitTest(new Point(120, 100), 0), "圆心右侧仍在圆里");
        Check.False(round.HitTest(new Point(139, 100), 0), "圆外右下角不命中（不是方框）");
        var square = new Mark.Serial(new Point(100, 100), 1, SerialSpec.Default with { Badge = SerialBadge.OutlinedSquare });
        Check.True(square.HitTest(new Point(119, 119), 0), "方徽标的角也命中");
        Check.Equal(40.32, square.Box.Width, "选中框是直径见方");

        // ============ 水印（上游 SnowCanvasWatermarkConfig） ============

        Check.True(Math.Abs(WatermarkSpec.Default.Step - Math.Max(56 + 16 * 4, 32)) < 1e-9,
            "默认格距 = max(间距 + 字号 × 4, 32)");
        Check.True(Math.Abs((new WatermarkSpec("x", Colors.Black, 2, "", 0, 10, 1)).Step - 32) < 1e-9,
            "字号小、间距小时格距有 32 的下限");
        Check.True(Math.Abs((new WatermarkSpec("x", Colors.Black, 30, "", 0, 200, 1)).Step - 320) < 1e-9,
            "大字号大间距照公式走");
        Check.True(Math.Abs(WatermarkSpec.Default.LineHeight - 19.2) < 1e-9, "默认行高 = 1.2 × 16");
        Check.Equal(string.Empty, WatermarkSpec.Default.Text, "默认水印文字为空（没填就什么都不画）");
        Check.Equal(30d, WatermarkSpec.Default.Angle, "默认角度 30°");
        Check.True(Math.Abs(WatermarkSpec.Default.Opacity - 0.16) < 1e-9, "默认不透明度 0.16");

        // 模板展开（照上游 resolve_watermark_template）
        var at = new DateTime(2026, 11, 18, 9, 7, 5);
        Check.Equal("机密", WatermarkTemplateText.Resolve("{text}", "机密", at), "{text} 换成当前文字");
        Check.Equal("机密 2026-11-18", WatermarkTemplateText.Resolve("{text} {YYYY}-{MM}-{DD}", "机密", at),
            "日期占位展开");
        Check.Equal("09:07:05", WatermarkTemplateText.Resolve("{HH}:{mm}:{ss}", "", at), "时间占位展开");
        Check.Equal("{foo}", WatermarkTemplateText.Resolve("{foo}", "机密", at), "认不出的占位原样保留");
        Check.Equal("nested {outer-{YYYY}}", WatermarkTemplateText.Resolve("nested {outer-{YYYY}}", "", at),
            "嵌套花括号整段原样保留（不当成时间占位）");
        Check.Equal("before {YYYY after", WatermarkTemplateText.Resolve("before {YYYY after", "", at),
            "没有闭合的花括号原样保留");
        Check.Equal("机密", WatermarkTemplateText.Resolve("", "机密", at), "空模板 = 直接用当前文字");
        Check.Equal("prefix-2026", WatermarkTemplateText.Resolve("{prefix-YYYY}", "", at),
            "带前缀的时间占位也展开");

        // ============ 放大镜（上游 document/src/magnifier.rs） ============

        var lensCenter = new Point(100, 50);
        var sourceCenter = new Point(400, 50);
        var leader = MagnifierInk.Leader(lensCenter, sourceCenter, 200, 100, ShapeKind.Rectangle, 0, 2);
        Check.True(leader.HasValue, "源在外面就有引导线");
        var (start, end) = leader!.Value;
        Check.Equal(sourceCenter, end, "引导线止于源中心");
        Check.True(start.X > 200, "起点在镜头轮廓外（含描边与空隙）");
        Check.True(Math.Abs(start.X - 210) < 0.05,
            "起点 = 镜头轮廓 + 半描边 + 8 DIP 空隙 + 半描边（上游 leader 的 high + gap + half_stroke）");
        Check.True(Math.Abs(start.Y - 50) < 0.01, "沿着镜头中心到源中心的直线");

        Check.False(MagnifierInk.Leader(lensCenter, lensCenter, 200, 100, ShapeKind.Rectangle, 0, 2).HasValue,
            "源在镜头里没有引导线");
        Check.False(MagnifierInk.Leader(lensCenter, new Point(100, 200), 200, 100, ShapeKind.ThoughtBubble, 0, 2).HasValue,
            "思考气泡不在放大镜家族里（没有引导线）");

        // 镜头 / 源矩形：镜头 = 源 × 倍率，源尺寸不乘倍率
        var lens = MagnifierInk.LensRect(lensCenter, 100, 50, 2);
        Check.Equal(new Rect(0, 0, 200, 100), lens, "镜头 = 源尺寸 × 倍率，中心是镜头中心");
        Check.Equal(new Rect(350, 25, 100, 50), MagnifierInk.SourceRectOf(sourceCenter, 100, 50),
            "源矩形按源中心摆，尺寸不乘倍率");

        // 渐变桥范围 = 镜头与源轮廓的凸包（镜头本身由裁剪挡掉，所以桥面只在镜头外）
        var lensRect = new Rect(0, 0, 100, 100);
        var sourceRect = new Rect(200, 20, 50, 50);
        var hull = MagnifierInk.Hull(lensRect, sourceRect, ShapeKind.Rectangle, 0);
        Check.True(hull.Count >= 4, "凸包至少有四个点");
        Check.True(hull.All(p => p.X >= -1e-9 && p.X <= 250 + 1e-9 && p.Y >= -1e-9 && p.Y <= 100 + 1e-9),
            "凸包不越出两个矩形的外接范围");
        Check.True(hull.Any(p => Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9), "凸包含镜头左上角");
        Check.True(hull.Any(p => Math.Abs(p.X - 250) < 1e-9), "凸包含源区域右上角");

        // 轮廓几何：椭圆是曲线、矩形带圆角、退化尺寸给空
        Check.True(MagnifierInk.Outline(new Rect(0, 0, 10, 10), ShapeKind.Ellipse, 0) is EllipseGeometry,
            "椭圆镜头用椭圆轮廓");
        Check.True(MagnifierInk.Outline(new Rect(0, 0, 10, 10), ShapeKind.Rectangle, 3) is RectangleGeometry,
            "矩形镜头用矩形轮廓");
        Check.True(MagnifierInk.Outline(new Rect(0, 0, 0, 0), ShapeKind.Rectangle, 0).IsEmpty(),
            "0 尺寸镜头给空几何（不抛）");

        // ============ 拖动语义（上游 hit_resolution.rs / magnifier_drag_style_tests.rs） ============

        var dragSpec = MagnifierSpec.Default with { Factor = 2 };
        var dragMark = new Mark.Magnifier(new Rect(0, 0, 200, 100), dragSpec);
        var movedAll = (Mark.Magnifier)dragMark.MoveBy(new Vector(30, 15));
        Check.Equal(dragMark.LensCenter + new Vector(30, 15), movedAll.LensCenter, "整块挪动：镜头跟着走");
        Check.Equal(dragMark.SourceCenter + new Vector(30, 15), movedAll.SourceCenter,
            "整块挪动：源中心一起走（上游 transform 同时映射两个中心）");
        Check.Equal(dragMark.Lens.Size, movedAll.Lens.Size, "整块挪动不改镜头大小");
        var lensOnly = (Mark.Magnifier)dragMark.WithLensCenter(dragMark.LensCenter + new Vector(30, 15));
        Check.Equal(dragMark.SourceCenter, lensOnly.SourceCenter, "只挪镜头时源中心不动");
        Check.Equal(dragMark.LensCenter + new Vector(30, 15), lensOnly.LensCenter, "只挪镜头时镜头跟着走");
        var sourceOnly = (Mark.Magnifier)dragMark.WithSourceCenter(new Point(500, 300));
        Check.Equal(dragMark.LensCenter, sourceOnly.LensCenter, "只挪源时镜头不动");

        // 移动手柄：镜头下缘 +24 DIP，14×14 的方块，命中半径 ±9（比看得见的大一圈）
        var gripLens = new Rect(0, 0, 200, 100);
        Check.Equal(new Point(100, 124), MagnifierInk.MoveGrip(gripLens), "手柄在镜头下缘正中 +24 DIP");
        Check.Equal(new Rect(93, 117, 14, 14), MagnifierInk.MoveGripRect(gripLens), "手柄是 14×14");
        Check.True(MagnifierInk.HitsMoveGrip(gripLens, new Point(100, 124)), "正中命中");
        Check.True(MagnifierInk.HitsMoveGrip(gripLens, new Point(109, 133)), "±9 的角上也命中");
        Check.False(MagnifierInk.HitsMoveGrip(gripLens, new Point(110, 124)), "超过 9 DIP 就不算抓手了");
        Check.False(MagnifierInk.HitsMoveGrip(gripLens, new Point(100, 60)), "镜头里面不是手柄");
    }
}
