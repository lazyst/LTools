using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

internal static partial class CaptureProSuite
{
    /// <summary>
    /// P2-d：端点的几何口径（尺寸 / 角度 / 比例 / 圆直径）、测距的数值与文案、
    /// 测角的内角折叠与单位、花括号的三点几何，以及这些参数的落盘往返与钳制。
    /// </summary>
    private static void MeasureStyles()
    {
        // ---- 端点尺寸 / 角度 / 分类（上游 render_core.rs 的 get_arrowhead_size 等）----
        Check.Equal(25.0 * 1.25, ArrowInk.Size(Arrowhead.Arrow), "箭头基值 25 × 1.25");
        Check.Equal(12.0 * 1.25, ArrowInk.Size(Arrowhead.Diamond), "菱形基值 12 × 1.25");
        Check.Equal(20.0 * 1.25, ArrowInk.Size(Arrowhead.CrowfootMany), "多趾基值 20 × 1.25");
        Check.Equal(15.0 * 1.25, ArrowInk.Size(Arrowhead.Circle), "其余（含圆点）基值 15 × 1.25");
        Check.Equal(90.0, ArrowInk.AngleDegrees(Arrowhead.Bar), "横杠的张角 90°");
        Check.Equal(20.0, ArrowInk.AngleDegrees(Arrowhead.Arrow), "箭头张角 20°");
        Check.Equal(25.0, ArrowInk.AngleDegrees(Arrowhead.Triangle), "其余张角 25°");
        Check.True(ArrowInk.IsRound(Arrowhead.CircleOutline), "空心圆是圆类");
        Check.True(ArrowInk.IsDiamond(Arrowhead.Square), "方块与菱形同一套 0.25 比例");
        Check.False(ArrowInk.IsRound(Arrowhead.Bar), "横杠不是圆类");

        // ---- 端点比例：0.5–3.0，坏值回落 1 ----
        Check.Equal(0.5, ArrowInk.Ratio(0.1), "比例下限 0.5");
        Check.Equal(3.0, ArrowInk.Ratio(9), "比例上限 3.0");
        Check.Equal(1.0, ArrowInk.Ratio(double.NaN), "坏比例回落 1");

        // ---- 测距：单位后缀 / 系数 / 小数位 ----
        Check.Equal("μm", MeasureLabel.Suffix(DistanceUnit.Um), "微米写成 μm");
        Check.Equal("px", MeasureLabel.Suffix(DistanceUnit.Px), "像素单位");
        Check.Equal(9, Enum.GetValues<DistanceUnit>().Length, "测距共 9 种单位");
        Check.Equal("100 cm", MeasureLabel.Distance(100, 1, 1, DistanceUnit.Cm, 0), "长度 × 缩放 × 系数 + 单位");
        Check.Equal("200 cm", MeasureLabel.Distance(100, 2, 1, DistanceUnit.Cm, 0), "物理缩放跟着乘");
        Check.Equal("300 cm", MeasureLabel.Distance(100, 1, 3, DistanceUnit.Cm, 0), "系数跟着乘");
        Check.Equal("1.5 cm", MeasureLabel.Distance(150, 1, 0.01, DistanceUnit.Cm, 1), "小数位按参数保留一位");
        Check.Equal(6.0, MeasureLabel.DistanceValue(3, 2, 1), "DistanceValue = 长度 × 缩放 × 系数");
        Check.Equal(3, MeasureLabel.ClampDecimals(99), "小数位上限 3");
        Check.Equal(0, MeasureLabel.ClampDecimals(-2), "小数位下限 0");

        // 标签字号 = 20 × √(线宽 / 2)，与上游 distance_label / angle_label 同一口径
        Check.Equal(20.0, MeasureLabel.FontSize(2), "线宽 2 → 字号 20");
        Check.Equal(40.0, MeasureLabel.FontSize(8), "线宽 8 → 字号 40（√4 = 2 倍）");

        // ---- 测角：有向 / 内角、度 / 弧度 ----
        Check.Equal(Math.PI / 2, MeasureLabel.InteriorRadians(Math.PI / 2), "内角：小于 180° 原样");
        Check.Equal(90.0, Math.Round(MeasureLabel.InteriorRadians(Math.PI * 1.5) * 180 / Math.PI, 6),
            "内角：大于 180° 折成 2π − sweep");
        Check.Equal("90°", MeasureLabel.Angle(Math.PI / 2, AngleKind.Directed, AngleUnit.Degrees, 0),
            "度带 ° 且无空格");
        Check.Equal("1.57 rad", MeasureLabel.Angle(Math.PI / 2, AngleKind.Directed, AngleUnit.Radians, 2),
            "弧度带一个前导空格的 rad");
        Check.Equal("1.6", MeasureLabel.Angle(Math.PI / 2, AngleKind.Directed, AngleUnit.Radians, 1)
            .Replace(" rad", string.Empty), "弧度小数位");

        // ---- 测距参数：钳制 ----
        var spec = new DistanceSpec(DistanceUnit.Mm, 0.0001, 9, 99, Arrowhead.Dot).Clamped();
        Check.Equal(0.01, spec.Factor, "系数下限 0.01");
        Check.Equal(3, spec.Decimals, "小数位钳到 3");
        Check.Equal(3.0, spec.EndpointScale, "端点比例钳到 3");
        Check.Equal(1000.0, new DistanceSpec(DistanceUnit.Cm, 1e9, 0, 1, Arrowhead.Bar).Clamped().Factor,
            "系数上限 1000");
        Check.Equal(DistanceUnit.Cm, DistanceSpec.Default.Unit, "测距默认厘米");
        Check.Equal(Arrowhead.Bar, DistanceSpec.Default.Endpoint, "测距默认横杠端点");
        Check.Equal(AngleKind.Directed, AngleSpec.Default.Kind, "测角默认有向");
        Check.Equal(AngleUnit.Degrees, AngleSpec.Default.Unit, "测角默认度");

        // ---- 测距标注：标签在中点、命中包含标签、端点样式不改几何 ----
        var plain = new Mark.Distance(new Point(0, 0), new Point(100, 0), Colors.Red, 2,
            new DistanceSpec(DistanceUnit.Px, 1, 0, 1, Arrowhead.Bar));
        var arrowy = new Mark.Distance(new Point(0, 0), new Point(100, 0), Colors.Red, 2,
            new DistanceSpec(DistanceUnit.Px, 2, 1, 2, Arrowhead.Arrow));
        Check.Equal("100 px", plain.LabelText(1), "默认参数下 100 DIP = 100 px");
        Check.Equal("200.0 px", arrowy.LabelText(1), "系数 2 × 长度 100 = 200.0（一位小数）");
        Check.Equal(plain.A, arrowy.A, "端点样式不影响线段起点");
        Check.Equal(plain.B, arrowy.B, "端点样式不影响线段终点");
        Check.True(arrowy.Bounds.Contains(arrowy.LabelBox(1)), "标签框在外接框内");
        Check.Equal(plain.Midpoint.X, plain.LabelBox(1).X + plain.LabelBox(1).Width / 2, "标签横向居中在中点");
        Check.Equal(plain.Midpoint.Y, plain.LabelBox(1).Y + plain.LabelBox(1).Height / 2, "标签纵向也压在中线上");

        // ---- 花括号：三点⋯⋯尖在中点、四段弧、缩放跟着搬 ----
        var brace = new Mark.Bracket(new Point(10, 20), new Point(60, 120), Colors.Red, 3);
        Check.Equal(new Point(10, 20), brace.A, "花括号起点");
        Check.Equal(new Point(10, 120), brace.B, "花括号终点");
        Check.Equal(new Point(60, 70), brace.Apex, "尖在终点 x 与中线上");
        Check.Equal(4, brace.Segments.Length, "四段三次贝塞尔");
        Check.True(brace.HitTest(new Point(60, 70), 1), "尖上命中");
        Check.False(brace.HitTest(new Point(10, 70), 0), "脊线（首尾连线）不命中——它是空的");

        var scaledBrace = (Mark.Bracket)brace.ScaleTo(new Rect(10, 20, 50, 100), new Rect(10, 20, 100, 200));
        Check.Equal(new Point(10, 220), scaledBrace.B, "缩放后终点跟着搬");
        Check.Equal(new Point(110, 120), scaledBrace.Apex, "缩放后尖也按矩形映射搬");
        Check.Equal(6d, scaledBrace.Width, "缩放时线宽跟着乘 2");

        // ---- 参数格：测距 / 测角 / 橡皮各自用到哪几格 ----
        Check.True(ToolCatalog.Uses(CaptureTool.Distance, ToolParam.DistanceValue), "测距有「实测值」");
        Check.True(ToolCatalog.Uses(CaptureTool.Distance, ToolParam.EndpointStyle), "测距有端点样式");
        Check.True(ToolCatalog.Uses(CaptureTool.Distance, ToolParam.EndpointScale), "测距有端点比例");
        Check.True(ToolCatalog.Uses(CaptureTool.Distance, ToolParam.DistanceDecimals), "测距有小数位");
        Check.False(ToolCatalog.Uses(CaptureTool.Distance, ToolParam.AngleKind), "测距没有角度类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Angle, ToolParam.AngleKind), "测角有类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Angle, ToolParam.AngleUnit), "测角有单位");
        Check.True(ToolCatalog.Uses(CaptureTool.Angle, ToolParam.AngleDecimals), "测角有小数位");
        Check.False(ToolCatalog.Uses(CaptureTool.Angle, ToolParam.DistanceValue), "测角没有实测值");
        Check.True(ToolCatalog.Uses(CaptureTool.Eraser, ToolParam.EraserMode), "橡皮有模式选择器");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushEraser, ToolParam.EraserMode), "画笔橡皮也有模式选择器");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushEraser, ToolParam.Width), "画笔橡皮有宽度");
        Check.False(ToolCatalog.Uses(CaptureTool.RectEraser, ToolParam.Width), "矩形橡皮没有宽度");
        Check.Equal("矩形橡皮", EraserLabel.Chip(CaptureTool.RectEraser), "橡皮模式的名字");

        // ---- 落盘往返：新字段都要能存能读 ----
        var style = new ToolStyle
        {
            DistanceUnit = DistanceUnit.Mm,
            DistanceFactor = 2.5,
            DistanceDecimals = 2,
            EndpointScale = 1.5,
            EndpointStyle = Arrowhead.DiamondOutline,
            AngleKind = AngleKind.Interior,
            AngleUnit = AngleUnit.Radians,
            AngleDecimals = 3,
        };
        var back = ToolStyle.FromDto(style.ToDto());
        Check.Equal(DistanceUnit.Mm, back.DistanceUnit, "单位往返");
        Check.Equal(2.5, back.DistanceFactor, "系数往返");
        Check.Equal(2, back.DistanceDecimals, "小数位往返");
        Check.Equal(1.5, back.EndpointScale, "端点比例往返");
        Check.Equal(Arrowhead.DiamondOutline, back.EndpointStyle, "端点样式往返");
        Check.Equal(AngleKind.Interior, back.AngleKind, "角度类型往返");
        Check.Equal(AngleUnit.Radians, back.AngleUnit, "角度单位往返");
        Check.Equal(3, back.AngleDecimals, "角度小数位往返");

        var broken = ToolStyle.FromDto(new CaptureToolStyleDto
        {
            DistanceUnit = "furlong",
            DistanceFactor = -5,
            DistanceDecimals = 42,
            EndpointScale = 99,
            EndpointStyle = "star",
            AngleKind = "obtuse",
            AngleUnit = "gradians",
            AngleDecimals = -1,
        });
        Check.Equal(DistanceUnit.Cm, broken.DistanceUnit, "认不出的单位丢弃、回落默认");
        Check.Equal(0.01, broken.DistanceFactor, "负系数钳到下限");
        Check.Equal(3, broken.DistanceDecimals, "越界小数位钳回 3");
        Check.Equal(3.0, broken.EndpointScale, "越界端点比例钳回 3");
        Check.Equal(Arrowhead.Bar, broken.EndpointStyle, "认不出的端点样式回落横杠");
        Check.Equal(AngleKind.Directed, broken.AngleKind, "认不出的角度类型回落有向");
        Check.Equal(AngleUnit.Degrees, broken.AngleUnit, "认不出的角度单位回落度");
        Check.Equal(0, broken.AngleDecimals, "负小数位钳回 0");

        // 画笔橡皮那一族新建时就是粗笔头（上游 30）
        var set = ToolStyleSet.FromFile(null);
        Check.Equal(30d, set.For(CaptureTool.BrushEraser).Width, "画笔橡皮默认笔头 30 DIP");
        Check.Equal(4d, set.For(CaptureTool.Pen).Width, "画笔仍是 4");
        Check.Equal(DistanceUnit.Cm, set.For(CaptureTool.Distance).DistanceSpecOf().Unit, "未改过时测距仍是默认");
    }
}
