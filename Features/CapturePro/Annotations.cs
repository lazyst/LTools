using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一条标注。**不可变**：移动、改样式都返回新实例，撤销栈只存整份列表快照。
/// 几何一律用「窗口 DIP 坐标」（= 物理像素 / 该屏缩放），换算只在覆盖层入口做。
/// </summary>
internal abstract class Mark
{
    /// <summary>外接矩形（命中、裁剪、选中框用）。</summary>
    public abstract Rect Bounds { get; }

    /// <summary>
    /// 选中框 / 缩放手柄用的矩形：外接框去掉描边的一半（选中框贴着**笔迹**而不是笔迹外的空当）。
    /// 改成缩放时看到的和拖到的是同一个框。
    /// </summary>
    public virtual Rect Frame => Bounds;

    /// <summary>能不能拖手柄改大小（序号徽标是固定尺寸的，只能移动）。</summary>
    public virtual bool CanScale => true;

    /// <summary>
    /// 按「旧框 → 新框」缩放这一条：各自的定义点按 <see cref="MarkGeometry.MapPoint"/> 搬过去，
    /// 线宽 / 字号 / 块大小乘 <see cref="MarkGeometry.ScaleOf"/>。所以拉成扁的它就是扁的（变形），
    /// 不是「在新位置重画一遍原样」。
    /// </summary>
    public abstract Mark ScaleTo(Rect from, Rect to);

    /// <summary>整体平移；用于「选择」工具拖动。</summary>
    public abstract Mark MoveBy(Vector delta);

    /// <summary>命中测试：<paramref name="tolerance"/> 是额外的宽容距离（DIP）。</summary>
    public abstract bool HitTest(Point point, double tolerance);

    /// <summary>画笔 / 荧光笔：一条手绘折线。</summary>
    public sealed class Stroke : Mark
    {
        public Stroke(IReadOnlyList<Point> points, Color color, double width, bool highlighter, double opacity)
        {
            Points = points;
            Color = color;
            Width = width;
            Highlighter = highlighter;
            Opacity = opacity;
        }

        public IReadOnlyList<Point> Points { get; }
        public Color Color { get; }

        /// <summary>线宽（DIP）。</summary>
        public double Width { get; }

        /// <summary>荧光笔：颜色带透明度、笔头方、整体压在半透明层。</summary>
        public bool Highlighter { get; }

        /// <summary>透明度 0–1（荧光笔用，普通画笔恒为 1）。</summary>
        public double Opacity { get; }

        public override Rect Bounds => MarkGeometry.BoundsOf(Points, Width);

        public override Rect Frame => MarkGeometry.BoundsOf(Points, 0);

        public override Mark MoveBy(Vector delta) =>
            new Stroke(MarkGeometry.Shift(Points, delta), Color, Width, Highlighter, Opacity);

        public override Mark ScaleTo(Rect from, Rect to) => new Stroke(
            MarkGeometry.MapAll(Points, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), Highlighter, Opacity);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearPolyline(Points, point, Width / 2 + tolerance);
    }

    /// <summary>矩形 / 椭圆（只有描边，不填充）。</summary>
    public sealed class Shape : Mark
    {
        public Shape(Point a, Point b, Color color, double width, bool dashed, bool ellipse)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
            Dashed = dashed;
            Ellipse = ellipse;
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }
        public bool Dashed { get; }
        public bool Ellipse { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => MarkGeometry.Inflate(Rect, Width / 2);

        public override Rect Frame => Rect;

        public override Mark MoveBy(Vector delta) =>
            new Shape(A + delta, B + delta, Color, Width, Dashed, Ellipse);

        public override Mark ScaleTo(Rect from, Rect to) => new Shape(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), Dashed, Ellipse);

