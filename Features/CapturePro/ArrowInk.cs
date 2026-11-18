using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 端点样式（顺序照上游 <c>snow_draw_engine_core::arrow::Arrowhead</c>）。
/// 箭头 / 测距端点 / 放大镜引导线共用这一套；<see cref="None"/> 排在最前给下拉用。
/// </summary>
internal enum Arrowhead
{
    None,
    Arrow,
    Bar,
    Dot,
    Circle,
    CircleOutline,
    Triangle,
    TriangleOutline,
    Diamond,
    DiamondOutline,
    CrowfootOne,
    CrowfootMany,
    CrowfootOneOrMany,
    Square,
    InvertedTriangle,
    IndentedTriangle,
}

/// <summary>
/// 端点几何的唯一实现（移植 <c>snow-draw-engine-document/src/arrow/render_core.rs</c>）：
/// 尺寸 = 基础尺寸 × 线宽缩放 × 箭头比例，肩点绕端点转固定角度，再按类型拼线 / 多边形 / 圆。
/// </summary>
/// <remarks>
/// 上游的 <c>FillMode::Background</c>（空心三角 / 空心菱形 / 空心圆）在这里画成**只描边**——
/// 上游填的是「画布背景色」，而 LTools 的背景是截图本身，填一个纯色块等于在图上打补丁；
/// 只描边与「空心」这个语义一致。
/// </remarks>
internal static class ArrowInk
{
    /// <summary>箭头比例范围（上游 <c>normalize_arrow_ratio</c>：0.5–3.0）。</summary>
    public const double MinRatio = 0.5;
    public const double MaxRatio = 3.0;

    public static double Ratio(double ratio) =>
        double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : 1;

    /// <summary>基础尺寸（上游 <c>get_arrowhead_size</c>，全部再 × 1.25）。</summary>
    public static double Size(Arrowhead head) => head switch
    {
        Arrowhead.Arrow => 25.0 * 1.25,
        Arrowhead.Diamond or Arrowhead.DiamondOutline or Arrowhead.Square => 12.0 * 1.25,
        Arrowhead.CrowfootOne or Arrowhead.CrowfootMany or Arrowhead.CrowfootOneOrMany => 20.0 * 1.25,
        _ => 15.0 * 1.25,
    };

    /// <summary>肩点的张角（度，上游 <c>get_arrowhead_angle</c>）。</summary>
    public static double AngleDegrees(Arrowhead head) => head switch
    {
        Arrowhead.Bar => 90.0,
        Arrowhead.Arrow => 20.0,
        _ => 25.0,
    };

    /// <summary>圆类端点（直径按 <c>base + min(线宽, 2) - 2</c>）。</summary>
    public static bool IsRound(Arrowhead head) =>
        head is Arrowhead.Dot or Arrowhead.Circle or Arrowhead.CircleOutline;

    public static bool IsDiamond(Arrowhead head) =>
        head is Arrowhead.Diamond or Arrowhead.DiamondOutline or Arrowhead.Square;

