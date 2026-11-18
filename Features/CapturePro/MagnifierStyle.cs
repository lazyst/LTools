using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>放大镜与源区域之间的连接方式（上游 <c>MagnifierConnectionType</c>）。</summary>
internal enum MagnifierConnection
{
    None,
    LeaderLine,
    Gradient,
}

/// <summary>
/// 放大镜的一整套画法（上游 <c>SnowCanvasMagnifierStyle</c>）。
/// 默认值与范围只在这里，渲染与自检共用。
/// </summary>
internal sealed record MagnifierSpec(
    ShapeKind Shape,
    Color Stroke,
    double StrokeWidth,
    double Factor,
    MagnifierConnection Connection,
    bool SourceOutline,
    Arrowhead LeaderArrowhead,
    double ArrowRatio,
    double CornerRadius,
    double Opacity)
{
    public const double MinFactor = 1;
    public const double MaxFactor = 10;
    public const double MinStrokeWidth = 0;
    public const double MaxStrokeWidth = 72;
    public const double MaxCornerRadius = 83;

    public static readonly Color DefaultStroke = Color.FromRgb(0xF5, 0x22, 0x2D);

    /// <summary>上游 <c>screenshotCanvasStyleDefaults().magnifier</c> + <c>SnowCanvasMagnifierStyle</c> 默认。</summary>
    public static MagnifierSpec Default { get; } = new(
        ShapeKind.Rectangle, DefaultStroke, 2, 2, MagnifierConnection.Gradient, true,
        Arrowhead.None, 1, 6, 1);

    public MagnifierSpec Clamped() => this with
    {
        Shape = Shape == ShapeKind.ThoughtBubble ? ShapeKind.Rectangle : Shape,
        StrokeWidth = double.IsFinite(StrokeWidth) ? Math.Clamp(StrokeWidth, MinStrokeWidth, MaxStrokeWidth) : 2,
        Factor = double.IsFinite(Factor) ? Math.Clamp(Factor, MinFactor, MaxFactor) : 2,
        ArrowRatio = double.IsFinite(ArrowRatio)
            ? Math.Clamp(ArrowRatio, ArrowInk.MinRatio, ArrowInk.MaxRatio)
            : 1,
        CornerRadius = double.IsFinite(CornerRadius) ? Math.Clamp(CornerRadius, 0, MaxCornerRadius) : 6,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
    };
}

/// <summary>
/// 放大镜族的参数文案（连接方式的三档）：参数行胶囊 / tooltip / 自检共用一份。
/// </summary>
internal static class MagnifierLabel
{
    public static string Chip(MagnifierConnection connection) => connection switch
    {
        MagnifierConnection.LeaderLine => "引导线",
        MagnifierConnection.Gradient => "渐变",
        _ => "无",
    };

    public static string Name(MagnifierConnection connection) => connection switch
    {
        MagnifierConnection.LeaderLine => "连接方式：从镜头拉一条引导线指向源区域",
        MagnifierConnection.Gradient => "连接方式：镜头与源区域之间渐变过渡",
        _ => "连接方式：不画连接",
    };
}

/// <summary>
/// 放大镜的几何（照上游 <c>document/src/magnifier.rs</c> + <c>core/src/magnifier.rs</c>）：
/// 镜头 = 源区域 × 倍率，源区域与镜头**各自独立定位**，连接线从镜头轮廓外 8 DIP 起、止于源中心。
/// 纯计算，<c>--selftest</c> 直接断言。
/// </summary>
internal static class MagnifierInk
{
    /// <summary>引导线离开镜头轮廓的空隙（上游 <c>SHAPE_CONNECTOR_GAP</c>）。</summary>
    public const double ConnectorGap = 8;

    /// <summary>移动手柄离镜头下缘的距离（上游 <c>magnifier_move_grip</c> 里的 <c>24 / zoom</c>，DIP）。</summary>
    public const double MoveGripOffset = 24;

    /// <summary>移动手柄的边长（上游 <c>14 / zoom</c>，DIP）。</summary>
    public const double MoveGripSize = 14;

    /// <summary>移动手柄的命中半宽（上游 <c>9 / zoom</c>，DIP）——比看得见的方块大一圈，好点。</summary>
    public const double MoveGripHit = 9;

    /// <summary>
    /// 移动手柄的中心：镜头下缘正中再往下 24 DIP。
    /// 上游把它放在镜头**外面**，这样源区域与镜头重合时镜头本体也还抓得到。
    /// </summary>
    public static Point MoveGrip(Rect lens) => new(lens.Left + lens.Width / 2, lens.Bottom + MoveGripOffset);

    /// <summary>移动手柄的方块（14×14，中心在 <see cref="MoveGrip"/>）。</summary>
    public static Rect MoveGripRect(Rect lens)
    {
        var center = MoveGrip(lens);
        return new Rect(center.X - MoveGripSize / 2, center.Y - MoveGripSize / 2, MoveGripSize, MoveGripSize);
    }

