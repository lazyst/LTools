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
}
