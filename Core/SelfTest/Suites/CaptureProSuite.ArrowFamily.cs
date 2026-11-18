using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 箭头族（P2-e1）：三种类型（直线 / 曲线 / 折线）、14 种端点、起止两端、渐宽杆、
/// 外描边与样式落盘。几何全部是纯函数，这里直接断言，不碰 WPF 可视化树。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void ArrowFamily()
    {
        var start = new Point(0, 0);
        var end = new Point(400, 0);

        // ---- 类型：直线 / 曲线 / 折线 ----
        Check.Equal(2, ArrowGeometry.Points(start, end, ArrowType.Straight).Length, "直线只有首尾两点");
        Check.Equal(2, ArrowGeometry.Points(start, end, ArrowType.Curve).Length, "曲线也是首尾两点");
        Check.Equal(4, ArrowGeometry.Points(start, new Point(400, 200), ArrowType.Elbow).Length,
            "斜拖的折线是四点（长边走一半再拐）");

        var elbow = ArrowGeometry.ElbowPoints(start, new Point(400, 200));
        Check.Equal(new Point(200, 0), elbow[1], "折线第一个拐点落在长边中点");
        Check.Equal(new Point(200, 200), elbow[2], "折线第二个拐点与终点同高");
        Check.Equal(2, ArrowGeometry.ElbowPoints(start, new Point(0, 300)).Length,
            "正上/正下拖折线退化成两点（上游 dx/dy 判据）");

        var straight = ArrowGeometry.Path(new[] { start, end }, ArrowType.Straight);
        Check.Equal(1, straight.Count, "直线路径一段");
        Check.True(straight[0].Straight, "直线段是直的");

        var curve = ArrowGeometry.Path(new[] { start, new Point(200, 120), end }, ArrowType.Curve);
        Check.Equal(2, curve.Count, "三点曲线 = 两段三次贝塞尔");
        Check.True(curve.All(s => !s.Straight), "曲线段都不是直线");
        Check.Equal(start, curve[0].A, "曲线从头开始");
        Check.Equal(end, curve[^1].B, "曲线在终点结束");

        // 折线：拐角圆角 = min(16, 邻边/2)，拐角处是「直线 → 二次（转三次）→ 直线」
        var elbowPath = ArrowGeometry.Path(elbow, ArrowType.Elbow);
        Check.Equal(5, elbowPath.Count, "四个点的折线 = 3 段直线 + 2 段拐角弧");
        Check.Equal(new Point(184, 0), elbowPath[0].B, "第一个拐角在拐点前 16 DIP 进弧");
        Check.Equal(new Point(200, 16), elbowPath[1].B, "第一段弧结束在转折后 16 DIP");
        Check.Equal(new Point(200, 184), elbowPath[2].B, "第二段直线在下一个拐角前 16 DIP 停");
        Check.Equal(new Point(216, 200), elbowPath[3].B, "第二段弧结束在拐点之后 16 DIP");

        // ---- 端点方向 ----
        Check.True(ArrowGeometry.Endpoint(new[] { start, new Point(0, 100) }, ArrowType.Curve,
            start: false, out var tip, out var direction, out var length), "曲线终点取得到");
        Check.Equal(new Point(0, 100), tip, "终点就是最后一个点");
        Check.True(direction.Y > 0.95, "竖向曲线的终点方向朝下（切线）");
        Check.True(length > 0, "终点方向有长度");

        Check.True(ArrowGeometry.Endpoint(elbow, ArrowType.Elbow, start: false, out _,
            out var elbowDirection, out _), "折线终点取得到");
        Check.True(elbowDirection.X > 0.95, "折线终点方向 = 末段弦（朝右）");

        // ---- 端点集合（14 种，顺序照上游 buildArrowFamily）----
        Check.Equal(14, ArrowGeometry.Heads.Length, "箭头族共 14 种端点");
        Check.Equal(Arrowhead.None, ArrowGeometry.Heads[0], "第一种是「无」");
        Check.Equal(Arrowhead.Arrow, ArrowGeometry.Heads[1], "第二种是三角箭头");
        Check.Equal(Arrowhead.CrowfootOneOrMany, ArrowGeometry.Heads[^1], "最后一种是多趾杠");

        // ---- 渐宽杆 ----
        var tapered = new ArrowSpec(Color.FromRgb(255, 0, 0), 2, StrokeKind.Solid,
            StrokeRendering.Architect, ArrowType.Straight, Arrowhead.None, Arrowhead.Arrow, 1,
            ArrowShaft.Tapered, ArrowSpec.DefaultOutline, 0, 1, 7);
        var parts = ArrowGeometry.Tapered(tapered, new[] { start, end });
        Check.True(parts.Count >= 1, "实线渐宽杆有一段轮廓");
        Check.True(parts[0].Closed, "渐宽杆轮廓是闭合的");
        var flatPart = parts[0].Segments.SelectMany(s => new[] { s.A, s.B }).ToList();
        Check.True(flatPart.Any(p => Math.Abs(p.X - 400) < 0.01), "渐宽杆轮廓顶到箭头尖");
        Check.True(flatPart.Any(p => Math.Abs(p.X) < 0.01), "渐宽杆轮廓从起点开始");

        var plain = tapered with { Shaft = ArrowShaft.Plain };
        Check.Equal(0, ArrowGeometry.Tapered(plain, new[] { start, end }).Count,
            "等宽杆不走渐宽几何");

        var dashed = tapered with { StrokeStyle = StrokeKind.Dashed };
        Check.True(ArrowGeometry.Tapered(dashed, new[] { start, end }).Count > 2,
            "虚线渐宽杆 = 多段短杆 + 箭头（不是一整块）");

        var unsupported = tapered with { End = Arrowhead.Bar };
        Check.Equal(0, ArrowGeometry.Tapered(unsupported, new[] { start, end }).Count,
            "横杠端点不支持渐宽杆（上游 supports_tapered_shaft）");

        Check.True(ArrowGeometry.SupportsTapered(Arrowhead.Arrow), "三角支持渐宽");
        Check.True(ArrowGeometry.SupportsTapered(Arrowhead.TriangleOutline), "空三角支持渐宽");
        Check.True(!ArrowGeometry.SupportsTapered(Arrowhead.Bar), "横杠不支持渐宽");
        Check.True(!ArrowGeometry.SupportsTapered(Arrowhead.None), "无端点不支持渐宽");

        // 箭头肩宽 ÷ 4 = 杆在箭头那一端的半径（上游 shaft.rs）
        var head = ArrowGeometry.Head(new Point(400, 0), new Vector(1, 0), Arrowhead.Arrow, 2, 1,
            400);
        Check.True(head.HasValue, "三角箭头算得出几何");
        double shoulder = (head!.Value.A - head.Value.B).Length;
        Check.True(Math.Abs(shoulder * 0.25 - head.Value.HalfWidth) < 1e-9,
            "渐宽杆端部半径 = 肩宽 / 4");

        // ---- Mark.Arrow：点列 / 命中 / 移动 / 缩放 ----
        var mark = new Mark.Arrow(elbow, tapered);
        Check.Equal(400d, mark.End.X, "箭头终点 = 点列最后一个");
        Check.Equal(0d, mark.Start.X, "箭头起点 = 点列第一个");
        Check.True(mark.Bounds.Contains(elbow[1]), "外接框包住拐点");
        Check.True(mark.HitTest(new Point(100, 0), 2), "命中折线的第一段");
        Check.True(!mark.HitTest(new Point(50, 150), 2), "空白处不命中");

        var moved = (Mark.Arrow)mark.MoveBy(new Vector(10, 20));
        Check.Equal(new Point(10, 20), moved.Start, "整体平移起点");
        Check.Equal(new Point(410, 220), moved.End, "整体平移终点");
        Check.Equal(ArrowShaft.Tapered, moved.Spec.Shaft, "平移不动样式");

        var scaled = (Mark.Arrow)mark.ScaleTo(mark.Frame, new Rect(0, 0, 800, 400));
        Check.Equal(800d, scaled.End.X, "缩放后终点跟着框走");
        Check.True(scaled.Spec.Width > mark.Spec.Width, "缩放放大线宽");

        // 旧的四参构造仍然给「两点 + 三角」
        var legacy = new Mark.Arrow(start, new Point(0, 100), Colors.Red, 3);
        Check.Equal(2, legacy.Points.Count, "四参构造 = 两点");
        Check.Equal(Arrowhead.Arrow, legacy.Spec.End, "四参构造默认带终点箭头");
        Check.Equal(Colors.Red, legacy.Color, "四参构造保留颜色");

        // ---- 样式：出厂值 / 钳位 / 落盘往返 ----
        var style = new ToolStyle();
        ToolStyleSet.ApplyFamilyDefaults(StyleFamilies.Arrow, style);
        Check.Equal(ArrowType.Curve, style.ArrowKind, "箭头族默认曲线");
        Check.Equal(Arrowhead.None, style.StartArrowhead, "起点默认无");
        Check.Equal(Arrowhead.Arrow, style.EndArrowhead, "终点默认三角");
        Check.Equal(ArrowShaft.Plain, style.ArrowShaftType, "默认等宽杆");
        Check.Equal(ArrowSpec.DefaultOutline, style.ArrowOutlineColor, "外描边色 #FFCCC7");
        Check.Equal(0d, style.ArrowOutlineWidth, "外描边默认关");

        style.ArrowKind = ArrowType.Elbow;
        style.StartArrowhead = Arrowhead.Dot;
        style.EndArrowhead = Arrowhead.DiamondOutline;
        style.ArrowShaftType = ArrowShaft.Tapered;
        style.ArrowOutlineWidth = 300;
        var spec = style.ArrowSpecOf(9);
        Check.Equal(ArrowSpec.MaxOutlineWidth, spec.OutlineWidth, "外描边宽钳到 72");
        Check.Equal(9u, spec.Seed, "种子带进箭头样式");
        Check.Equal(ArrowType.Elbow, spec.Type, "箭头类型带进样式");

        var back = ToolStyle.FromDto(style.ToDto());
        Check.Equal(ArrowType.Elbow, back.ArrowKind, "类型落盘往返");
        Check.Equal(Arrowhead.Dot, back.StartArrowhead, "起点端点落盘往返");
        Check.Equal(Arrowhead.DiamondOutline, back.EndArrowhead, "终点端点落盘往返");
        Check.Equal(ArrowShaft.Tapered, back.ArrowShaftType, "杆型落盘往返");
        Check.Equal(ArrowSpec.MaxOutlineWidth, back.ArrowOutlineWidth, "外描边宽落盘往返");
    }
}