        public override bool HitTest(Point point, double tolerance)
        {
            var r = Rect;
            double t = Width / 2 + tolerance;
            bool nearBorder =
                MarkGeometry.NearSegment(point, new Point(r.Left, r.Top), new Point(r.Right, r.Top), t) ||
                MarkGeometry.NearSegment(point, new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), t) ||
                MarkGeometry.NearSegment(point, new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom), t) ||
                MarkGeometry.NearSegment(point, new Point(r.Left, r.Bottom), new Point(r.Left, r.Top), t);
            return nearBorder;
        }
    }

    /// <summary>箭头：起点 + 终点 + 三角箭头。</summary>
    public sealed class Arrow : Mark
    {
        public Arrow(Point start, Point end, Color color, double width)
        {
            Start = start;
            End = end;
            Color = color;
            Width = width;
        }

        public Point Start { get; }
        public Point End { get; }
        public Color Color { get; }
        public double Width { get; }

        public override Rect Bounds => MarkGeometry.Inflate(
            MarkGeometry.RectOf(Start, End), Width + MarkGeometry.HeadSize(Width));

        public override Rect Frame => MarkGeometry.RectOf(Start, End);

        public override Mark MoveBy(Vector delta) =>
            new Arrow(Start + delta, End + delta, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to) => new Arrow(
            MarkGeometry.MapPoint(Start, from, to), MarkGeometry.MapPoint(End, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, Start, End, Width / 2 + tolerance);
    }

    /// <summary>折线 / 曲线（多段线，可带箭头）。</summary>
    public sealed class Polyline : Mark
    {
        public Polyline(IReadOnlyList<Point> points, Color color, double width, bool dashed)
        {
            Points = points;
            Color = color;
            Width = width;
            Dashed = dashed;
        }

        public IReadOnlyList<Point> Points { get; }
        public Color Color { get; }
        public double Width { get; }
        public bool Dashed { get; }

        public override Rect Bounds => MarkGeometry.BoundsOf(Points, Width);

        public override Rect Frame => MarkGeometry.BoundsOf(Points, 0);

        public override Mark MoveBy(Vector delta) =>
            new Polyline(MarkGeometry.Shift(Points, delta), Color, Width, Dashed);

        public override Mark ScaleTo(Rect from, Rect to) => new Polyline(
            MarkGeometry.MapAll(Points, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), Dashed);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearPolyline(Points, point, Width / 2 + tolerance);
    }

    /// <summary>文字：可带外描边与半透明背景。</summary>
    public sealed class Text : Mark
    {
        public Text(Point origin, string text, double fontSize, Color color,
            double outlineWidth, Color outlineColor, double backgroundOpacity, double radius)
        {
            Origin = origin;
            Content = text;
            FontSize = fontSize;
            Color = color;
            OutlineWidth = outlineWidth;
            OutlineColor = outlineColor;
            BackgroundOpacity = backgroundOpacity;
            Radius = radius;
            Measured = MarkGeometry.MeasureText(text, fontSize);
        }

        public Point Origin { get; }
        public string Content { get; }
        public double FontSize { get; }
        public Color Color { get; }

        /// <summary>外描边宽度（0 = 不描边）。</summary>
        public double OutlineWidth { get; }

        public Color OutlineColor { get; }

        /// <summary>背景不透明度 0–1（0 = 无背景）。</summary>
        public double BackgroundOpacity { get; }

        /// <summary>背景圆角。</summary>
        public double Radius { get; }

        /// <summary>排版尺寸（构造时量一次，命中与背景框都用它）。</summary>
        public Size Measured { get; }

        public Rect Box => new(Origin.X - MarkGeometry.TextPad, Origin.Y - MarkGeometry.TextPad,
            Measured.Width + MarkGeometry.TextPad * 2, Measured.Height + MarkGeometry.TextPad * 2);

        public override Rect Bounds => Box;

        public override Mark MoveBy(Vector delta) => new Text(Origin + delta, Content, FontSize, Color,
            OutlineWidth, OutlineColor, BackgroundOpacity, Radius);

        /// <summary>改字号而不是改行数：文字不会被拉成歪的，放大就是字变大。</summary>
        public override Mark ScaleTo(Rect from, Rect to) => new Text(
            MarkGeometry.MapPoint(Origin, from, to), Content,
            Math.Max(6, FontSize * MarkGeometry.ScaleOf(from, to)), Color,
            OutlineWidth, OutlineColor, BackgroundOpacity, Radius);

        public override bool HitTest(Point point, double tolerance) => Box.Contains(point);
    }

    /// <summary>序号：圆形 / 方形 / 圆角方徽标 + 数字，序号由文档递增分配，删除不重排。</summary>
    public sealed class Serial : Mark
    {
        public Serial(Point center, int number, Color color, SerialShape shape, SerialNumbering numbering)
        {
            Center = center;
            Number = number;
            Color = color;
            BadgeShape = shape;
            Numbering = numbering;
        }

        public Point Center { get; }
        public int Number { get; }
        public Color Color { get; }

        /// <summary>徽标形状。</summary>
        public SerialShape BadgeShape { get; }

        /// <summary>数字写法（阿拉伯 / 罗马 / 字母 / 中文）。</summary>
        public SerialNumbering Numbering { get; }

        /// <summary>徽标上要画的字（罗马、中文比数字长，字号由渲染按长度收）。</summary>
        public string Label => SerialLabel.Of(Number, Numbering);

        public const double Radius = 13;

        public Rect Box => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

        public override Rect Bounds => Box;

        /// <summary>徽标直径是固定的（数字要看得清），所以它只有移动、没有缩放。</summary>
        public override bool CanScale => false;

        public override Mark MoveBy(Vector delta) => new Serial(Center + delta, Number, Color, BadgeShape, Numbering);

        public override Mark ScaleTo(Rect from, Rect to) =>
            new Serial(MarkGeometry.MapPoint(Center, from, to), Number, Color, BadgeShape, Numbering);

        public override bool HitTest(Point point, double tolerance)
        {
            var b = Box;
            b.Inflate(tolerance, tolerance);
            return b.Contains(point);
        }
    }

    /// <summary>马赛克：把一个矩形区域按块大小像素化（P0 只有矩形）。</summary>
    public sealed class Mosaic : Mark
    {
        public Mosaic(Point a, Point b, double block)
        {
            A = a;
            B = b;
            Block = block;
        }

        public Point A { get; }
        public Point B { get; }

        /// <summary>块边长（DIP）。</summary>
        public double Block { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => Rect;

        public override Mark MoveBy(Vector delta) => new Mosaic(A + delta, B + delta, Block);

        public override Mark ScaleTo(Rect from, Rect to) => new Mosaic(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to),
            Math.Max(2, Block * MarkGeometry.ScaleOf(from, to)));

        public override bool HitTest(Point point, double tolerance)
        {
            var r = Rect;
            r.Inflate(tolerance, tolerance);
            return r.Contains(point);
        }
    }

    /// <summary>
    /// 矩形滤镜：把一块区域按类型处理（模糊 / 灰度 / 反相 / 浮雕 / 亮度）。
    /// 与马赛克分开：马赛克只是「打码」，滤镜是可选择的处理方式。
    /// </summary>
    public sealed class Filter : Mark
    {
        public Filter(Point a, Point b, FilterKind kind, double strength)
        {
            A = a;
            B = b;
            Kind = kind;
            Strength = strength;
        }

        public Point A { get; }
        public Point B { get; }

        /// <summary>处理方式。</summary>
        public FilterKind Kind { get; }

        /// <summary>强度 0–100（模糊 = 半径、亮度 = 50 是原样、其余忽略）。</summary>
        public double Strength { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => Rect;

        public override Mark MoveBy(Vector delta) => new Filter(A + delta, B + delta, Kind, Strength);

        public override Mark ScaleTo(Rect from, Rect to) => new Filter(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Kind, Strength);

        public override bool HitTest(Point point, double tolerance)
        {
            var r = Rect;
            r.Inflate(tolerance, tolerance);
            return r.Contains(point);
        }
    }

    /// <summary>聚光灯：框内保持原样，框外压暗。</summary>
    public sealed class Spotlight : Mark
    {
        public Spotlight(Point a, Point b, double dim)
        {
            A = a;
            B = b;
            Dim = dim;
        }

        public Point A { get; }
        public Point B { get; }

        /// <summary>框外压暗的不透明度 0–1。</summary>
        public double Dim { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => Rect;

        public override Mark MoveBy(Vector delta) => new Spotlight(A + delta, B + delta, Dim);

        public override Mark ScaleTo(Rect from, Rect to) => new Spotlight(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Dim);

        public override bool HitTest(Point point, double tolerance)
        {
            var r = Rect;
            r.Inflate(tolerance, tolerance);
            return r.Contains(point);
        }
    }

    // ============ P1：与 Snow Shot 对齐的补充工具 ============

    /// <summary>直线：两点之间的一条线（没有箭头）。</summary>
    public sealed class Line : Mark
    {
        public Line(Point a, Point b, Color color, double width, bool dashed)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
            Dashed = dashed;
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }
        public bool Dashed { get; }

        public override Rect Bounds => MarkGeometry.Inflate(MarkGeometry.RectOf(A, B), Width / 2);
        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) => new Line(A + delta, B + delta, Color, Width, Dashed);

        public override Mark ScaleTo(Rect from, Rect to) => new Line(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), Dashed);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, A, B, Width / 2 + tolerance);
    }

    /// <summary>
    /// 测距：两端带垂直刻度的线段 + 中点标签（按**物理像素**显示长度，与上游一致）。
    /// 刻度长度随线宽变大，短线段也不会糊成一团。
    /// </summary>
    public sealed class Distance : Mark
    {
        public const double FontSize = 12;

        public Distance(Point a, Point b, Color color, double width)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }

        /// <summary>端部刻度长度（DIP）。</summary>
        public double Tick => Math.Max(10, Width * 3);

        public Point Midpoint => new((A.X + B.X) / 2, (A.Y + B.Y) / 2);

        /// <summary>标签文字：长度按缩放换成物理像素。</summary>
        public string LabelText(double scale) =>
            Math.Round((B - A).Length * (scale <= 0 ? 1 : scale)).ToString("0");

        public Size LabelSize(double scale) => MarkGeometry.MeasureText(LabelText(scale), FontSize);

        /// <summary>标签框（中线正上方）。绘制与命中取同一个框。</summary>
        public Rect LabelBox(double scale)
        {
            var size = LabelSize(scale);
            return new Rect(Midpoint.X - size.Width / 2 - 4, Midpoint.Y - size.Height - 12,
                size.Width + 8, size.Height + 4);
        }

        public override Rect Bounds
        {
            get
            {
                var box = MarkGeometry.Inflate(MarkGeometry.RectOf(A, B), Math.Max(Width / 2, Tick));
                box.Union(LabelBox(1));
                return box;
            }
        }

        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) => new Distance(A + delta, B + delta, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to) => new Distance(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, A, B, Width / 2 + tolerance) ||
            LabelBox(1).Contains(point);
    }

    /// <summary>
    /// 测角：一个顶点 + 两条等长射线 + 中间一段圆弧 + 角度标签。
    /// 角度值可以被滚轮调整（覆盖层负责），改动只换「张角」，方位与臂长不动。
    /// </summary>
    public sealed class Angle : Mark
    {
        public const double FontSize = 12;

        public Angle(Point vertex, double radius, double bisector, double degrees, Color color, double width)
        {
            Vertex = vertex;
            Radius = radius;
            Bisector = bisector;
            Degrees = Math.Clamp(degrees, 1, 359);
            Color = color;
            Width = width;
        }

        /// <summary>用两条臂的三个点造一个角（顶点 + 两个臂上的点）。</summary>
        public static Angle FromArms(Point vertex, Point p1, Point p2, Color color, double width)
        {
            var v1 = p1 - vertex;
            var v2 = p2 - vertex;
            double a1 = Math.Atan2(v1.Y, v1.X);
            double a2 = Math.Atan2(v2.Y, v2.X);
            double delta = MarkGeometry.NormalizeAngle(a2 - a1);
            bool reflex = delta > Math.PI;
            double open = reflex ? Math.PI * 2 - delta : delta;
            double bisector = MarkGeometry.NormalizeAngle(reflex ? a2 + open / 2 : a1 + open / 2);
            double radius = Math.Clamp((v1.Length + v2.Length) / 2, 24, 400);
            return new Angle(vertex, radius, bisector, open * 180 / Math.PI, color, width);
        }

        public Point Vertex { get; }
        public double Radius { get; }
        public double Degrees { get; }

        /// <summary>角平分线方向（弧度）；两条射线是它左右各转半个张角。</summary>
        public double Bisector { get; }

        public Color Color { get; }
        public double Width { get; }

        private double Open => Degrees * Math.PI / 180;

        public Point A => MarkGeometry.Polar(Vertex, Bisector - Open / 2, Radius);
        public Point B => MarkGeometry.Polar(Vertex, Bisector + Open / 2, Radius);

        /// <summary>圆弧半径（臂长的 55%，短臂也不会跑到臂外）。</summary>
        public double ArcRadius => Math.Clamp(Radius * 0.55, 14, 90);

        public string LabelText => Math.Round(Degrees).ToString("0") + "°";

        public Point LabelAt => MarkGeometry.Polar(Vertex, Bisector, ArcRadius + 16);

        public Rect LabelBox
        {
            get
            {
                var size = MarkGeometry.MeasureText(LabelText, FontSize);
                var at = LabelAt;
                return new Rect(at.X - size.Width / 2 - 4, at.Y - size.Height / 2 - 2,
                    size.Width + 8, size.Height + 4);
            }
        }

        public Angle WithDegrees(double degrees) =>
            new(Vertex, Radius, Bisector, degrees, Color, Width);

        public override Rect Bounds
        {
            get
            {
                var box = MarkGeometry.RectOf(A, B);
                box.Union(Vertex);
                box = MarkGeometry.Inflate(box, ArcRadius + 24);
                box.Union(LabelBox);
                return box;
            }
        }

        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) =>
            new Angle(Vertex + delta, Radius, Bisector, Degrees, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            double scale = MarkGeometry.ScaleOf(from, to);
            return new Angle(MarkGeometry.MapPoint(Vertex, from, to), Math.Max(18, Radius * scale),
                Bisector, Degrees, Color, Math.Max(0.5, Width * scale));
        }

        public override bool HitTest(Point point, double tolerance)
        {
            double t = Width / 2 + tolerance;
            return MarkGeometry.NearSegment(point, Vertex, A, t) ||
                   MarkGeometry.NearSegment(point, Vertex, B, t);
        }
    }

    /// <summary>括号：一条主线 + 两端垂直刻度（相当于不带数字的测距）。</summary>
    public sealed class Bracket : Mark
    {
        public Bracket(Point a, Point b, Color color, double width)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }

        public double Tick => Math.Max(10, Width * 3);

        public override Rect Bounds => MarkGeometry.Inflate(MarkGeometry.RectOf(A, B), Math.Max(Width / 2, Tick));
        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) => new Bracket(A + delta, B + delta, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to) => new Bracket(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, A, B, Math.Max(Width / 2 + tolerance, Tick / 2));
    }

    /// <summary>
    /// 气泡：圆角矩形 + 左下角一条指向外的尾巴。
    /// 尾巴尺寸按气泡宽高的比例算（小气泡不会挂一条比身体还大的尾巴）。
    /// </summary>
    public sealed class SpeechBubble : Mark
    {
        public SpeechBubble(Rect body, Color color, double width)
        {
            Body = body;
            Color = color;
            Width = width;
        }

        public Rect Body { get; }
        public Color Color { get; }
        public double Width { get; }

        /// <summary>尾巴根部离左边缘的距离。</summary>
        public double TailInset => Math.Clamp(Body.Width * 0.12, 10, 28);

        public double TailWidth => Math.Clamp(Body.Width * 0.20, 14, 46);

        public double TailHeight => Math.Clamp(TailWidth * 0.9, 12, 42);

        /// <summary>尾巴三角形的三个点（根两点 + 尖一点）。</summary>
        public Point[] Tail => new[]
        {
            new Point(Body.Left + TailInset, Body.Bottom - Width / 2),
            new Point(Body.Left + TailInset + TailWidth, Body.Bottom - Width / 2),
            new Point(Body.Left + TailInset + TailWidth * 0.35, Body.Bottom + TailHeight),
        };

        /// <summary>气泡圆角。</summary>
        public double Radius => Math.Clamp(Math.Min(Body.Width, Body.Height) * 0.18, 4, 16);

        public override Rect Frame
        {
            get
            {
                var box = Body;
                box.Union(Tail[2]);
                return box;
            }
        }

        public override Rect Bounds => MarkGeometry.Inflate(Frame, Width / 2);

        public override Mark MoveBy(Vector delta) => new SpeechBubble(
            new Rect(Body.X + delta.X, Body.Y + delta.Y, Body.Width, Body.Height), Color, Width);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            var a = MarkGeometry.MapPoint(Body.TopLeft, from, to);
            var b = MarkGeometry.MapPoint(Body.BottomRight, from, to);
            return new SpeechBubble(MarkGeometry.RectOf(a, b), Color,
                Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));
        }

        public override bool HitTest(Point point, double tolerance)
        {
            if (MarkGeometry.Inflate(Body, tolerance).Contains(point)) return true;
            var tail = Tail;
            return MarkGeometry.InTriangle(point, tail[0], tail[1], tail[2]);
        }
    }

    /// <summary>
    /// 放大镜：把底图上「镜头中心」附近的一小块按倍率放大画进镜头框。
    /// 镜头框就是它在外接框里的位置；倍率默认 2 倍。
    /// </summary>
    public sealed class Magnifier : Mark
    {
        public Magnifier(Rect lens, double factor, Color color, double width)
        {
            Lens = lens;
            Factor = Math.Clamp(factor, 1.2, 8);
            Color = color;
            Width = width;
        }

        public Rect Lens { get; }
        public double Factor { get; }
        public Color Color { get; }
        public double Width { get; }

        /// <summary>取图区域：镜头中心、边长 = 镜头 / 倍率（DIP，渲染时再按底图裁）。</summary>
        public Rect SourceRect
        {
            get
            {
                double w = Lens.Width / Factor, h = Lens.Height / Factor;
                return new Rect(Lens.X + (Lens.Width - w) / 2, Lens.Y + (Lens.Height - h) / 2, w, h);
            }
        }

        public override Rect Bounds => MarkGeometry.Inflate(Lens, Width / 2 + 2);
        public override Rect Frame => Lens;

        public override Mark MoveBy(Vector delta) => new Magnifier(
            new Rect(Lens.X + delta.X, Lens.Y + delta.Y, Lens.Width, Lens.Height), Factor, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            var a = MarkGeometry.MapPoint(Lens.TopLeft, from, to);
            var b = MarkGeometry.MapPoint(Lens.BottomRight, from, to);
            return new Magnifier(MarkGeometry.RectOf(a, b), Factor, Color,
                Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));
        }

        public override bool HitTest(Point point, double tolerance)
        {
            var box = Lens;
            box.Inflate(tolerance, tolerance);
            return box.Contains(point);
        }
    }

    /// <summary>
    /// 水印：整张图上斜向平铺的文字（颜色 / 字号 / 角度 / 间距 / 不透明度）。
    /// 它是**覆盖整张图**的效果，不参与选中 / 拖动（<see cref="Bounds"/> 为空、<see cref="HitTest"/> 恒 false）。
    /// </summary>
    public sealed class Watermark : Mark
    {
        public Watermark(string text, Color color, double fontSize, double angle, double gap, double opacity)
        {
            Content = string.IsNullOrWhiteSpace(text) ? "LTools" : text;
            Color = color;
            FontSize = Math.Clamp(fontSize, 6, 512);
            Rotation = angle;
            Gap = Math.Clamp(gap, 10, 600);
            Opacity = Math.Clamp(opacity, 0.02, 1);
        }

        public string Content { get; }
        public Color Color { get; }
        public double FontSize { get; }

        /// <summary>倾斜角度（度，逆时针为正）。</summary>
        public double Rotation { get; }

        /// <summary>相邻两块水印的间距（DIP）。</summary>
        public double Gap { get; }

        public double Opacity { get; }

        public override Rect Bounds => Rect.Empty;
        public override Rect Frame => Rect.Empty;
        public override bool CanScale => false;

        public override Mark MoveBy(Vector delta) => this;

        public override Mark ScaleTo(Rect from, Rect to) => this;

        public override bool HitTest(Point point, double tolerance) => false;
    }

    /// <summary>矩形高亮：把一块区域铺上半透明色（文字打底用）。</summary>
    public sealed class Highlight : Mark
    {
        public Highlight(Point a, Point b, Color color, double opacity)
        {
            A = a;
            B = b;
            Color = color;
            Opacity = Math.Clamp(opacity, 0.02, 1);
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Opacity { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => Rect;

        public override Mark MoveBy(Vector delta) => new Highlight(A + delta, B + delta, Color, Opacity);

        public override Mark ScaleTo(Rect from, Rect to) => new Highlight(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color, Opacity);

        public override bool HitTest(Point point, double tolerance)
        {
            var r = Rect;
            r.Inflate(tolerance, tolerance);
            return r.Contains(point);
        }
    }

    /// <summary>
    /// 矩形橡皮 / 画笔橡皮落下的「恢复原图」块：把这块像素还原成截图原样，盖掉它下面的标注、
    /// 滤镜与打码（对齐上游的 <c>RestoreBackground</c> 滤镜，`SnowCanvasTool::RectangleEraser` /
    /// <c>BrushEraser</c> 各自落的是一条矩形 / 画笔滤镜）。
    /// <para>
    /// 上游这种块是**不可选、不可删**的（点不中、旧橡皮也删不掉，只能用 Ctrl+Z 撤销），
    /// 所以 <see cref="CaptureDocument.HitTest"/> 会跳过它，渲染时它只还原底图像素。
    /// </para>
    /// </summary>
    public sealed class Restore : Mark
    {
        /// <summary>矩形橡皮：<paramref name="path"/> 为空，整块都还原。</summary>
        public Restore(Point a, Point b, IReadOnlyList<Point>? path = null, double width = 0)
        {
            A = a;
            B = b;
            Path = path;
            Width = Math.Max(0, width);
        }

        public Point A { get; }
        public Point B { get; }

        /// <summary>画笔橡皮的笔迹；为空 = 矩形橡皮（整块还原）。名字不能叫 Stroke，会撞 <see cref="Mark.Stroke"/>。</summary>
        public IReadOnlyList<Point>? Path { get; }
        public double Width { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds =>
            Path == null ? Rect : MarkGeometry.BoundsOf(Path, Width);

        public override Rect Frame => Path == null ? Rect : MarkGeometry.BoundsOf(Path, 0);

        public override Mark MoveBy(Vector delta) => new Restore(
            A + delta, B + delta,
            Path == null ? null : MarkGeometry.Shift(Path, delta), Width);

        public override Mark ScaleTo(Rect from, Rect to) => new Restore(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to),
            Path == null ? null : MarkGeometry.MapAll(Path, from, to),
            Path == null ? Width : Math.Max(1, Width * MarkGeometry.ScaleOf(from, to)));

        /// <summary>永远点不中：上游的恢复块不参与选择 / 命中。</summary>
        public override bool HitTest(Point point, double tolerance) => false;
    }

    /// <summary>
    /// 画笔滤镜：沿笔迹把路过的像素按类型处理（马赛克 / 模糊 / 灰度…）。
    /// 与矩形滤镜同一套 <see cref="FilterKind"/>，只是遮罩来自笔迹而不是矩形。
    /// </summary>
    public sealed class BrushFilter : Mark
    {
        public BrushFilter(IReadOnlyList<Point> points, double width, FilterKind kind, double strength)
        {
            Points = points;
            Width = Math.Max(2, width);
            Kind = kind;
            Strength = strength;
        }

        public IReadOnlyList<Point> Points { get; }
        public double Width { get; }
        public FilterKind Kind { get; }
        public double Strength { get; }

        public override Rect Bounds => MarkGeometry.BoundsOf(Points, Width);
        public override Rect Frame => MarkGeometry.BoundsOf(Points, 0);

        public override Mark MoveBy(Vector delta) =>
            new BrushFilter(MarkGeometry.Shift(Points, delta), Width, Kind, Strength);

        public override Mark ScaleTo(Rect from, Rect to) => new BrushFilter(
            MarkGeometry.MapAll(Points, from, to),
            Math.Max(2, Width * MarkGeometry.ScaleOf(from, to)), Kind, Strength);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearPolyline(Points, point, Width / 2 + tolerance);
    }
}
