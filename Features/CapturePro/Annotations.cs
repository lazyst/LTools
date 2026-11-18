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

    /// <summary>矩形 / 椭圆 / 菱形（形状族：描边 + 可选填充，见 <see cref="ShapeSpec"/>）。</summary>
    public sealed class Shape : Mark
    {
        /// <summary>简化构造（录制窗口的旧调用点）：直角矩形 / 椭圆，只有描边。</summary>
        public Shape(Point a, Point b, Color color, double width, bool dashed, bool ellipse)
            : this(a, b, new ShapeSpec(
                ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle, color, width,
                dashed ? StrokeKind.Dashed : StrokeKind.Solid, StrokeRendering.Architect,
                Colors.Transparent, FillKind.None, 0, ShapeInk.NextSeed()))
        {
        }

        public Shape(Point a, Point b, ShapeSpec spec)
        {
            A = a;
            B = b;
            Spec = spec;
        }

        public Point A { get; }
        public Point B { get; }
        public ShapeSpec Spec { get; }

        public Color Color => Spec.Stroke;
        public double Width => Spec.Width;
        public bool Dashed => Spec.StrokeStyle == StrokeKind.Dashed;
        public bool Ellipse => Spec.Kind == ShapeKind.Ellipse;

        public Rect Rect => MarkGeometry.RectOf(A, B);

        /// <summary>当前形状的轮廓（命中 / 上色共用一份）。</summary>
        public IReadOnlyList<InkSegment> Contour => ShapeInk.Contour(Rect, Spec.Kind, Spec.Radius);

        public override Rect Bounds =>
            MarkGeometry.Inflate(Rect, Width / 2 + ShapeInk.Outset(Spec.Rendering));

        public override Rect Frame => Rect;

        public override Mark MoveBy(Vector delta) => new Shape(A + delta, B + delta, Spec);

        public override Mark ScaleTo(Rect from, Rect to) => new Shape(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to),
            Spec with { Width = Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)) });

        public override bool HitTest(Point point, double tolerance) =>
            ShapeInk.HitTest(new[] { new InkPart(Contour, true) }, Spec, point, tolerance);
    }

    /// <summary>
    /// 箭头：点列 + 一整套 <see cref="ArrowSpec"/>（类型 / 两端端点 / 杆型 / 线型 / 笔触 / 外描边）。
    /// 曲线与折线的拐点保存在 <see cref="Points"/> 里，改类型只换 <see cref="Spec"/> 不丢点。
    /// </summary>
    public sealed class Arrow : Mark
    {
        /// <summary>旧的四参构造（只有首尾两点、三角箭头）——给自检与旧调用点兜底。</summary>
        public Arrow(Point start, Point end, Color color, double width)
            : this(new[] { start, end }, ArrowSpec.Default with
            {
                Stroke = color, Width = width, Seed = ShapeInk.NextSeed(),
            })
        {
        }

        public Arrow(IReadOnlyList<Point> points, ArrowSpec spec)
        {
            Points = points.Count >= 2 ? points : new[] { new Point(), new Point() };
            Spec = spec.Clamped();
        }

        public IReadOnlyList<Point> Points { get; }
        public ArrowSpec Spec { get; }

        /// <summary>起点 / 终点（派生，别单独存）。</summary>
        public Point Start => Points[0];

        public Point End => Points[^1];

        public Color Color => Spec.Stroke;

        public double Width => Spec.Width;

        public override Rect Bounds
        {
            get
            {
                var flat = ArrowGeometry.Flatten(Points, Spec.Type);
                double pad = Spec.Width / 2 + Spec.OutlineWidth +
                    MarkGeometry.HeadSize(Spec.Width) * Math.Max(1, Spec.Ratio) +
                    ShapeInk.Outset(Spec.Rendering);
                return MarkGeometry.Inflate(MarkGeometry.BoundsOf(flat, 0), pad);
            }
        }

        public override Rect Frame => MarkGeometry.BoundsOf(Points, 0);

        public override Mark MoveBy(Vector delta) =>
            new Arrow(MarkGeometry.Shift(Points, delta), Spec);

        public override Mark ScaleTo(Rect from, Rect to) => new Arrow(
            MarkGeometry.MapAll(Points, from, to),
            Spec with { Width = Math.Max(0.5, Spec.Width * MarkGeometry.ScaleOf(from, to)) });

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearPolyline(ArrowGeometry.Flatten(Points, Spec.Type), point,
                Spec.Width / 2 + Spec.OutlineWidth + tolerance);
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

    /// <summary>
    /// 文字：一整套 <see cref="TextSpec"/>（字色 / 字号 / 字体族 / 粗斜 / 对齐 / 描边 /
    /// 背景胶囊的填充色与方式 / 圆角）。绘制见 <see cref="TextInk"/>。
    /// </summary>
    public sealed class Text : Mark
    {
        public Text(Point origin, string text, TextSpec style)
        {
            Origin = origin;
            Content = text ?? string.Empty;
            Style = style;
            Measured = TextInk.Measure(Content, style);
        }

        public Point Origin { get; }
        public string Content { get; }

        /// <summary>这一条文字的样式（不可变，改样式 = 换一份）。</summary>
        public TextSpec Style { get; }

        /// <summary>墨迹尺寸（构造时量一次，不含背景内边距）。</summary>
        public Size Measured { get; }

        /// <summary>命中 / 选中框：墨迹 + 背景内边距 + 描边外溢。</summary>
        public Rect Box => TextInk.BoundsOf(Origin, Content, Style);

        public override Rect Bounds => Box;

        public override Mark MoveBy(Vector delta) => new Text(Origin + delta, Content, Style);

        /// <summary>改字号而不是改行数：文字不会被拉成歪的，放大就是字变大。</summary>
        public override Mark ScaleTo(Rect from, Rect to) => new Text(
            MarkGeometry.MapPoint(Origin, from, to), Content,
            Style with
            {
                FontSize = Math.Clamp(
                    Style.FontSize * MarkGeometry.ScaleOf(from, to),
                    TextInk.MinFontSize, TextInk.MaxFontSize),
            });

        public override bool HitTest(Point point, double tolerance) =>
            Rect.Inflate(Box, Math.Max(tolerance, MarkGeometry.TextPad), Math.Max(tolerance, MarkGeometry.TextPad))
                .Contains(point);
    }

    /// <summary>序号：徽标（5 种）+ 数字（5 种写法），序号由文档递增分配，删除不重排。</summary>
    public sealed class Serial : Mark
    {
        public Serial(Point center, int number, SerialSpec spec)
        {
            Center = center;
            Number = Math.Max(0, number);
            Spec = (spec ?? SerialSpec.Default).Clamped();
        }

        public Point Center { get; }
        public int Number { get; }

        /// <summary>整套画法（类型 / 写法 / 颜色 / 填充 / 字号 / 字体 / 描边 / 不透明度）。</summary>
        public SerialSpec Spec { get; }

        public SerialBadge Badge => Spec.Badge;
        public SerialNumbering Numbering => Spec.Numbering;
        public Color Color => Spec.Stroke;

        /// <summary>徽标上要画的字（<see cref="SerialBadge.Circle"/> 不写字）。</summary>
        public string Label => Spec.Numbered ? SerialLabel.Of(Number, Spec.Numbering) : string.Empty;

        /// <summary>徽标直径（1.68 × 字号，与写法 / 位数无关）。</summary>
        public double Diameter => Spec.Diameter;

        public Rect Box
        {
            get
            {
                double r = Diameter / 2;
                return new Rect(Center.X - r, Center.Y - r, Diameter, Diameter);
            }
        }

        public override Rect Bounds => Box;

        /// <summary>徽标直径由字号决定，所以它只有移动、没有缩放。</summary>
        public override bool CanScale => false;

        public override Mark MoveBy(Vector delta) => new Serial(Center + delta, Number, Spec);

        public override Mark ScaleTo(Rect from, Rect to) =>
            new Serial(MarkGeometry.MapPoint(Center, from, to), Number, Spec);

        public override bool HitTest(Point point, double tolerance)
        {
            var box = Box;
            box.Inflate(tolerance, tolerance);
            if (!box.Contains(point)) return false;
            if (Spec.Square) return true;

            double rx = box.Width / 2, ry = box.Height / 2;
            double dx = (point.X - Center.X) / rx, dy = (point.Y - Center.Y) / ry;
            return dx * dx + dy * dy <= 1;
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
        public Filter(Point a, Point b, FilterKind kind, double strength, int? autoRegionId = null)
        {
            A = a;
            B = b;
            Kind = kind;
            Strength = strength;
            AutoRegionId = autoRegionId;
        }

        public Point A { get; }
        public Point B { get; }

        /// <summary>处理方式。</summary>
        public FilterKind Kind { get; }

        /// <summary>强度 0–100（模糊 = 半径、亮度 = 50 是原样、其余忽略）。</summary>
        public double Strength { get; }

        /// <summary>
        /// 自动打码填出来的那一块对应哪个识别区域（1 起；普通矩形滤镜是 null）。
        /// 一个区域最多一条填充，再点一次同类型的就删掉它。
        /// </summary>
        public int? AutoRegionId { get; }

        public Rect Rect => MarkGeometry.RectOf(A, B);

        public override Rect Bounds => Rect;

        public override Mark MoveBy(Vector delta) => new Filter(A + delta, B + delta, Kind, Strength, AutoRegionId);

        public override Mark ScaleTo(Rect from, Rect to) => new Filter(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Kind, Strength, AutoRegionId);

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

    /// <summary>直线：两点之间的一条线（没有箭头）。线型 / 笔触风格与形状族共用。</summary>
    public sealed class Line : Mark
    {
        public Line(Point a, Point b, Color color, double width, bool dashed)
            : this(a, b, color, width, dashed, LineType.Straight)
        {
        }

        public Line(Point a, Point b, Color color, double width, bool dashed, LineType type)
            : this(a, b, color, width, dashed, type, StrokeRendering.Architect)
        {
        }

        public Line(Point a, Point b, Color color, double width, bool dashed, LineType type,
            StrokeRendering rendering)
            : this(a, b, color, width, dashed ? StrokeKind.Dashed : StrokeKind.Solid, type,
                rendering, ShapeInk.NextSeed())
        {
        }

        public Line(Point a, Point b, Color color, double width, StrokeKind style, LineType type,
            StrokeRendering rendering)
            : this(a, b, color, width, style, type, rendering, ShapeInk.NextSeed())
        {
        }

        public Line(Point a, Point b, Color color, double width, StrokeKind style, LineType type,
            StrokeRendering rendering, uint seed)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
            StrokeStyle = style;
            Type = type;
            Rendering = rendering;
            Seed = seed;
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }

        /// <summary>描边线型（实线 / 虚线 / 点线）。</summary>
        public StrokeKind StrokeStyle { get; }

        /// <summary>旧口径：是不是虚线（点线也算虚线）。</summary>
        public bool Dashed => StrokeStyle != StrokeKind.Solid;

        /// <summary>直线 / 曲线（上游两点直线与曲线都是直的，保留开关只为与上游参数一致）。</summary>
        public LineType Type { get; }

        public StrokeRendering Rendering { get; }
        public uint Seed { get; }

        /// <summary>描边用的样式（填充不适用）。</summary>
        public ShapeSpec StrokeSpec => new(ShapeKind.Rectangle, Color, Width,
            StrokeStyle, Rendering, Colors.Transparent, FillKind.None, 0, Seed);

        /// <summary>笔迹（不闭合）。</summary>
        public IReadOnlyList<InkSegment> Contour => new[] { InkSegment.Line(A, B) };

        public override Rect Bounds => MarkGeometry.Inflate(MarkGeometry.RectOf(A, B),
            Width / 2 + ShapeInk.Outset(Rendering));

        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) =>
            new Line(A + delta, B + delta, Color, Width, StrokeStyle, Type, Rendering, Seed);

        public override Mark ScaleTo(Rect from, Rect to) => new Line(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), StrokeStyle, Type, Rendering, Seed);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, A, B, Width / 2 + ShownOutset + tolerance);

        private double ShownOutset => ShapeInk.Outset(Rendering);
    }

    /// <summary>
    /// 测距：两端带端点样式（默认横杠）的线段 + 中点标签。
    /// 数值 / 单位 / 小数位 / 端点样式与比例照上游 <c>distance.rs</c>：默认厘米、0 位小数。
    /// </summary>
    public sealed class Distance : Mark
    {
        public Distance(Point a, Point b, Color color, double width)
            : this(a, b, color, width, DistanceSpec.Default)
        {
        }

        public Distance(Point a, Point b, Color color, double width, DistanceSpec spec)
        {
            A = a;
            B = b;
            Color = color;
            Width = width;
            Spec = spec.Clamped();
        }

        public Point A { get; }
        public Point B { get; }
        public Color Color { get; }
        public double Width { get; }

        /// <summary>单位 / 系数 / 小数位 / 端点样式。</summary>
        public DistanceSpec Spec { get; }

        /// <summary>线段长度（DIP）。</summary>
        public double Length => (B - A).Length;

        /// <summary>标签字号：上游 <c>20 × √(线宽 / 2)</c>。</summary>
        public double FontSize => MeasureLabel.FontSize(Width);

        public Point Midpoint => new((A.X + B.X) / 2, (A.Y + B.Y) / 2);

        /// <summary>标签文字：长度按物理缩放 × 系数，再带单位后缀。</summary>
        public string LabelText(double scale) => MeasureLabel.Distance(
            Length, scale <= 0 ? 1 : scale, Spec.Factor, Spec.Unit, Spec.Decimals);

        public Size LabelSize(double scale) => MarkGeometry.MeasureText(LabelText(scale), FontSize);

        /// <summary>标签框（上游把标签**居中放在中线上**，与线重叠；绘制与命中取同一个框）。</summary>
        public Rect LabelBox(double scale)
        {
            var size = LabelSize(scale);
            return new Rect(Midpoint.X - size.Width / 2 - 3, Midpoint.Y - size.Height / 2 - 1,
                size.Width + 6, size.Height + 2);
        }

        public override Rect Bounds
        {
            get
            {
                var box = MarkGeometry.Inflate(MarkGeometry.RectOf(A, B),
                    Math.Max(Width / 2, ArrowInk.Size(Spec.Endpoint) * Spec.EndpointScale / 2));
                box.Union(LabelBox(1));
                return box;
            }
        }

        public override Rect Frame => MarkGeometry.RectOf(A, B);

        public override Mark MoveBy(Vector delta) => new Distance(A + delta, B + delta, Color, Width, Spec);

        public override Mark ScaleTo(Rect from, Rect to) => new Distance(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(B, from, to), Color,
            Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)), Spec);

        public override bool HitTest(Point point, double tolerance) =>
            MarkGeometry.NearSegment(point, A, B, Math.Max(Width / 2 + tolerance,
                ArrowInk.Size(Spec.Endpoint) * Spec.EndpointScale / 2)) ||
            LabelBox(1).Contains(point);
    }

    /// <summary>
    /// 测角：一个顶点 + 两条等长射线 + 中间一段圆弧 + 角度标签。
    /// 角度值可以被滚轮调整（覆盖层负责），改动只换「张角」，方位与臂长不动。
    /// </summary>
    public sealed class Angle : Mark
    {
        public Angle(Point vertex, double radius, double bisector, double degrees, Color color, double width)
            : this(vertex, radius, bisector, degrees, color, width, AngleSpec.Default)
        {
        }

        public Angle(Point vertex, double radius, double bisector, double degrees, Color color, double width,
            AngleSpec spec)
        {
            Vertex = vertex;
            Radius = radius;
            Bisector = bisector;
            Degrees = Math.Clamp(degrees, 1, 359);
            Color = color;
            Width = width;
            Spec = spec.Clamped();
        }

        /// <summary>用两条臂的三个点造一个角（顶点 + 两个臂上的点）。</summary>
        public static Angle FromArms(Point vertex, Point p1, Point p2, Color color, double width) =>
            FromArms(vertex, p1, p2, color, width, AngleSpec.Default);

        public static Angle FromArms(Point vertex, Point p1, Point p2, Color color, double width, AngleSpec spec)
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
            return new Angle(vertex, radius, bisector, open * 180 / Math.PI, color, width, spec);
        }

        public Point Vertex { get; }
        public double Radius { get; }
        public double Degrees { get; }

        /// <summary>角平分线方向（弧度）；两条射线是它左右各转半个张角。</summary>
        public double Bisector { get; }

        public Color Color { get; }
        public double Width { get; }

        /// <summary>角度类型 / 单位 / 小数位。</summary>
        public AngleSpec Spec { get; }

        /// <summary>标签字号：上游 <c>20 × √(线宽 / 2)</c>。</summary>
        public double FontSize => MeasureLabel.FontSize(Width);

        private double Open => Degrees * Math.PI / 180;

        /// <summary>实际画出的圆弧张角：内角模式下大于 180° 的角画补角（上游 <c>Interior</c>）。</summary>
        public double ShownOpen =>
            Spec.Kind == AngleKind.Interior && Open > Math.PI ? Math.PI * 2 - Open : Open;

        /// <summary>圆弧的角平分线：内角折过去时落在另一侧。</summary>
        public double ShownBisector =>
            Spec.Kind == AngleKind.Interior && Open > Math.PI
                ? MarkGeometry.NormalizeAngle(Bisector + Math.PI)
                : Bisector;

        public Point A => MarkGeometry.Polar(Vertex, Bisector - Open / 2, Radius);
        public Point B => MarkGeometry.Polar(Vertex, Bisector + Open / 2, Radius);

        /// <summary>圆弧半径（臂长的 55%，短臂也不会跑到臂外）。</summary>
        public double ArcRadius => Math.Clamp(Radius * 0.55, 14, 90);

        public Point ArcStart => MarkGeometry.Polar(Vertex, ShownBisector - ShownOpen / 2, ArcRadius);
        public Point ArcEnd => MarkGeometry.Polar(Vertex, ShownBisector + ShownOpen / 2, ArcRadius);

        /// <summary>标签文字（度带 <c>°</c>、弧度带 <c>" rad"</c>，小数位由参数决定）。</summary>
        public string LabelText => MeasureLabel.Angle(Open, Spec.Kind, Spec.Unit, Spec.Decimals);

        public Point LabelAt => MarkGeometry.Polar(Vertex, ShownBisector, ArcRadius + FontSize * 0.9);

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
            new(Vertex, Radius, Bisector, degrees, Color, Width, Spec);

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
            new Angle(Vertex + delta, Radius, Bisector, Degrees, Color, Width, Spec);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            double scale = MarkGeometry.ScaleOf(from, to);
            return new Angle(MarkGeometry.MapPoint(Vertex, from, to), Math.Max(18, Radius * scale),
                Bisector, Degrees, Color, Math.Max(0.5, Width * scale), Spec);
        }

        public override bool HitTest(Point point, double tolerance)
        {
            double t = Width / 2 + tolerance;
            return MarkGeometry.NearSegment(point, Vertex, A, t) ||
                   MarkGeometry.NearSegment(point, Vertex, B, t);
        }
    }

    /// <summary>
    /// 括号：**花括号**（上游 <c>bracket.rs</c>：三点定义 a 起 / c 尖 / b 止，四段三次贝塞尔）。
    /// 拖拽时按上游 <c>process_bracket_creation_pointer_event</c> 把「起点 → 终点」换算成三点：
    /// 首尾落在起点的 x 上、纵向撑满，尖落在终点的 x 与中线上——所以开口朝拖动方向。
    /// </summary>
    public sealed class Bracket : Mark
    {
        private readonly BracketInk.Cubic[]? _segments;

        /// <summary>
        /// 拖拽成形（上游 <c>process_bracket_creation_pointer_event</c>）：
        /// 首尾落在起点的 y 两端、纵向撑满，尖在终点的 x 与中线——开口朝拖动方向。
        /// </summary>
        public Bracket(Point start, Point end, Color color, double width)
            : this(new Point(start.X, Math.Min(start.Y, end.Y)),
                new Point(end.X, (start.Y + end.Y) / 2),
                new Point(start.X, Math.Max(start.Y, end.Y)), color, width)
        {
        }

        /// <summary>三点成形（移动 / 缩放 / 反序列化走这条）。</summary>
        public Bracket(Point a, Point apex, Point b, Color color, double width)
        {
            A = a;
            Apex = apex;
            B = b;
            Color = color;
            Width = width;
            _segments = BracketInk.Segments(A, Apex, B);
        }

        /// <summary>起点（花括号的一端）。</summary>
        public Point A { get; }

        /// <summary>尖（花括号中间那个凸起）。</summary>
        public Point Apex { get; }

        /// <summary>终点（另一端）。</summary>
        public Point B { get; }

        public Color Color { get; }
        public double Width { get; }

        /// <summary>四段弧（退化时为空，绘制 / 命中都当没有）。</summary>
        public BracketInk.Cubic[] Segments => _segments ?? Array.Empty<BracketInk.Cubic>();

        public override Rect Bounds => _segments == null
            ? MarkGeometry.Inflate(MarkGeometry.RectOf(A, B), Width / 2 + 2)
            : BracketInk.Bounds(_segments, Width);

        public override Rect Frame
        {
            get
            {
                var box = MarkGeometry.RectOf(A, B);
                box.Union(Apex);
                return box;
            }
        }

        public override Mark MoveBy(Vector delta) =>
            new Bracket(A + delta, Apex + delta, B + delta, Color, Width);

        public override Mark ScaleTo(Rect from, Rect to) => new Bracket(
            MarkGeometry.MapPoint(A, from, to), MarkGeometry.MapPoint(Apex, from, to),
            MarkGeometry.MapPoint(B, from, to), Color, Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)));

        public override bool HitTest(Point point, double tolerance) =>
            _segments != null
                ? BracketInk.Hit(_segments, point, Width / 2 + tolerance)
                : MarkGeometry.NearSegment(point, A, B, Width / 2 + tolerance);
    }

    /// <summary>
    /// 气泡：身体轮廓（矩形 / 椭圆 / 菱形 / 思考气泡云朵）+ 一条指向外的尾巴。
    /// 尾巴根部取自身体轮廓（上游 <c>speech_bubble</c>），所以身体边线不会横穿尾巴；
    /// 思考气泡的尾巴是一串由大到小的圆点。
    /// </summary>
    public sealed class SpeechBubble : Mark
    {
        /// <summary>简化构造：圆角矩形 + 左下角一条尾巴（录制窗口的旧调用点）。</summary>
        public SpeechBubble(Rect body, Color color, double width)
            : this(body, new ShapeSpec(
                ShapeKind.Rectangle, color, width, StrokeKind.Solid, StrokeRendering.Architect,
                Colors.Transparent, FillKind.None,
                Math.Clamp(Math.Min(body.Width, body.Height) * 0.18, 4, 16), ShapeInk.NextSeed()))
        {
        }

        public SpeechBubble(Rect body, ShapeSpec spec)
        {
            Body = body;
            Spec = spec;
        }

        public Rect Body { get; }
        public ShapeSpec Spec { get; }
        public Color Color => Spec.Stroke;
        public double Width => Spec.Width;

        /// <summary>气泡圆角（形状族同一套参数）。</summary>
        public double Radius => Spec.Radius;

        /// <summary>思考气泡：尾巴是圆点、身体是云朵。</summary>
        public bool Thought => Spec.Kind == ShapeKind.ThoughtBubble;

        /// <summary>尾巴根部离左边缘的距离。</summary>
        public double TailInset => Math.Clamp(Body.Width * 0.12, 10, 28);

        public double TailWidth => Math.Clamp(Body.Width * 0.20, 14, 46);

        public double TailHeight => Math.Clamp(TailWidth * 0.9, 12, 42);

        /// <summary>尾巴尖（左下角朝外）。</summary>
        public Point Tip => new(Body.Left + TailInset + TailWidth * 0.35, Body.Bottom + TailHeight);

        /// <summary>身体轮廓（不含尾巴 / 圆点）。</summary>
        public IReadOnlyList<InkSegment> BodyOutline => ShapeInk.Contour(Body, Spec.Kind, Spec.Radius);

        /// <summary>尾巴根部两点；尖落在身体里时没有尾巴。</summary>
        public (Point Left, Point Right)? Bases =>
            Thought ? null : ShapeInk.TailBases(BodyOutline, Body, Tip, TailWidth);

        /// <summary>尾巴三角形的三个点（根两点 + 尖一点）。</summary>
        public Point[] Tail
        {
            get
            {
                if (Bases is { } bases) return new[] { bases.Left, bases.Right, Tip };
                return new[]
                {
                    new Point(Body.Left + TailInset, Body.Bottom - Width / 2),
                    new Point(Body.Left + TailInset + TailWidth, Body.Bottom - Width / 2),
                    Tip,
                };
            }
        }

        /// <summary>思考气泡的圆点尾巴（其余形状为空）。</summary>
        public IReadOnlyList<(Point Center, double Radius)> Trail =>
            Thought ? ShapeInk.ThoughtTrail(BodyOutline, Body, Tip, TailWidth, Width) : Array.Empty<(Point, double)>();

        /// <summary>可画的几块轮廓：身体（含尾巴）与圆点。</summary>
        public IReadOnlyList<InkPart> Parts
        {
            get
            {
                var parts = new List<InkPart>(1 + Trail.Count);
                var body = BodyOutline;
                if (Bases is { } bases) body = ShapeInk.WithTail(body, bases.Left, bases.Right, Tip);
                parts.Add(new InkPart(body, true));
                foreach (var (center, radius) in Trail)
                    parts.Add(new InkPart(ShapeInk.Circle(center, radius), true));
                return parts;
            }
        }

        public override Rect Frame
        {
            get
            {
                var box = Body;
                box.Union(Tip);
                foreach (var (center, radius) in Trail)
                {
                    box.Union(new Point(center.X - radius, center.Y - radius));
                    box.Union(new Point(center.X + radius, center.Y + radius));
                }
                return box;
            }
        }

        public override Rect Bounds =>
            MarkGeometry.Inflate(Frame, Width / 2 + ShapeInk.Outset(Spec.Rendering));

        public override Mark MoveBy(Vector delta) => new SpeechBubble(
            new Rect(Body.X + delta.X, Body.Y + delta.Y, Body.Width, Body.Height), Spec);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            var a = MarkGeometry.MapPoint(Body.TopLeft, from, to);
            var b = MarkGeometry.MapPoint(Body.BottomRight, from, to);
            return new SpeechBubble(MarkGeometry.RectOf(a, b),
                Spec with { Width = Math.Max(0.5, Width * MarkGeometry.ScaleOf(from, to)) });
        }

        public override bool HitTest(Point point, double tolerance) =>
            ShapeInk.HitTest(Parts, Spec, point, tolerance, interiorAlways: true);
    }

    /// <summary>
    /// 放大镜：镜头框把「源区域」按倍率放大画出来；源区域与镜头**各自独立定位**
    /// （上游 <c>MagnifierData</c> 的 <c>source_center</c> / <c>magnified_center</c>）。
    /// </summary>
    public sealed class Magnifier : Mark
    {
        /// <summary>拖出镜头框造一个：源区域默认与镜头同心，取镜头 / 倍率那么大一块。</summary>
        public Magnifier(Rect lens, MagnifierSpec spec)
            : this(lens.Location + new Vector(lens.Width / 2, lens.Height / 2), lens.Width, lens.Height,
                lens.Location + new Vector(lens.Width / 2, lens.Height / 2), spec, fromLens: true)
        {
        }

        private Magnifier(Point lensCenter, double sourceWidth, double sourceHeight, Point sourceCenter,
            MagnifierSpec spec, bool fromLens)
        {
            Spec = (spec ?? MagnifierSpec.Default).Clamped();
            LensCenter = lensCenter;
            SourceCenter = sourceCenter;
            if (fromLens)
            {
                SourceWidth = Math.Max(1, sourceWidth / Spec.Factor);
                SourceHeight = Math.Max(1, sourceHeight / Spec.Factor);
            }
            else
            {
                SourceWidth = Math.Max(1, sourceWidth);
                SourceHeight = Math.Max(1, sourceHeight);
            }
        }

        /// <summary>镜头中心 / 源区域中心各自独立（可以拖开，形成连接桥）。</summary>
        public Point LensCenter { get; }
        public Point SourceCenter { get; }

        public double SourceWidth { get; }
        public double SourceHeight { get; }

        public MagnifierSpec Spec { get; }

        public double Factor => Spec.Factor;
        public Color Color => Spec.Stroke;
        public double Width => Spec.StrokeWidth;

        /// <summary>放大后的镜头矩形（源尺寸 × 倍率）。</summary>
        public Rect Lens => MagnifierInk.LensRect(LensCenter, SourceWidth, SourceHeight, Factor);

        /// <summary>取图区域（源尺寸、源中心）。</summary>
        public Rect SourceRect => MagnifierInk.SourceRectOf(SourceCenter, SourceWidth, SourceHeight);

        /// <summary>换源中心（拖手柄用；镜头不动）。</summary>
        public Mark WithSourceCenter(Point center) =>
            new Magnifier(LensCenter, SourceWidth, SourceHeight, center, Spec, fromLens: false);

        /// <summary>换镜头中心（拖镜头 / 拖移动手柄用；源不动，与上游拖动镜头一致）。</summary>
        public Mark WithLensCenter(Point center) =>
            new Magnifier(center, SourceWidth, SourceHeight, SourceCenter, Spec, fromLens: false);

        /// <summary>两个中心一起平移（拖源区域 / 键盘微调 / 整块挪动，上游 transform 会同时映射两个中心）。</summary>
        public Mark WithCenters(Point lensCenter, Point sourceCenter) =>
            new Magnifier(lensCenter, SourceWidth, SourceHeight, sourceCenter, Spec, fromLens: false);

        public override Rect Bounds
        {
            get
            {
                var box = Rect.Union(Lens, SourceRect);
                box.Inflate(Width / 2 + 2, Width / 2 + 2);
                return box;
            }
        }

        public override Rect Frame => Lens;

        /// <summary>
        /// 整个标注平移：源与镜头**一起走**（上游 transform 同时映射 <c>source_center</c> 与
        /// <c>magnified_center</c>）。只想动镜头请用 <see cref="WithLensCenter"/>。
        /// </summary>
        public override Mark MoveBy(Vector delta) =>
            WithCenters(LensCenter + delta, SourceCenter + delta);

        public override Mark ScaleTo(Rect from, Rect to)
        {
            var lensCenter = MarkGeometry.MapPoint(LensCenter, from, to);
            var sourceCenter = MarkGeometry.MapPoint(SourceCenter, from, to);
            double scale = MarkGeometry.ScaleOf(from, to);
            return new Magnifier(lensCenter, SourceWidth * scale, SourceHeight * scale, sourceCenter,
                Spec, fromLens: false);
        }

        public override bool HitTest(Point point, double tolerance)
        {
            var lens = Lens;
            lens.Inflate(tolerance, tolerance);
            if (lens.Contains(point)) return true;

            var source = SourceRect;
            source.Inflate(tolerance, tolerance);
            return source.Contains(point);
        }
    }

    /// <summary>
    /// 水印：整张图上按角度平铺的文字（颜色 / 字号 / 字体 / 角度 / 间距 / 不透明度）。
    /// 它是**覆盖整张图**的效果，不参与选中 / 拖动（<see cref="Bounds"/> 为空、<see cref="HitTest"/> 恒 false）。
    /// </summary>
    public sealed class Watermark : Mark
    {
        public Watermark(WatermarkSpec spec) => Spec = (spec ?? WatermarkSpec.Default).Clamped();

        public WatermarkSpec Spec { get; }

        public string Content => Spec.Text;
        public Color Color => Spec.Color;
        public double FontSize => Spec.FontSize;
        public string FontFamily => Spec.FontFamily;

        /// <summary>倾斜角度（度，逆时针为正）。</summary>
        public double Rotation => Spec.Angle;

        /// <summary>相邻两块水印的间距（配置值；实际格距见 <see cref="WatermarkSpec.Step"/>）。</summary>
        public double Gap => Spec.Gap;

        public double Opacity => Spec.Opacity;

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