    /// <summary>这一点在不在移动手柄的命中范围里（±9 DIP 的方块）。</summary>
    public static bool HitsMoveGrip(Rect lens, Point point)
    {
        var center = MoveGrip(lens);
        return Math.Abs(point.X - center.X) <= MoveGripHit && Math.Abs(point.Y - center.Y) <= MoveGripHit;
    }

    /// <summary>放大后的镜头矩形：源尺寸 × 倍率，中心是镜头中心。</summary>
    public static Rect LensRect(Point lensCenter, double sourceWidth, double sourceHeight, double factor)
    {
        double w = Math.Max(1, sourceWidth) * Math.Max(1, factor);
        double h = Math.Max(1, sourceHeight) * Math.Max(1, factor);
        return new Rect(lensCenter.X - w / 2, lensCenter.Y - h / 2, w, h);
    }

    /// <summary>源区域矩形：尺寸就是源尺寸（不乘倍率），中心是源中心。</summary>
    public static Rect SourceRectOf(Point sourceCenter, double sourceWidth, double sourceHeight) =>
        new(sourceCenter.X - Math.Max(1, sourceWidth) / 2, sourceCenter.Y - Math.Max(1, sourceHeight) / 2,
            Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

    /// <summary>形状轮廓（裁剪镜头内容 / 描边用）。</summary>
    public static Geometry Outline(Rect rect, ShapeKind shape, double radius)
    {
        if (rect.Width <= 0.01 || rect.Height <= 0.01) return Geometry.Empty;

        switch (shape)
        {
            case ShapeKind.Ellipse:
            {
                var ellipse = new EllipseGeometry(rect);
                ellipse.Freeze();
                return ellipse;
            }
            case ShapeKind.Diamond:
            {
                var path = new StreamGeometry();
                using (var ctx = path.Open())
                {
                    var p0 = new Point(rect.Left + rect.Width / 2, rect.Top);
                    var p1 = new Point(rect.Right, rect.Top + rect.Height / 2);
                    var p2 = new Point(rect.Left + rect.Width / 2, rect.Bottom);
                    var p3 = new Point(rect.Left, rect.Top + rect.Height / 2);
                    ctx.BeginFigure(p0, isFilled: true, isClosed: true);
                    ctx.LineTo(p1, true, false);
                    ctx.LineTo(p2, true, false);
                    ctx.LineTo(p3, true, false);
                }
                path.Freeze();
                return path;
            }
            default:
            {
                double k = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
                var rounded = new RectangleGeometry(rect, k, k);
                rounded.Freeze();
                return rounded;
            }
        }
    }

    /// <summary>
    /// 轮廓外距离（上游 <c>contour_distance</c>）：在轮廓内返回 0，在外面返回正的距离。
    /// 只服务引导线起点的二分，误差几个像素看不出来。
    /// </summary>
    public static double ContourDistance(ShapeKind shape, double halfW, double halfH, double radius,
        double x, double y)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y);
        switch (shape)
        {
            case ShapeKind.Ellipse:
            {
                if (halfW <= 0 || halfH <= 0) return 0;
                double metric = Math.Sqrt(ax / halfW * (ax / halfW) + ay / halfH * (ay / halfH));
                if (metric <= 1) return 0;
                double nx = ax / metric, ny = ay / metric;
                return Math.Sqrt((ax - nx) * (ax - nx) + (ay - ny) * (ay - ny));
            }
            case ShapeKind.Diamond:
            {
                if (halfW <= 0 || halfH <= 0) return 0;
                double metric = ax / halfW + ay / halfH;
                if (metric <= 1) return 0;
                double nx = ax / metric, ny = ay / metric;
                return Math.Sqrt((ax - nx) * (ax - nx) + (ay - ny) * (ay - ny));
            }
            default:
            {
                double distance = Math.Sqrt(Math.Max(0, ax - halfW) * Math.Max(0, ax - halfW) +
                                            Math.Max(0, ay - halfH) * Math.Max(0, ay - halfH));
                double r = Math.Clamp(radius, 0, Math.Min(halfW, halfH));
                if (r <= 0) return distance;
                foreach (var (sx, sy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                {
                    double dx = sx * ax - (halfW - r);
                    double dy = sy * ay - (halfH - r);
                    if (dx > 0 && dy > 0) distance = Math.Max(distance, Math.Sqrt(dx * dx + dy * dy) - r);
                }
                return distance;
            }
        }
    }

    /// <summary>
    /// 引导线：从镜头轮廓外（含描边与 8 DIP 空隙）指向源中心；退化时返回 null
    /// （上游 <c>MagnifierData::leader</c>）。
    /// </summary>
    public static (Point Start, Point End)? Leader(Point lensCenter, Point sourceCenter, double lensWidth,
        double lensHeight, ShapeKind shape, double radius, double strokeWidth)
    {
        if (shape == ShapeKind.ThoughtBubble) return null;

        double dx = sourceCenter.X - lensCenter.X, dy = sourceCenter.Y - lensCenter.Y;
        double halfW = lensWidth / 2, halfH = lensHeight / 2;
        if (halfW <= 0 || halfH <= 0) return null;

        double metric = shape switch
        {
            ShapeKind.Ellipse => Math.Sqrt(dx / halfW * (dx / halfW) + dy / halfH * (dy / halfH)),
            ShapeKind.Diamond => Math.Abs(dx) / halfW + Math.Abs(dy) / halfH,
            _ => Math.Max(Math.Abs(dx) / halfW, Math.Abs(dy) / halfH),
        };

        if (ContourDistance(shape, halfW, halfH, radius, dx, dy) == 0) return null;

        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= double.Epsilon) return null;

        double halfStroke = strokeWidth / 2;
        double outerRadius = Math.Sqrt(halfW * halfW + halfH * halfH) + halfStroke;
        if (length <= outerRadius && ContourDistance(shape, halfW, halfH, radius, dx, dy) <= halfStroke)
            return null;

        double low = shape == ShapeKind.Ellipse ? length / Math.Max(metric, 1e-9) : 0;
        double high = Math.Min(length, outerRadius);
        for (int i = 0; i < 48; i++)
        {
            double middle = (low + high) / 2;
            double px = dx * (middle / length), py = dy * (middle / length);
            if (ContourDistance(shape, halfW, halfH, radius, px, py) <= halfStroke) low = middle;
            else high = middle;
        }

        double startDistance = high + ConnectorGap + halfStroke;
        double tolerance = outerRadius * 1e-15 * 32;
        if (length - startDistance <= halfStroke + tolerance) return null;

        var start = new Point(lensCenter.X + dx * (startDistance / length),
            lensCenter.Y + dy * (startDistance / length));
        return (start, sourceCenter);
    }