    /// <summary>
    /// 画一个端点。
    /// </summary>
    /// <param name="tip">端点（线的尽头）。</param>
    /// <param name="toward">从线段指向端点方向的单位向量（肩点往后退的方向）。</param>
    /// <param name="segmentLength">最后一段的长度：端点不能比线段本身还长（上游 <c>length_multiplier</c>）。</param>
    /// <param name="grow">
    /// 外描边垫底用：0 = 正常墨迹；&gt;0 = 用这个颜色把同一形状整体外扩 <c>grow / 2</c>
    /// （实心图元外扩靠描边、线条 / 空心图元靠加粗笔），随后正常墨迹盖在上面。
    /// </param>
    public static void Draw(DrawingContext dc, Point tip, Vector toward, Arrowhead head, Color color,
        double width, double ratio, double segmentLength, double grow = 0)
    {
        if (head == Arrowhead.None) return;
        if (!(segmentLength > 0.01) || !double.IsFinite(segmentLength)) return;

        var dir = toward;
        if (dir.Length < 1e-9) return;
        dir.Normalize();

        double stroke = Math.Max(0.1, width);
        // 外描边那一遍：线条 / 空心形状的笔要更粗，实心形状靠描边把它撑大
        double line = grow > 0 ? Math.Max(0.1, width + grow) : stroke;
        double edge = grow > 0 ? Math.Max(0.1, grow) : stroke;
        double strokeScale = Math.Sqrt(Math.Max(stroke / 2.0, 1.0));
        double baseSize = Size(head) * strokeScale;
        double scale = Ratio(ratio);
        double size = baseSize * scale;
        double multiplier = IsDiamond(head) ? 0.25 : 0.5;
        double minSize = Math.Min(size, segmentLength * multiplier);

        Point basePoint = tip - dir * minSize;

        if (IsRound(head))
        {
            double adjust = Math.Min(stroke, 2.0) - 2.0;
            double diameter = Math.Min((baseSize + adjust) * scale, segmentLength * multiplier + adjust);
            if (diameter <= 0.2) return;

            double radius = diameter / 2;
            if (head == Arrowhead.CircleOutline)
                dc.DrawEllipse(null, Pen(color, line), tip, radius, radius);
            else if (grow > 0)
                dc.DrawEllipse(Brush(color), Pen(color, edge), tip, radius, radius);
            else
                dc.DrawEllipse(Brush(color), null, tip, radius, radius);
            return;
        }

        double angle = AngleDegrees(head) * Math.PI / 180.0;
        Point a = Rotate(tip, basePoint, -angle);
        Point b = Rotate(tip, basePoint, angle);

        switch (head)
        {
            case Arrowhead.CrowfootOne:
                // 只有一根横杠（上游 CrowfootOne 取肩点之间的那一条）
                DrawPolyline(dc, color, line, new[] { a, b });
                return;

            case Arrowhead.CrowfootMany:
                // 三趾：从基部往两个肩点各一线（开口朝端点）
                DrawPolyline(dc, color, line, new[] { a, basePoint, b });
                return;

            case Arrowhead.CrowfootOneOrMany:
                DrawPolyline(dc, color, line, new[] { a, basePoint, b });
                DrawPolyline(dc, color, line, new[] { a, b });
                return;

            case Arrowhead.Bar:
                DrawPolyline(dc, color, line, new[] { a, basePoint, b });
                return;

            case Arrowhead.Triangle:
            case Arrowhead.TriangleOutline:
            case Arrowhead.InvertedTriangle:
                Polygon(dc, color, head == Arrowhead.TriangleOutline ? line : edge,
                    head == Arrowhead.TriangleOutline, new[] { tip, a, b });
                return;

            case Arrowhead.IndentedTriangle:
            {
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var notch = new Point(
                    mid.X + 0.25 * (tip.X - mid.X),
                    mid.Y + 0.25 * (tip.Y - mid.Y));
                Polygon(dc, color, line, true, new[] { tip, a, notch, b });
                return;
            }

            case Arrowhead.Diamond:
            case Arrowhead.DiamondOutline:
            case Arrowhead.Square:
            {
                Point back = tip - dir * (minSize * 2);
                Polygon(dc, color, head == Arrowhead.DiamondOutline ? line : edge,
                    head == Arrowhead.DiamondOutline, new[] { tip, a, back, b });
                return;
            }

            default:
                // Arrow：两条肩线汇到端点
                DrawPolyline(dc, color, line, new[] { a, tip, b });
                return;
        }
    }

    /// <summary>绕 <paramref name="center"/> 旋转（上游 <c>rotate_point(center, point, angle)</c>）。</summary>
    public static Point Rotate(Point center, Point point, double radians)
    {
        double sin = Math.Sin(radians), cos = Math.Cos(radians);
        double dx = point.X - center.X, dy = point.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    private static void Polygon(DrawingContext dc, Color color, double width, bool outline, Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = !outline };
        figure.Segments.Add(new PolyLineSegment(points[1..], true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        dc.DrawGeometry(outline ? null : Brush(color), Pen(color, width), geometry);
    }

    private static void DrawPolyline(DrawingContext dc, Color color, double width, Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        if (points.Length > 1) figure.Segments.Add(new PolyLineSegment(points[1..], true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        dc.DrawGeometry(null, Pen(color, width), geometry);
    }

    private static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Color color, double width)
    {
        var pen = new Pen(Brush(color), Math.Max(0.1, width))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
