using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

internal static partial class CaptureProSuite
{
    /// <summary>
    /// P2-b：形状族（上游 <c>buildShapeFamily</c>）——形状选择 / 描边线型 / 笔触风格 /
    /// 填充（实心 / 斜线 / 交叉）/ 圆角 / 直线曲线 / 画笔模式，以及气泡尾巴与思考气泡圆点。
    /// 纯数据断言（不碰窗口、不碰位图）。
    /// </summary>
    private static void ShapeStyles()
    {
        // ---- 参数表：形状族 / 箭头 / 直线 / 画笔各挂哪几格 ----
        foreach (var tool in new[] { CaptureTool.Shape, CaptureTool.SpeechBubble })
            foreach (var param in new[]
                     {
                         ToolParam.Color, ToolParam.Width, ToolParam.ShapeKind, ToolParam.StrokeStyle,
                         ToolParam.Rendering, ToolParam.FillColor, ToolParam.FillStyle, ToolParam.CornerRadius,
                     })
                Check.True(ToolCatalog.Uses(tool, param), $"{tool} 有 {param}");

        Check.False(ToolCatalog.Uses(CaptureTool.Shape, ToolParam.LineType), "形状没有直线 / 曲线");
        Check.False(ToolCatalog.Uses(CaptureTool.SpeechBubble, ToolParam.BrushMode), "气泡没有画笔模式");
        Check.True(ToolCatalog.Uses(CaptureTool.Line, ToolParam.StrokeStyle), "直线有描边线型");
        Check.True(ToolCatalog.Uses(CaptureTool.Line, ToolParam.Rendering), "直线有笔触风格");
        Check.True(ToolCatalog.Uses(CaptureTool.Line, ToolParam.LineType), "直线有直线 / 曲线");
        Check.True(ToolCatalog.Uses(CaptureTool.Arrow, ToolParam.StrokeStyle), "箭头有描边线型");
        Check.False(ToolCatalog.Uses(CaptureTool.Arrow, ToolParam.FillStyle), "箭头没有填充（上游 arrow_style 没有）");
        Check.True(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.BrushMode), "画笔有模式");
        Check.Equal("形状", ToolCatalog.Of(CaptureTool.Shape).Name, "合并后工具叫「形状」");
        Check.Equal(System.Windows.Input.Key.R, ToolCatalog.Of(CaptureTool.Shape).Shortcut, "形状仍是 R");

