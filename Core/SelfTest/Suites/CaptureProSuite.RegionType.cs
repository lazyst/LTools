using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 选区形状（照上游 <c>ScreenshotRegionType</c> + <c>snowCanvasCatmullRomPath</c> +
/// <c>simplifyScreenshotRegionPoints</c>）：id 映射 / 化简 / 平滑曲线 / 命中。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void RegionTypeCases()
    {
        // ============ ① id 映射（照 screenshotRegionTypeId / FromId） ============
        Check.Equal("rectangle", SelectionRegionTypes.Id(SelectionMode.Rect), "矩形 id");
        Check.Equal("polyline", SelectionRegionTypes.Id(SelectionMode.Polyline), "折线 id");
        Check.Equal("curve", SelectionRegionTypes.Id(SelectionMode.Curve), "曲线 id");
        Check.Equal("freehand", SelectionRegionTypes.Id(SelectionMode.Lasso), "自由绘制 id");

        foreach (SelectionMode mode in Enum.GetValues<SelectionMode>())
            Check.Equal(mode, SelectionRegionTypes.FromId(SelectionRegionTypes.Id(mode)),
                "id 往返：" + mode);

        Check.Equal(SelectionMode.Rect, SelectionRegionTypes.FromId(null), "没配 → 矩形");
        Check.Equal(SelectionMode.Rect, SelectionRegionTypes.FromId(""), "空串 → 矩形");
        Check.Equal(SelectionMode.Curve, SelectionRegionTypes.FromId("  CURVE "), "大小写与空白不敏感");
        Check.Equal(SelectionMode.Rect, SelectionRegionTypes.FromId("bogus"), "认不出的 → 矩形");

        Check.Equal("矩形", SelectionRegionTypes.Label(SelectionMode.Rect), "矩形显示名");
        Check.Equal("折线", SelectionRegionTypes.Label(SelectionMode.Polyline), "折线显示名");
        Check.Equal("曲线", SelectionRegionTypes.Label(SelectionMode.Curve), "曲线显示名");
        Check.Equal("自由绘制", SelectionRegionTypes.Label(SelectionMode.Lasso), "自由绘制显示名");
        Check.False(SelectionRegionTypes.IsCustom(SelectionMode.Rect), "矩形不是自定义区域");
        Check.True(SelectionRegionTypes.IsCustom(SelectionMode.Curve), "曲线是自定义区域");
        Check.True(SelectionRegionTypes.IsCustom(SelectionMode.Lasso), "自由绘制是自定义区域");

        // 枚举顺序与上游一致（快捷键轮换就是「下一档」）
        Check.Equal(0, (int)SelectionMode.Rect, "枚举顺序：矩形");
        Check.Equal(1, (int)SelectionMode.Polyline, "枚举顺序：折线");
        Check.Equal(2, (int)SelectionMode.Curve, "枚举顺序：曲线");
        Check.Equal(3, (int)SelectionMode.Lasso, "枚举顺序：自由绘制");

        // ============ ② 化简（RDP，照 simplifyScreenshotRegionPoints） ============
        var line = new[]
        {
            new Point(0, 0), new Point(10, 0.05), new Point(20, -0.05), new Point(30, 0),
        };
        var simplifiedLine = SelectionShape.Simplify(line, 0.125);
        Check.Equal(2, simplifiedLine.Length, "共线点只剩首尾");
        Check.Equal(line[0], simplifiedLine[0], "化简保留首点");
        Check.Equal(line[^1], simplifiedLine[^1], "化简保留尾点");

        var corner = new[]
        {
            new Point(0, 0), new Point(50, 0), new Point(100, 0), new Point(100, 100),
        };
        var simplifiedCorner = SelectionShape.Simplify(corner, 0.125);
        Check.Equal(3, simplifiedCorner.Length, "拐点必须留下（实际 " + simplifiedCorner.Length + "）");
        Check.True(simplifiedCorner.Contains(new Point(100, 0)), "留下的就是拐点 (100,0)");

        Check.Equal(2, SelectionShape.Simplify(new[] { new Point(0, 0), new Point(5, 5) }, 0.125).Length,
            "少于三个点原样返回");
        Check.Equal(4, SelectionShape.Simplify(corner, 0).Length, "容差 0 = 不化简");

        // ============ ③ 平滑曲线（向心 Catmull-Rom，三次贝塞尔） ============
        var bend = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10) };
        var polyline = SelectionShape.Geometry(bend, closed: false);
        var smooth = SelectionShape.Geometry(bend, closed: false, smooth: true);

        var polylinePath = PathGeometry.CreateFromGeometry(polyline);
        var smoothPath = PathGeometry.CreateFromGeometry(smooth);
        Check.Equal(1, polylinePath.Figures.Count, "折线：一条轮廓");
        Check.Equal(1, smoothPath.Figures.Count, "平滑：一条轮廓");
        Check.True(polylinePath.Figures[0].Segments.Single() is PolyLineSegment,
            "折线合并成一段折线（两段直线）");
        Check.True(smoothPath.Figures[0].Segments.Single() is PolyBezierSegment,
            "平滑合并成一段贝塞尔（两段三次）");

        var bezier = (PolyBezierSegment)smoothPath.Figures[0].Segments[0];
        Check.Equal(6, bezier.Points.Count, "两段三次贝塞尔 = 6 个控制点（含终点）");
        Check.True(Close(smoothPath.Figures[0].StartPoint, bend[0]), "曲线从第一个点开始");
        Check.True(Close(bezier.Points[2], bend[1]), "曲线穿过中间那个点（插值样条，不是逼近）");
        Check.True(Close(bezier.Points[5], bend[^1]), "曲线结束在最后一个点");
        Check.True(smooth.Bounds.Top < -0.1, "拐角处曲线会外凸（Catmull-Rom 的特征，不是折线）");

        // 退化（两点 / 重合点）退回直线，别抛
        Check.True(SelectionShape.Geometry(new[] { new Point(0, 0), new Point(5, 5) },
            closed: false, smooth: true) is StreamGeometry, "两个点平滑 = 还是直线，不抛");
        var dupes = SelectionShape.Geometry(
            new[] { new Point(0, 0), new Point(0, 0), new Point(0, 0) }, closed: true, smooth: true);
        Check.True(dupes.Bounds.Width < 0.001 && dupes.Bounds.Height < 0.001, "重合点不产生面积");

        // ============ ④ 闭合曲线的命中 / 外接框 ============
        var square = new[]
        {
            new Point(0, 0), new Point(100, 0), new Point(100, 100), new Point(0, 100),
        };
        var closedSmooth = SelectionShape.Geometry(square, closed: true, smooth: true);
        Check.True(closedSmooth.FillContains(new Point(50, 50)), "闭合曲线里：命中");
        Check.False(closedSmooth.FillContains(new Point(-40, 50)), "闭合曲线外：不命中");
        Rect smoothBounds = smooth.Bounds;
        Check.True(smoothBounds.Width > 0 && smoothBounds.Height > 0, "开放曲线有外接框");

        // ============ ⑤ 区域带 Smooth 标记，几何 / 变换都保持 ============
        var curved = SelectionRegion.FromPoints(RegionMode.Add, square, smooth: true);
        Check.True(curved.Smooth, "点列区记下了平滑标记");
        var curvedPath = PathGeometry.CreateFromGeometry(curved.Geometry());
        Check.Equal(1, curvedPath.Figures.Count, "带平滑标记的几何是一条闭合轮廓");
        var curvedBezier = curvedPath.Figures[0].Segments.Single() as PolyBezierSegment;
        Check.True(curvedBezier != null, "带平滑标记的几何 = 贝塞尔段");
        Check.Equal(12, curvedBezier?.Points.Count ?? 0, "闭合四个点 = 四段三次贝塞尔");

        var straight = SelectionRegion.FromPoints(RegionMode.Add, square);
        Check.False(straight.Smooth, "默认不平滑（折线 / 自由画另说）");

        Check.True(curved.Offset(new Vector(10, 0)).Smooth, "平移保持平滑");
        Check.True(curved.Scale(curved.Bounds, new Rect(0, 0, 50, 50)).Smooth, "缩放保持平滑");
        Check.True(curved.ToImage(new Point(0, 0), 2).Smooth, "换成图像像素保持平滑");
        Check.True(curved.Clamp(60, 60).Smooth, "夹进底图保持平滑");

        // 圆角只对矩形生效：平滑点列块加了半径也不变（上游对自定义路径 cornerRadius = 0）
        var withRadius = SelectionSet.Combine(new[] { curved }, 30)!;
        var withoutRadius = SelectionSet.Combine(new[] { curved }, 0)!;
        Check.Equal(Math.Round(withoutRadius.Bounds.Width, 3),
            Math.Round(withRadius.Bounds.Width, 3), "自定义区域的圆角半径不生效");
        Check.True(SelectionSet.Contains(new[] { curved }, new Point(50, 50), 30),
            "命中测试按平滑后的路径算（中间命中）");
        Check.False(SelectionSet.Contains(new[] { curved }, new Point(-60, 50), 30),
            "命中测试按平滑后的路径算（外面不命中）");

        // ============ ⑥ 自由绘制松手那一步：化简 + 平滑 ============
        var stroke = new List<Point>();
        for (int i = 0; i <= 200; i++) stroke.Add(new Point(i * 0.5, 40 + Math.Sin(i * 0.05) * 20));
        var reduced = SelectionShape.Simplify(stroke, SelectionShape.SimplifyTolerance);
        Check.True(reduced.Length < stroke.Count, "一笔 201 个点被化简（实际 " + reduced.Length + "）");
        Check.True(reduced.Length >= 3, "化简后仍能围出区域");
        Check.Equal(stroke[0], reduced[0], "化简保留起点");
        Check.Equal(stroke[^1], reduced[^1], "化简保留终点");
    }

    private static bool Close(Point a, Point b, double tolerance = 0.01) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;
}