    /// <summary>
    /// 镜头 + 源区域轮廓点的凸包（上游渐变桥用两个形状的网格顶点求凸包）。
    /// 渐变桥只在镜头外着色，所以「凸包减镜头」就是它的范围。
    /// </summary>
    public static IReadOnlyList<Point> Hull(Rect lens, Rect source, ShapeKind shape, double lensRadius)
    {
        var points = new List<Point>(48);
        points.AddRange(OutlinePoints(lens, shape, lensRadius));
        double sourceRadius = lensRadius / Math.Max(1e-9, lens.Width / Math.Max(1, source.Width));
        points.AddRange(OutlinePoints(source, shape, Math.Min(sourceRadius, Math.Min(source.Width, source.Height) / 2)));
        return ConvexHull(points);
    }

    /// <summary>形状轮廓采样点（凸包输入；椭圆 32 段，其余按角点）。</summary>
    public static IReadOnlyList<Point> OutlinePoints(Rect rect, ShapeKind shape, double radius)
    {
        var list = new List<Point>(40);
        if (rect.Width <= 0.01 || rect.Height <= 0.01) return list;

        switch (shape)
        {
            case ShapeKind.Ellipse:
                for (int i = 0; i < 32; i++)
                {
                    double a = i * Math.PI * 2 / 32;
                    list.Add(new Point(rect.X + rect.Width / 2 + Math.Cos(a) * rect.Width / 2,
                        rect.Y + rect.Height / 2 + Math.Sin(a) * rect.Height / 2));
                }
                break;
            case ShapeKind.Diamond:
                list.Add(new Point(rect.X + rect.Width / 2, rect.Y));
                list.Add(new Point(rect.Right, rect.Y + rect.Height / 2));
                list.Add(new Point(rect.X + rect.Width / 2, rect.Bottom));
                list.Add(new Point(rect.X, rect.Y + rect.Height / 2));
                break;
            default:
            {
                double k = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
                if (k <= 0.01)
                {
                    list.Add(rect.TopLeft);
                    list.Add(rect.TopRight);
                    list.Add(rect.BottomRight);
                    list.Add(rect.BottomLeft);
                    break;
                }
                foreach (var (cx, cy, start) in new[]
                         {
                             (rect.Left + k, rect.Top + k, Math.PI),
                             (rect.Right - k, rect.Top + k, -Math.PI / 2),
                             (rect.Right - k, rect.Bottom - k, 0),
                             (rect.Left + k, rect.Bottom - k, Math.PI / 2),
                         })
                {
                    for (int i = 0; i < 5; i++)
                    {
                        double a = start + i * (Math.PI / 2) / 4;
                        list.Add(new Point(cx + Math.Cos(a) * k, cy + Math.Sin(a) * k));
                    }
                }
                break;
            }
        }
        return list;
    }

    private static IReadOnlyList<Point> ConvexHull(List<Point> points)
    {
        if (points.Count <= 2) return points;

        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var hull = new List<Point>(points.Count * 2);

        double Cross(Point o, Point a, Point b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        foreach (var p in points)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = points.Count - 2; i >= 0; i--)
        {
            var p = points[i];
            while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>凸包转几何（渐变桥的填充范围）。</summary>
    public static Geometry HullGeometry(Rect lens, Rect source, ShapeKind shape, double lensRadius)
    {
        var hull = Hull(lens, source, shape, lensRadius);
        if (hull.Count < 3) return Geometry.Empty;

        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(hull[0], isFilled: true, isClosed: true);
            for (int i = 1; i < hull.Count; i++) ctx.LineTo(hull[i], true, false);
        }
        path.Freeze();
        return path;
    }
}
