using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 选中标注后的样式族（P2-e2）：标注 → 样式族映射、值捕获、样式写回（几何与种子不动）、
/// 以及「只把变过的字段合并进创建样式」。
/// </summary>
/// <remarks>
/// 照上游 <c>style.rs</c> 的 <c>style_toolbar_source</c>（选中 → <c>Selected*</c> 来源）、
/// <c>set_shape_style_patch</c>（只 merge <c>properties</c>）与
/// <c>screenshottoolpalettestylecontrols.cpp</c> 的 <c>commit*Property</c>。
/// 这里全是纯数据，不开窗口。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void SelectionStyles()
    {
        // ============ 反向映射：每一族都能找到代表工具，并且工具再映射回同一族 ============
        foreach (var family in StyleFamilies.All)
        {
            var tool = StyleFamilies.ToolOf(family);
            Check.Equal(family, StyleFamilies.Of(tool), family + "：代表工具映射回同一族");
        }

        // ============ 标注 → 族 / 工具 ============
        var pen = new Mark.Stroke(new[] { new Point(0, 0), new Point(10, 10) },
            Color.FromRgb(0x2E, 0xB8, 0x72), 3, false, 1);
        ExpectFamily(pen, StyleFamilies.Pen, CaptureTool.Pen, "画笔");

        var highlighter = new Mark.Stroke(new[] { new Point(0, 0), new Point(10, 0) },
            Color.FromRgb(0xF4, 0xF4, 0xF5), 12, true, 0.5);
        ExpectFamily(highlighter, StyleFamilies.Highlighter, CaptureTool.Highlighter, "荧光笔");

        var shapeSpec = new ShapeSpec(ShapeKind.Ellipse, Color.FromRgb(0x3B, 0x82, 0xF6), 5,
            StrokeKind.Dashed, StrokeRendering.Artist, Color.FromRgb(0x2E, 0xB8, 0x72),
            FillKind.Solid, 9, 4242);
        var shape = new Mark.Shape(new Point(10, 10), new Point(50, 40), shapeSpec);
        ExpectFamily(shape, StyleFamilies.Shape, CaptureTool.Shape, "形状");

        var bubbleSpec = shapeSpec with { Kind = ShapeKind.ThoughtBubble, Stroke = Colors.Black };
        var bubble = new Mark.SpeechBubble(new Rect(20, 20, 80, 50), bubbleSpec);
        ExpectFamily(bubble, StyleFamilies.SpeechBubble, CaptureTool.SpeechBubble, "气泡");

        var arrowSpec = new ArrowSpec(Color.FromRgb(0x8B, 0x5C, 0xF6), 2, StrokeKind.Solid,
            StrokeRendering.Architect, ArrowType.Elbow, Arrowhead.Triangle, Arrowhead.Diamond,
            2, ArrowShaft.Tapered, Color.FromRgb(0xFF, 0xCC, 0xC7), 3, 1, 777);
        var arrow = new Mark.Arrow(new[] { new Point(0, 0), new Point(40, 20) }, arrowSpec);
        ExpectFamily(arrow, StyleFamilies.Arrow, CaptureTool.Arrow, "箭头");

        var line = new Mark.Line(new Point(0, 0), new Point(30, 10), Colors.Red, 4,
            StrokeKind.Dotted, LineType.Curve, StrokeRendering.Cartoonist, 99);
        ExpectFamily(line, StyleFamilies.Line, CaptureTool.Line, "直线");

        var polyline = new Mark.Polyline(new[] { new Point(0, 0), new Point(10, 5), new Point(20, 0) },
            Colors.Blue, 2, true);
        ExpectFamily(polyline, StyleFamilies.Polyline, CaptureTool.Polyline, "折线");

        var textSpec = new TextSpec("Consolas", 42, true, true, TextAlign.Center,
            Color.FromRgb(0xF5, 0x9E, 0x0B), 2, TextInk.DefaultStroke, Colors.Transparent,
            FillKind.Line, 4);
        var text = new Mark.Text(new Point(5, 6), "截图", textSpec);
        ExpectFamily(text, StyleFamilies.Text, CaptureTool.Text, "文字");

        var serialSpec = new SerialSpec(SerialBadge.SolidSquare, SerialNumbering.Roman,
            Color.FromRgb(0xE1, 0x5B, 0x4E), SerialSpec.DefaultFill, FillKind.CrossLine,
            30, "Segoe UI", 3, 0.8);
        var serial = new Mark.Serial(new Point(40, 40), 7, serialSpec);
        ExpectFamily(serial, StyleFamilies.Serial, CaptureTool.Serial, "序号");

        ExpectFamily(new Mark.Mosaic(new Point(0, 0), new Point(30, 30), 12),
            StyleFamilies.Mosaic, CaptureTool.Mosaic, "马赛克");

        var filter = new Mark.Filter(new Point(0, 0), new Point(30, 30), FilterKind.Emboss, 65, 3);
        ExpectFamily(filter, StyleFamilies.Filter, CaptureTool.Filter, "矩形滤镜");

        var brushFilter = new Mark.BrushFilter(new[] { new Point(0, 0), new Point(20, 20) },
            14, FilterKind.Grayscale, 20);
        ExpectFamily(brushFilter, StyleFamilies.BrushFilter, CaptureTool.BrushFilter, "画笔滤镜");

        ExpectFamily(new Mark.Spotlight(new Point(0, 0), new Point(40, 40), 0.7),
            StyleFamilies.Spotlight, CaptureTool.Spotlight, "聚光");

        ExpectFamily(new Mark.Highlight(new Point(0, 0), new Point(40, 20),
            Color.FromRgb(0x3B, 0x82, 0xF6), 0.4), StyleFamilies.HighlightRect, CaptureTool.HighlightRect, "矩形荧光");

        ExpectFamily(new Mark.Distance(new Point(0, 0), new Point(60, 0), Colors.Red, 2),
            StyleFamilies.Distance, CaptureTool.Distance, "测距");

        ExpectFamily(new Mark.Angle(new Point(0, 0), 60, 0.4, 80, Colors.Red, 2),
            StyleFamilies.Angle, CaptureTool.Angle, "测角");

        ExpectFamily(new Mark.Bracket(new Point(0, 0), new Point(40, 0), Colors.Red, 2),
            StyleFamilies.Bracket, CaptureTool.Bracket, "括号");

        var magnifierSpec = new MagnifierSpec(ShapeKind.Diamond, Color.FromRgb(0x2E, 0xB8, 0x72), 3,
            3, MagnifierConnection.LeaderLine, false, Arrowhead.CrowfootOne, 1.5, 12, 0.9);
        var magnifier = new Mark.Magnifier(new Rect(0, 0, 40, 30), magnifierSpec);
        ExpectFamily(magnifier, StyleFamilies.Magnifier, CaptureTool.Magnifier, "放大镜");

        // 水印（整张图效果）与恢复块（不可选）不归任何族
        Check.False(MarkStyler.TryFamily(new Mark.Watermark(WatermarkSpec.Default), out _, out _),
            "水印不参与选中样式");
        Check.False(MarkStyler.TryFamily(new Mark.Restore(new Point(0, 0), new Point(10, 10)), out _, out _),
            "恢复块不参与选中样式");

        // ============ 捕获 → 写回：几何与抖动种子不动，样式等价 ============
        RoundTrip(pen, "画笔");
        RoundTrip(highlighter, "荧光笔");
        RoundTrip(shape, "形状");
        RoundTrip(bubble, "气泡");
        RoundTrip(arrow, "箭头");
        RoundTrip(line, "直线");
        RoundTrip(polyline, "折线");
        RoundTrip(text, "文字");
        RoundTrip(serial, "序号");
        RoundTrip(new Mark.Mosaic(new Point(4, 4), new Point(44, 34), 12), "马赛克");
        RoundTrip(filter, "矩形滤镜");
        RoundTrip(brushFilter, "画笔滤镜");
        RoundTrip(new Mark.Spotlight(new Point(2, 2), new Point(42, 32), 0.7), "聚光");
        RoundTrip(new Mark.Highlight(new Point(2, 2), new Point(42, 22),
            Color.FromRgb(0x3B, 0x82, 0xF6), 0.4), "矩形荧光");
        RoundTrip(new Mark.Distance(new Point(3, 4), new Point(63, 4), Colors.Red, 2), "测距");
        RoundTrip(new Mark.Angle(new Point(5, 5), 60, 0.4, 80, Colors.Red, 2), "测角");
        RoundTrip(new Mark.Bracket(new Point(3, 3), new Point(43, 3), Colors.Red, 2), "括号");
        RoundTrip(magnifier, "放大镜");

        // ============ 改一项只写回一项：几何 / 种子 / 其它样式不动 ============
        var display = new ToolStyle();
        MarkStyler.Capture(shape, display);
        display.ShapeKind = ShapeKind.Diamond;
        display.Color = Color.FromRgb(0x10, 0x10, 0x14);
        var reshaped = (Mark.Shape)MarkStyler.Apply(shape, display);
        Check.Equal(new Point(10, 10), reshaped.A, "改形状不动起点");
        Check.Equal(new Point(50, 40), reshaped.B, "改形状不动终点");
        Check.Equal(ShapeKind.Diamond, reshaped.Spec.Kind, "形状换成菱形");
        Check.Equal(Color.FromRgb(0x10, 0x10, 0x14), reshaped.Spec.Stroke, "描边色跟着换");
        Check.Equal(shapeSpec.Radius, reshaped.Spec.Radius, "没改的圆角保持原值");
        Check.Equal(shapeSpec.Seed, reshaped.Spec.Seed, "手绘抖动种子不变（形状不会换个花纹）");
        Check.Equal(shapeSpec.Fill, reshaped.Spec.Fill, "没改的填充色保持原值");

        // 箭头换杆型：点列、端点、比例、外描边都不动
        display = new ToolStyle();
        MarkStyler.Capture(arrow, display);
        display.ArrowShaftType = ArrowShaft.Plain;
        var straight = (Mark.Arrow)MarkStyler.Apply(arrow, display);
        Check.True(arrow.Points.SequenceEqual(straight.Points), "换杆型不动点列");
        Check.Equal(ArrowShaft.Plain, straight.Spec.Shaft, "杆型换回等宽");
        Check.Equal(arrowSpec.Start, straight.Spec.Start, "起点端点不动");
        Check.Equal(arrowSpec.End, straight.Spec.End, "终点端点不动");
        Check.Equal(arrowSpec.Ratio, straight.Spec.Ratio, "箭头比例不动");
        Check.Equal(arrowSpec.OutlineWidth, straight.Spec.OutlineWidth, "外描边宽不动");
        Check.Equal(arrowSpec.Seed, straight.Spec.Seed, "箭头种子不动");

        // 放大镜只改倍率：两个中心与源尺寸都不动
        display = new ToolStyle();
        MarkStyler.Capture(magnifier, display);
        display.Factor = 5;
        var zoomed = (Mark.Magnifier)MarkStyler.Apply(magnifier, display);
        Check.Equal(5.0, zoomed.Factor, "倍率写回");
        Check.Equal(magnifier.LensCenter, zoomed.LensCenter, "镜头中心不动");
        Check.Equal(magnifier.SourceCenter, zoomed.SourceCenter, "源中心不动");
        Check.Equal(magnifier.SourceWidth, zoomed.SourceWidth, "源宽不动");

        // ============ 合并：只写变过的字段进创建样式 ============
        var creation = new ToolStyle
        {
            Width = 2, FontSize = 18, Block = 6, Color = Colors.Black,
            ArrowKind = ArrowType.Straight, SerialNumber = 3,
        };
        var start = creation.Clone();
        var edited = creation.Clone();
        edited.Color = Color.FromRgb(0xFF, 0xCC, 0xC7);
        edited.ArrowKind = ArrowType.Curve;

        edited.CopyChangedTo(creation, start);
        Check.Equal(Color.FromRgb(0xFF, 0xCC, 0xC7), creation.Color, "变过的颜色合并进去");
        Check.Equal(ArrowType.Curve, creation.ArrowKind, "变过的箭头类型合并进去");
        Check.Equal(2.0, creation.Width, "没变过的线宽不动");
        Check.Equal(18.0, creation.FontSize, "没变过的字号不动");
        Check.Equal(6.0, creation.Block, "没变过的块大小不动");

        // 序号计数器从不合并（选中一条旧序号时参数行照旧显示下一个号）
        creation.SerialNumber = 3;
        var counterStart = creation.Clone();
        var counterEdited = creation.Clone();
        counterEdited.SerialNumber = 9;
        counterEdited.Width = 7;
        counterEdited.CopyChangedTo(creation, counterStart);
        Check.Equal(7.0, creation.Width, "同一次合并里的其它字段照常写回");
        Check.Equal(3, creation.SerialNumber, "序号计数器不被选中项的号码覆盖");

        // DiffersFrom：改过的算变、没改的不算
        var before = new ToolStyle();
        var after = before.Clone();
        Check.False(after.DiffersFrom(before), "原样复制不算变");
        after.TextBold = !before.TextBold;
        Check.True(after.DiffersFrom(before), "翻转粗体算变");
        after = before.Clone();
        after.WatermarkGap += 1;
        Check.True(after.DiffersFrom(before), "水印间距算变（46 个字段一个不漏）");
    }

    private static void ExpectFamily(Mark mark, string family, CaptureTool tool, string label)
    {
        Check.True(MarkStyler.TryFamily(mark, out string actualFamily, out CaptureTool actualTool),
            label + "：有样式族");
        Check.Equal(family, actualFamily, label + "：族名");
        Check.Equal(tool, actualTool, label + "：参数行按它排");
        Check.Equal(family, StyleFamilies.Of(tool), label + "：工具与族对得上");
    }

    /// <summary>捕获 → 原样写回：必须得到与新造的那条等价的标注（几何 / 种子 / 样式都不动）。</summary>
    private static void RoundTrip(Mark mark, string label)
    {
        var style = new ToolStyle();
        MarkStyler.Capture(mark, style);
        var applied = MarkStyler.Apply(mark, style);

        Check.Equal(mark.GetType(), applied.GetType(), label + "：类型不变");
        Check.Equal(mark.Frame, applied.Frame, label + "：选中框（几何）不变");
        Check.True(SameMark(mark, applied), label + "：样式与种子原样写回");
    }

    /// <summary>逐类型比较「值全都一样」（标注不可变，等价 = 每个字段都相等）。</summary>
    private static bool SameMark(Mark a, Mark b) => (a, b) switch
    {
        (Mark.Stroke x, Mark.Stroke y) =>
            x.Points.SequenceEqual(y.Points) && x.Color == y.Color && x.Width == y.Width &&
            x.Highlighter == y.Highlighter && Math.Abs(x.Opacity - y.Opacity) < 1e-9,

        (Mark.Shape x, Mark.Shape y) => x.A == y.A && x.B == y.B && x.Spec == y.Spec,

        (Mark.SpeechBubble x, Mark.SpeechBubble y) => x.Body == y.Body && x.Spec == y.Spec,

        (Mark.Arrow x, Mark.Arrow y) => x.Points.SequenceEqual(y.Points) && x.Spec == y.Spec,

        (Mark.Line x, Mark.Line y) =>
            x.A == y.A && x.B == y.B && x.Color == y.Color && x.Width == y.Width &&
            x.StrokeStyle == y.StrokeStyle && x.Type == y.Type && x.Rendering == y.Rendering &&
            x.Seed == y.Seed,

        (Mark.Polyline x, Mark.Polyline y) =>
            x.Points.SequenceEqual(y.Points) && x.Color == y.Color && x.Width == y.Width &&
            x.Dashed == y.Dashed,

        (Mark.Text x, Mark.Text y) => x.Origin == y.Origin && x.Content == y.Content && x.Style == y.Style,

        (Mark.Serial x, Mark.Serial y) => x.Center == y.Center && x.Number == y.Number && x.Spec == y.Spec,

        (Mark.Mosaic x, Mark.Mosaic y) => x.A == y.A && x.B == y.B && x.Block == y.Block,

        (Mark.Filter x, Mark.Filter y) =>
            x.A == y.A && x.B == y.B && x.Kind == y.Kind && x.Strength == y.Strength &&
            x.AutoRegionId == y.AutoRegionId,

        (Mark.BrushFilter x, Mark.BrushFilter y) =>
            x.Points.SequenceEqual(y.Points) && x.Width == y.Width && x.Kind == y.Kind &&
            x.Strength == y.Strength,

        (Mark.Spotlight x, Mark.Spotlight y) => x.A == y.A && x.B == y.B && x.Dim == y.Dim,

        (Mark.Highlight x, Mark.Highlight y) =>
            x.A == y.A && x.B == y.B && x.Color == y.Color && x.Opacity == y.Opacity,

        (Mark.Distance x, Mark.Distance y) =>
            x.A == y.A && x.B == y.B && x.Color == y.Color && x.Width == y.Width && x.Spec == y.Spec,

        (Mark.Angle x, Mark.Angle y) =>
            x.Vertex == y.Vertex && x.Radius == y.Radius && x.Bisector == y.Bisector &&
            x.Degrees == y.Degrees && x.Color == y.Color && x.Width == y.Width && x.Spec == y.Spec,

        (Mark.Bracket x, Mark.Bracket y) =>
            x.A == y.A && x.Apex == y.Apex && x.B == y.B && x.Color == y.Color && x.Width == y.Width,

        (Mark.Magnifier x, Mark.Magnifier y) =>
            x.LensCenter == y.LensCenter && x.SourceCenter == y.SourceCenter &&
            x.SourceWidth == y.SourceWidth && x.SourceHeight == y.SourceHeight && x.Spec == y.Spec,

        _ => false,
    };
}