        // ---- 枚举文案：每个值都要有中文名（参数行 chip 与 tooltip 共用）----
        foreach (var kind in Enum.GetValues<ShapeKind>())
        {
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(kind)), kind + " 有 chip 标签");
            Check.True(ShapeLabel.Name(kind).StartsWith("形状：", StringComparison.Ordinal), kind + " 的 tooltip");
        }
        foreach (var strokeKind in Enum.GetValues<StrokeKind>())
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(strokeKind)), strokeKind + " 有中文名");
        foreach (var rendering in Enum.GetValues<StrokeRendering>())
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(rendering)), rendering + " 有中文名");
        foreach (var fill in Enum.GetValues<FillKind>())
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(fill)), fill + " 有中文名");
        foreach (var type in Enum.GetValues<LineType>())
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(type)), type + " 有中文名");
        foreach (var mode in Enum.GetValues<BrushMode>())
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(mode)), mode + " 有中文名");

        // ---- 轮廓：四种形状 ----
        var box = new Rect(0, 0, 200, 100);

        var square = ShapeInk.Contour(box, ShapeKind.Rectangle, 0);
        Check.Equal(4, square.Count, "直角矩形是四段");
        Check.True(square.All(s => s.Straight), "直角矩形没有曲线");

        var rounded = ShapeInk.Contour(box, ShapeKind.Rectangle, 6);
        Check.Equal(8, rounded.Count, "圆角矩形是 4 直线 + 4 贝塞尔");
        Check.Equal(4, rounded.Count(s => !s.Straight), "四个角各一段贝塞尔");
        var roundedBig = ShapeInk.Contour(box, ShapeKind.Rectangle, 999);
        var roundedCap = ShapeInk.Contour(box, ShapeKind.Rectangle, 50);
        Check.Equal(roundedCap.Count, roundedBig.Count, "矩形圆角上限 = 短边 / 2");
        Check.Equal(roundedCap[0].B, roundedBig[0].B, "超过上限按上限算（不是继续变圆）");

        var ellipse = ShapeInk.Contour(box, ShapeKind.Ellipse, 12);
        Check.Equal(4, ellipse.Count, "椭圆用四段贝塞尔");
        var ellipsePoints = ShapeInk.Flatten(ellipse, 32);
        Check.True(ellipsePoints.Max(p => p.X) > 199.9 && ellipsePoints.Max(p => p.Y) > 99.9,
            "椭圆打到外接框的右边与下边");
        Check.True(ellipsePoints.Min(p => p.X) > -0.001 && ellipsePoints.Min(p => p.Y) > -0.001,
            "椭圆不越界");
        Check.True(ellipsePoints.Count(p => Math.Abs(p.Y) < 0.001 || Math.Abs(p.Y - 100) < 0.001) >= 2,
            "椭圆上下各有一个极值点");

        var diamond = ShapeInk.Contour(box, ShapeKind.Diamond, 0);
        Check.Equal(4, diamond.Count, "无圆角菱形是四段直线");
        Check.True(diamond.Any(s => Math.Abs(s.B.X - 200) < 0.001 && Math.Abs(s.B.Y - 50) < 0.001),
            "菱形的右顶点在 (200,50)（边的中点）");
        var diamondBig = ShapeInk.Contour(box, ShapeKind.Diamond, 999);
        var diamondCap = ShapeInk.Contour(box, ShapeKind.Diamond, 25);
        Check.Equal(diamondCap.Count, diamondBig.Count, "菱形圆角上限 = 短边 / 4");
        Check.True(diamondBig.Count > diamond.Count, "带圆角的菱形多出圆弧段");

        var cloud = ShapeInk.Contour(box, ShapeKind.ThoughtBubble, 12);
        Check.Equal(14, cloud.Count, "思考气泡是 14 段手绘云朵");
        Check.True(cloud.All(s => !s.Straight), "云朵全是贝塞尔");
        var cloudPoints = ShapeInk.Flatten(cloud, 8);
        Check.True(cloudPoints.Max(p => p.X) > 199.5 && cloudPoints.Max(p => p.Y) > 99.5,
            "云朵撑满外接框（四个极值贴着框）");
        Check.True(cloudPoints.Min(p => p.X) < 0.5 && cloudPoints.Min(p => p.Y) < 0.5, "云朵左边与上边也贴着框");

        // ---- 填充 / 描边：命中口径与虚点线 ----
        var spec = new ShapeSpec(ShapeKind.Rectangle, Colors.Red, 4, StrokeKind.Solid,
            StrokeRendering.Architect, Colors.Transparent, FillKind.None, 0, 7);

        var outlineOnly = new Mark.Shape(new Point(0, 0), new Point(200, 100), spec);
        Check.False(outlineOnly.HitTest(new Point(100, 50), 0), "没填充：内部不命中");
        Check.True(outlineOnly.HitTest(new Point(100, 0), 1), "没填充：边上命中");

        var filled = new Mark.Shape(new Point(0, 0), new Point(200, 100),
            spec with { FillStyle = FillKind.Solid, Fill = Colors.White });
        Check.True(filled.HitTest(new Point(100, 50), 0), "填了色：内部命中");
        Check.False(filled.HitTest(new Point(400, 400), 0), "填了色：框外仍不命中");

        Check.Equal(0, ShapeInk.DashPattern(StrokeKind.Solid).Length, "实线没有 dash 段");
        Check.Equal(4d, ShapeInk.DashPattern(StrokeKind.Dashed)[0], "虚线 4/4（上游原值，别再除线宽）");
        Check.Equal(4d, ShapeInk.DashPattern(StrokeKind.Dashed)[1], "虚线 4/4");
        Check.True(Math.Abs(ShapeInk.DashPattern(StrokeKind.Dotted)[0] - 0.01) < 1e-9,
            "点线第一段 0.01（圆头画出来就是一个点）");
        Check.Equal(3d, ShapeInk.DashPattern(StrokeKind.Dotted)[1], "点线间隔 3");

        // ---- 笔触风格：手绘会超出去，干净不超；同样的 seed 画出来一样 ----
        Check.Equal(0d, ShapeInk.Outset(StrokeRendering.Architect), "干净不多占地方");
        Check.Equal(2d, ShapeInk.Outset(StrokeRendering.Artist), "手绘最多偏 2 DIP");
        Check.Equal(4d, ShapeInk.Outset(StrokeRendering.Cartoonist), "涂鸦最多偏 4 DIP");
        Check.True(ShapeInk.NextSeed() != ShapeInk.NextSeed(), "每条标注各拿一个种子");
        Check.True((ShapeInk.NextSeed() & 1u) == 1u, "种子固定为奇数（哈希采样的起点）");

        var jittered = new Mark.Shape(new Point(0, 0), new Point(200, 100),
            spec with { Rendering = StrokeRendering.Cartoonist });
        var jitterMoved = (Mark.Shape)jittered.MoveBy(new Vector(10, 10));
        Check.Equal(jittered.Spec.Seed, jitterMoved.Spec.Seed, "平移不改抖动种子（移动时线条不自己动）");
        var jitterScaled = (Mark.Shape)jittered.ScaleTo(jittered.Frame,
            new Rect(0, 0, 400, 200));
        Check.Equal(jittered.Spec.Seed, jitterScaled.Spec.Seed, "缩放也不改种子");
        Check.True(jitterScaled.Width > jittered.Width, "缩放把线宽一起放大");
        Check.True(jittered.Bounds.Height > outlineOnly.Bounds.Height,
            "涂鸦的外接框比干净的大（抖动预留了 4 DIP）");

        // ---- 气泡：尾巴根部取自轮廓，身体边线不横穿尾巴 ----
        var bubble = new Mark.SpeechBubble(box, Colors.Red, 4);
        Check.True(bubble.Bases is { } bases && (bases.Left - bases.Right).Length > 1,
            "尾巴尖在身体外面：找得到两个根部");
        Check.Equal(3, bubble.Tail.Length, "尾巴仍是三个点");
        Check.Equal(bubble.Tip, bubble.Tail[2], "第三点是尾巴尖");
        Check.True(Math.Abs(bubble.Tail[0].Y - 100) < 0.6 && Math.Abs(bubble.Tail[1].Y - 100) < 0.6,
            "两个根部都落在身体下边缘上");
        Check.True(Math.Abs((bubble.Tail[0] - bubble.Tail[1]).Length - bubble.TailWidth) < 2,
            "根部张开宽度 = 尾巴宽度");
        Check.Equal(1, bubble.Parts.Count, "三角尾巴只有一个闭合轮廓");
        Check.True(bubble.Parts[0].Segments.Count >= bubble.BodyOutline.Count + 2,
            "身体轮廓切开后接上两条腿");
        Check.True(bubble.Parts[0].Segments.Count <= bubble.BodyOutline.Count + 4,
            "切开最多把两段各劈一次（不会凭空多出一圈）");
        Check.True(bubble.Parts[0].Closed, "尾巴轮廓是闭合的（能填充）");
        Check.True(bubble.HitTest(new Point(100, 50), 0), "气泡内部命中（描边就算有内容）");
        Check.True(bubble.HitTest(bubble.Tip, 1), "尾巴尖上也命中");
        Check.False(bubble.HitTest(new Point(400, 400), 0), "气泡外不命中");
        Check.Equal(136d, bubble.Frame.Bottom, "尾巴尖算进选中框");

        var rectangleBubble = new Mark.SpeechBubble(box, spec);
        Check.True(rectangleBubble.Trail.Count == 0, "矩形气泡没有圆点尾巴");

        // 尾巴尖落进身体里 → 没有尾巴（回到纯身体轮廓）
        var inner = new Mark.SpeechBubble(box, spec);
        var innerTail = ShapeInk.TailBases(inner.BodyOutline, inner.Body, new Point(100, 50), 40);
        Check.True(innerTail == null, "尾巴尖在身体里面就没有尾巴");

        // ---- 思考气泡：云朵 + 由大到小的圆点 ----
        var thought = new Mark.SpeechBubble(box, spec with { Kind = ShapeKind.ThoughtBubble });
        var trail = thought.Trail;
        Check.True(trail.Count >= 1 && trail.Count <= 3, "思考气泡圆点 1–3 个");
        Check.Equal(1 + trail.Count, thought.Parts.Count, "身体 + 每个圆点各一块轮廓");
        Check.True(thought.Tail.Length == 3, "思考气泡的 Tail 仍是三点（旧断言不炸）");
        Check.True(thought.Bases == null, "思考气泡没有三角尾巴根部");

        var center = new Point(100, 50);
        for (int i = 0; i < trail.Count; i++)
        {
            var (at, radius) = trail[i];
            var delta = at - center;
            double boundary = ShapeInk.RayRadius(thought.BodyOutline, center, Math.Atan2(delta.Y, delta.X));
            Check.True(delta.Length > boundary, $"第 {i + 1} 个圆点在身体外面");
            Check.True(radius > 0.25, $"第 {i + 1} 个圆点有实际大小");
            if (i > 0) Check.True(radius < trail[i - 1].Radius, $"第 {i + 1} 个圆点比上一个小");
        }

        var circleFrame = new Mark.SpeechBubble(box, spec with { Kind = ShapeKind.ThoughtBubble });
        Check.True(circleFrame.Frame.Bottom >= 100, "思考气泡的选中框覆盖身体");

        // ---- 样式 → 形状：一处换算 ----
        var style = new ToolStyle
        {
            ShapeKind = ShapeKind.Diamond,
            StrokeStyle = StrokeKind.Dotted,
            Rendering = StrokeRendering.Artist,
            FillStyle = FillKind.Line,
            FillColor = Colors.Gold,
            CornerRadius = 9,
            Width = 3,
        };
        var made = style.ShapeSpecOf(77);
        Check.Equal(ShapeKind.Diamond, made.Kind, "样式带出形状");
        Check.Equal(StrokeKind.Dotted, made.StrokeStyle, "样式带出线型");
        Check.Equal(StrokeRendering.Artist, made.Rendering, "样式带出笔触");
        Check.Equal(FillKind.Line, made.FillStyle, "样式带出填充方式");
        Check.Equal(Colors.Gold, made.Fill, "样式带出填充色");
        Check.Equal(9d, made.Radius, "样式带出圆角");
        Check.Equal(3d, made.Width, "样式带出线宽");
        Check.Equal(77u, made.Seed, "种子原样带过去");
        Check.Equal(ToolStyle.DefaultFillColor, new ToolStyle().FillColor, "默认填充色是上游的 #FFCCC7");
        Check.Equal(6d, new ToolStyle().CornerRadius, "默认圆角 6（上游 cornerRadii）");
        Check.Equal(FillKind.None, new ToolStyle().FillStyle, "默认填充 = 上游的 Border（无）");
        Check.Equal(StrokeRendering.Architect, new ToolStyle().Rendering, "默认笔触是干净");
        Check.Equal(LineType.Curve, new ToolStyle().LineType, "默认线型是曲线（上游 arrowType）");
        Check.Equal(BrushMode.Regular, new ToolStyle().BrushMode, "默认画笔是常规");
    }
}
