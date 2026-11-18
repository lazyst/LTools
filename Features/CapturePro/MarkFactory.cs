using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>测角的两个臂（<see cref="Armed"/> = false 表示只落了顶点，正在画第一条臂）。</summary>
internal readonly record struct AngleArms(Point Vertex, Point First, bool Armed)
{
    public static readonly AngleArms None = new(default, default, false);
}

/// <summary>
/// 标注的「按当前工具与样式造一条」：截图覆盖层与图片编辑窗共用同一份实现。
/// </summary>
/// <remarks>
/// 这里只做纯数据（不碰控件、不落盘、不进撤销栈），坐标一律是**文档坐标**
/// （覆盖层 / 编辑窗都把它换算好了再递进来）。新增工具只在这里加一个 case，
/// 别再在某个窗口里另写一套形状构造。
/// </remarks>
internal static class MarkFactory
{
    /// <summary>拖一类工具从 <paramref name="start"/> 拖到 <paramref name="current"/> 时的预览 / 成品。</summary>
    public static Mark? Drag(CaptureTool tool, Point start, Point current,
        IReadOnlyList<Point> freehand, ToolStyle style, uint seed, AngleArms angle = default)
    {
        var color = style.Color;
        double width = style.WidthFor(tool);

        switch (tool)
        {
            case CaptureTool.Pen:
                return new Mark.Stroke(Points(freehand), color, width, false, 1);

            case CaptureTool.Highlighter:
                return new Mark.Stroke(Points(freehand), color, width, true, style.Opacity);

            case CaptureTool.Shape:
                return new Mark.Shape(start, current, style.ShapeSpecOf(seed));

            case CaptureTool.Arrow:
                return new Mark.Arrow(
                    ArrowGeometry.Points(start, current, style.ArrowKind),
                    style.ArrowSpecOf(seed));

            case CaptureTool.Line:
                return new Mark.Line(start, current, color, width, style.StrokeStyle,
                    style.LineType, style.Rendering, seed);

            case CaptureTool.Distance:
                return new Mark.Distance(start, current, color, width, style.DistanceSpecOf());

            case CaptureTool.Bracket:
                return new Mark.Bracket(start, current, color, width);

            case CaptureTool.SpeechBubble:
                return new Mark.SpeechBubble(MarkGeometry.RectOf(start, current), style.ShapeSpecOf(seed));

            case CaptureTool.Magnifier:
                return new Mark.Magnifier(MarkGeometry.RectOf(start, current), style.MagnifierSpecOf());

            case CaptureTool.HighlightRect:
                return new Mark.Highlight(start, current, color, style.Opacity);

            case CaptureTool.BrushFilter:
                return new Mark.BrushFilter(Points(freehand), width, style.FilterMode, style.FilterStrength);

            case CaptureTool.Mosaic:
                return new Mark.Mosaic(start, current, style.Block);

            case CaptureTool.Filter:
                return new Mark.Filter(start, current, style.FilterMode, style.FilterStrength);

            case CaptureTool.Spotlight:
                return new Mark.Spotlight(start, current, style.Dim);

            case CaptureTool.Angle:
                return angle.Armed
                    ? Mark.Angle.FromArms(angle.Vertex, angle.First, current, color, width, style.AngleSpecOf())
                    : new Mark.Line(angle.Vertex, current, color, width, false);

            default:
                return null;
        }
    }

    /// <summary>画笔橡皮拖动时的预览笔迹（半透明白，落盘时才变成「恢复原图」块）。</summary>
    public static Mark BrushEraseTrail(IReadOnlyList<Point> freehand, ToolStyle style) =>
        new Mark.Stroke(Points(freehand), Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
            style.WidthFor(CaptureTool.BrushEraser), false, 1);

    /// <summary>拖得够大了才落盘：手绘要有点，线性看长度，其它要两个方向都有。</summary>
    public static bool BigEnough(CaptureTool tool, Point start, Point current, int freehandCount)
    {
        double minimum = ToolCatalog.MinimumSize(tool);

        if (ToolCatalog.IsFreehand(tool)) return freehandCount > 0;
        if (ToolCatalog.IsLinear(tool)) return (current - start).Length >= minimum;

        return Math.Abs(current.X - start.X) >= minimum && Math.Abs(current.Y - start.Y) >= minimum;
    }

    private static Point[] Points(IReadOnlyList<Point> source)
    {
        var points = new Point[source.Count];
        for (int i = 0; i < source.Count; i++) points[i] = source[i];
        return points;
    }
}
