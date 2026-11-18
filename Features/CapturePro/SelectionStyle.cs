using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>选区外观的影响方式：柔和阴影，或者向外扩的一圈实色描边（照上游 <c>ScreenshotSelectionBorderType</c>）。</summary>
internal enum SelectionBorderType
{
    /// <summary>阴影像素宽（<c>shadowWidth</c>）：选区外侧一圈柔和投影。</summary>
    Shadow = 0,

    /// <summary>边框宽（<c>borderWidth</c>）：选区外侧一圈实色描边。</summary>
    Border = 1,
}

/// <summary>
/// 选区外观：圆角半径 + 阴影 / 边框（宽 + 色）+ 类型。
/// </summary>
/// <remarks>
/// <para>
/// 长度一律是**窗口 DIP**（与选区的 DIP 几何同一个坐标系），导出时乘
/// <see cref="CaptureDocument.Scale"/> 换成物理像素——与 X / Y / W / H 显示物理像素不同，
/// 工具栏显示圆角 / 效果宽时会乘 <c>Scale</c> 再取整（见 <c>SelectionBarModel</c>）。
/// </para>
/// <para>
/// 上游同时保留 <c>shadowWidth</c> 与 <c>borderWidth</c>：切类型不会把另一种的数值抹掉，
/// 所以这里两份都存，<see cref="EffectWidth"/> 按类型取当前生效的那一份。
/// </para>
/// </remarks>
internal sealed record SelectionStyle
{
    /// <summary>圆角上限（上游 <c>kScreenshotSelectionCornerRadiusMax</c>）。</summary>
    public const int CornerRadiusMax = 256;

    /// <summary>阴影 / 边框宽度上限（上游 <c>kScreenshotSelectionShadowWidthMax</c>）。</summary>
    public const int EffectWidthMax = 64;

    /// <summary>选区最小边长（上游 <c>kScreenshotSelectionMinimumSize</c>）。</summary>
    public const double MinimumSize = 1.0;

    /// <summary>阴影默认色（上游 <c>#333333</c>）。</summary>
    public static readonly Color DefaultShadowColor = Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>边框默认色（上游白色）。</summary>
    public static readonly Color DefaultBorderColor = Colors.White;

    /// <summary>默认外观 = 直角、无阴影、无边框（与 P3 之前完全一致）。</summary>
    public static readonly SelectionStyle None = new();

    public double CornerRadius { get; init; }

    public double ShadowWidth { get; init; }

    public Color ShadowColor { get; init; } = DefaultShadowColor;

    public double BorderWidth { get; init; }

    public Color BorderColor { get; init; } = DefaultBorderColor;

    public SelectionBorderType BorderType { get; init; }

    /// <summary>当前生效的影响宽度（类型决定取阴影还是边框那一个）。</summary>
    public double EffectWidth => BorderType == SelectionBorderType.Border ? BorderWidth : ShadowWidth;

    /// <summary>当前生效的影响颜色。</summary>
    public Color EffectColor => BorderType == SelectionBorderType.Border ? BorderColor : ShadowColor;

    /// <summary>直角 + 没有效果 = 走「不合成」的快路径（导出尺寸不变）。</summary>
    public bool IsPlain => CornerRadius == 0 && EffectWidth == 0;

    /// <summary>夹到上游的上限；负数按 0。</summary>
    public SelectionStyle Clamp() => this with
    {
        CornerRadius = Math.Clamp(CornerRadius, 0, CornerRadiusMax),
        ShadowWidth = Math.Clamp(ShadowWidth, 0, EffectWidthMax),
        BorderWidth = Math.Clamp(BorderWidth, 0, EffectWidthMax),
    };

    /// <summary>换当前生效的影响宽度（不动另一种）。</summary>
    public SelectionStyle WithEffectWidth(double width)
    {
        double value = Math.Clamp(width, 0, EffectWidthMax);
        return BorderType == SelectionBorderType.Border
            ? this with { BorderWidth = value }
            : this with { ShadowWidth = value };
    }

    /// <summary>换当前生效的影响颜色（不动另一种）。</summary>
    public SelectionStyle WithEffectColor(Color color) =>
        BorderType == SelectionBorderType.Border
            ? this with { BorderColor = color }
            : this with { ShadowColor = color };

    /// <summary>切类型（两种宽度 / 颜色各自保留）。</summary>
    public SelectionStyle WithBorderType(SelectionBorderType type) => this with { BorderType = type };

    /// <summary>圆角在给定选区里的实际半径：不超过短边的一半（上游 <c>std::clamp</c> 同口径）。</summary>
    public double EffectiveRadius(Rect bounds) => Math.Clamp(
        CornerRadius, 0, Math.Max(0, Math.Min(bounds.Width, bounds.Height) / 2));

    /// <summary>物理像素下的效果宽度（导出 / 工具栏显示都用它）。</summary>
    public int EffectPixels(double scale) =>
        Math.Max(0, (int)Math.Round(EffectWidth * (scale > 0 ? scale : 1)));

    /// <summary>物理像素下的圆角半径。</summary>
    public int RadiusPixels(double scale) =>
        Math.Max(0, (int)Math.Round(CornerRadius * (scale > 0 ? scale : 1)));
}

/// <summary>
/// 比例预设（照上游 <c>ScreenshotSelectionAspectRatioPreset</c>）。
/// **几何里存的是「高 / 宽」**，与上游 <c>screenshotSelectionAspectRatioHeightOverWidth</c> 一致；
/// 界面上显示的是常规的「宽:高」写法。
/// </summary>
internal enum SelectionAspect
{
    Free = 0,
    Square,
    Landscape3x2,
    Landscape4x3,
    Landscape16x9,
    Portrait2x3,
    Portrait3x4,
    Portrait9x16,
}

/// <summary>
/// 比例预设表：Id（写配置）、显示名、高 / 宽、宽 / 高。
/// 顺序就是工具栏下拉的顺序（上次选区 / 自定义预设在前，这 8 条在后）。
/// </summary>
internal static class SelectionAspects
{
    /// <summary>下拉里的固定顺序（照上游 <c>kAspectRatioPresets</c>）。</summary>
    public static readonly SelectionAspect[] All =
    {
        SelectionAspect.Free,
        SelectionAspect.Square,
        SelectionAspect.Landscape3x2,
        SelectionAspect.Landscape4x3,
        SelectionAspect.Landscape16x9,
        SelectionAspect.Portrait2x3,
        SelectionAspect.Portrait3x4,
        SelectionAspect.Portrait9x16,
    };

    /// <summary>写配置用的 Id（与上游 <c>screenshotSelectionAspectRatioPresetId</c> 同一套字符串）。</summary>
    public static string Id(SelectionAspect aspect) => aspect switch
    {
        SelectionAspect.Square => "1:1",
        SelectionAspect.Landscape3x2 => "3:2",
        SelectionAspect.Landscape4x3 => "4:3",
        SelectionAspect.Landscape16x9 => "16:9",
        SelectionAspect.Portrait2x3 => "2:3",
        SelectionAspect.Portrait3x4 => "3:4",
        SelectionAspect.Portrait9x16 => "9:16",
        _ => "free",
    };

    /// <summary>从配置里的 Id 读回来；认不出 = 自由。</summary>
    public static SelectionAspect FromId(string? id) => id switch
    {
        "1:1" => SelectionAspect.Square,
        "3:2" => SelectionAspect.Landscape3x2,
        "4:3" => SelectionAspect.Landscape4x3,
        "16:9" => SelectionAspect.Landscape16x9,
        "2:3" => SelectionAspect.Portrait2x3,
        "3:4" => SelectionAspect.Portrait3x4,
        "9:16" => SelectionAspect.Portrait9x16,
        _ => SelectionAspect.Free,
    };

    /// <summary>下拉里显示的文字。</summary>
    public static string Label(SelectionAspect aspect) => aspect == SelectionAspect.Free ? "自由" : Id(aspect);

    /// <summary>高 / 宽（拖拽几何用的那个方向）；自由 = 0。</summary>
    public static double HeightOverWidth(SelectionAspect aspect) => aspect switch
    {
        SelectionAspect.Square => 1.0,
        SelectionAspect.Landscape3x2 => 2.0 / 3.0,
        SelectionAspect.Landscape4x3 => 3.0 / 4.0,
        SelectionAspect.Landscape16x9 => 9.0 / 16.0,
        SelectionAspect.Portrait2x3 => 3.0 / 2.0,
        SelectionAspect.Portrait3x4 => 4.0 / 3.0,
        SelectionAspect.Portrait9x16 => 16.0 / 9.0,
        _ => 0.0,
    };

    /// <summary>宽 / 高（比较与显示用）；自由 = 0。</summary>
    public static double WidthOverHeight(SelectionAspect aspect)
    {
        double ratio = HeightOverWidth(aspect);
        return ratio > 0 ? 1.0 / ratio : 0.0;
    }

    /// <summary>
    /// 与给定矩形最接近的比例预设（照上游 <c>screenshotSelectionClosestAspectRatioPreset</c>）：
    /// 正常态锁定一个没配预设的自定义比例时，下拉里显示最接近的那一项。
    /// </summary>
    public static SelectionAspect Closest(Rect selection)
    {
        if (selection.Width <= 0 || selection.Height <= 0) return SelectionAspect.Free;

        double ratio = selection.Width / selection.Height;
        var closest = SelectionAspect.Free;
        double distance = double.PositiveInfinity;
        foreach (var aspect in All)
        {
            double candidate = WidthOverHeight(aspect);
            if (candidate <= 0) continue;
            double d = Math.Abs(ratio - candidate);
            if (d < distance)
            {
                closest = aspect;
                distance = d;
            }
        }
        return closest;
    }
}

/// <summary>
/// 比例锁定的纯几何（照上游 <c>screenshotselectiongeometry.cpp</c> 的三个函数）：
/// 拖拽改框、框选拉框、把已有选区套成某个比例。全部是纯函数，自检直接断言。
/// </summary>
internal static class AspectLock
{
    /// <summary>拖拽方向：0 = 这条边不动，-1 = 往左 / 上，+1 = 往右 / 下。</summary>
    public static (int Horizontal, int Vertical) Directions(FrameHandle handle) => handle switch
    {
        FrameHandle.Left or FrameHandle.TopLeft or FrameHandle.BottomLeft => (-1, handle == FrameHandle.Left ? 0 : handle == FrameHandle.TopLeft ? -1 : 1),
        FrameHandle.Right or FrameHandle.TopRight or FrameHandle.BottomRight => (1, handle == FrameHandle.Right ? 0 : handle == FrameHandle.TopRight ? -1 : 1),
        FrameHandle.Top => (0, -1),
        FrameHandle.Bottom => (0, 1),
        _ => (0, 0),
    };

    /// <summary>
    /// 按比例锁定拖拽八个把手（上游 <c>aspectRatioLockedSelectionRect</c>）：
    /// 由「变化更大的那一轴」驱动，另一轴按比例推出来；锚点是被拖边相对的那条边 / 角。
    /// 放不进 <paramref name="bounds"/> 时二分收缩，所以不会越界。
    /// </summary>
    public static Rect Drag(Rect origin, FrameHandle handle, Vector delta, Rect bounds,
        double minimumSize, double ratio)
    {
        var (horizontal, vertical) = Directions(handle);
        if ((horizontal == 0 && vertical == 0) || origin.Width <= 0 || origin.Height <= 0 || ratio <= 0)
            return origin;

        double horizontalSpan = horizontal == 0 ? origin.Width : origin.Width + horizontal * delta.X;
        double verticalSpan = vertical == 0 ? origin.Height : origin.Height + vertical * delta.Y;
        int resizedHorizontal = horizontalSpan < 0 ? -horizontal : horizontal;
        int resizedVertical = verticalSpan < 0 ? -vertical : vertical;

        bool resizeFromWidth = horizontal != 0 &&
            (vertical == 0 ||
             Math.Abs(horizontalSpan / origin.Width - 1.0) >= Math.Abs(verticalSpan / origin.Height - 1.0));

        double minimumSpan = resizeFromWidth
            ? Math.Max(minimumSize, minimumSize / ratio)
            : Math.Max(minimumSize, minimumSize * ratio);
        double span = Math.Max(resizeFromWidth ? Math.Abs(horizontalSpan) : Math.Abs(verticalSpan),
            minimumSpan);

        Rect RectForSpan(double nextSpan)
        {
            double width = resizeFromWidth ? nextSpan : nextSpan / ratio;
            double height = resizeFromWidth ? nextSpan * ratio : nextSpan;
            var center = new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2);
            double left = center.X - width / 2;
            double right = left + width;
            double top = center.Y - height / 2;
            double bottom = top + height;

            if (horizontal != 0)
            {
                double anchor = horizontal < 0 ? origin.Right : origin.Left;
                if (resizedHorizontal < 0)
                {
                    right = anchor;
                    left = right - width;
                }
                else
                {
                    left = anchor;
                    right = left + width;
                }
            }

            if (vertical != 0)
            {
                double anchor = vertical < 0 ? origin.Bottom : origin.Top;
                if (resizedVertical < 0)
                {
                    bottom = anchor;
                    top = bottom - height;
                }
                else
                {
                    top = anchor;
                    bottom = top + height;
                }
            }

            return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        if (bounds.IsEmpty) return RectForSpan(span);

        bool Fits(Rect r) =>
            r.Left >= bounds.Left - 1e-6 && r.Top >= bounds.Top - 1e-6 &&
            r.Right <= bounds.Right + 1e-6 && r.Bottom <= bounds.Bottom + 1e-6;

        if (Fits(RectForSpan(span))) return RectForSpan(span);

        double lower = minimumSpan;
        double upper = span;
        for (int i = 0; i < 40; i++)
        {
            double middle = (lower + upper) / 2;
            if (Fits(RectForSpan(middle))) lower = middle;
            else upper = middle;
        }
        return RectForSpan(lower);
    }

    /// <summary>
    /// 按比例锁定「框选」拖出来的矩形（上游 <c>aspectRatioLockedMarqueeRect</c>）：
    /// 以按下点为锚，把较短的一边撑到符合比例，再夹进 <paramref name="bounds"/>。
    /// </summary>
    public static Rect Marquee(Point origin, Point current, Rect bounds, double ratio)
    {
        Rect inclusive = RectOf(origin, current);
        if (ratio <= 0) return inclusive;

        int horizontal = current.X < origin.X ? -1 : 1;
        int vertical = current.Y < origin.Y ? -1 : 1;

        double width = Math.Max(inclusive.Width, inclusive.Height / ratio);
        double height = width * ratio;

        double anchorX = horizontal < 0 ? inclusive.Right : inclusive.Left;
        double anchorY = vertical < 0 ? inclusive.Bottom : inclusive.Top;

        if (!bounds.IsEmpty)
        {
            anchorX = Math.Clamp(anchorX, bounds.Left, bounds.Right);
            anchorY = Math.Clamp(anchorY, bounds.Top, bounds.Bottom);
            double availableWidth = horizontal < 0 ? anchorX - bounds.Left : bounds.Right - anchorX;
            double availableHeight = vertical < 0 ? anchorY - bounds.Top : bounds.Bottom - anchorY;
            double maximumWidth = Math.Max(0, Math.Min(availableWidth, availableHeight / ratio));
            width = Math.Min(width, maximumWidth);
            height = width * ratio;
        }

        double left = horizontal < 0 ? anchorX - width : anchorX;
        double top = vertical < 0 ? anchorY - height : anchorY;
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// 把已有选区套成某个比例（上游 <c>aspectRatioScreenshotSelectionRect</c>）：
    /// 保持左上角、宽度不动、高度按比例推；放不下时缩小宽度，最后夹回 <paramref name="bounds"/>。
    /// 返回 <see cref="Rect.Empty"/> = 这个比例在给定范围内做不到。
    /// </summary>
    public static Rect Apply(Rect selection, Rect bounds, double ratio, double minimumSize)
    {
        if (selection.Width <= 0 || selection.Height <= 0 || ratio <= 0) return Rect.Empty;

        double minimumWidth = Math.Max(minimumSize, minimumSize / ratio);
        double width = Math.Max(selection.Width, minimumWidth);
        double left = selection.Left;
        double top = selection.Top;

        if (!bounds.IsEmpty)
        {
            double maximumWidth = Math.Min(bounds.Width, bounds.Height / ratio);
            if (maximumWidth < minimumWidth) return Rect.Empty;
            width = Math.Min(width, maximumWidth);
            left = Math.Clamp(left, bounds.Left, bounds.Right - width);
            top = Math.Clamp(top, bounds.Top, bounds.Bottom - width * ratio);
        }

        return new Rect(left, top, width, width * ratio);
    }

    /// <summary>两点组成的矩形（不要求有序）。</summary>
    public static Rect RectOf(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>
/// 选区阴影的强度曲线（照上游 <c>screenshotshadowprofile.h</c>）：
/// 紧贴选区那一圈最浓（峰值 36%），往外按四次方衰减到 0。
/// </summary>
internal static class ShadowProfile
{
    /// <summary>峰值不透明度。</summary>
    public const double PeakAlphaScale = 0.36;

    /// <summary>距选区边 <paramref name="distance"/> 处、总宽 <paramref name="width"/> 的不透明度比例。</summary>
    public static double Falloff(double distance, double width)
    {
        if (width <= 0) return 0;
        double remaining = Math.Clamp(1.0 - distance / width, 0.0, 1.0);
        double squared = remaining * remaining;
        return squared * squared;
    }

    /// <summary>第 <paramref name="index"/> 圈（从选区边往外数，1 起）的不透明度。</summary>
    public static double RingAlpha(int index, int width) =>
        index <= 0 ? 0 : PeakAlphaScale * Falloff(index - 0.5, Math.Max(1, width));
}

/// <summary>
/// 选区效果的几何与绘制（预览与导出共用）：圆角路径、阴影环、外扩边框、输出尺寸。
/// </summary>
internal static class SelectionEffects
{
    /// <summary>把「选区的几何」按圆角处理：矩形变成圆角矩形，点列保持原样。</summary>
    public static Geometry Round(Geometry geometry, Rect bounds, double radius)
    {
        double r = Math.Clamp(radius, 0, Math.Max(0, Math.Min(bounds.Width, bounds.Height) / 2));
        if (r <= 0) return geometry;
        if (geometry is RectangleGeometry rect)
            return new RectangleGeometry(rect.Rect, r, r);
        return geometry;
    }

    /// <summary>选区（圆角后）向外扩 <paramref name="expand"/> 的形状。</summary>
    public static Geometry Inflate(Geometry geometry, double expand)
    {
        if (expand <= 0) return geometry;
        if (geometry is RectangleGeometry rect)
            return new RectangleGeometry(
                new Rect(rect.Rect.X - expand, rect.Rect.Y - expand,
                    rect.Rect.Width + expand * 2, rect.Rect.Height + expand * 2),
                Math.Max(0, rect.RadiusX + expand), Math.Max(0, rect.RadiusY + expand));

        // 非矩形：用「描边再减掉原形状」当外扩（圆角连接，和上游 renderBorder 同一手法）
        var stroked = geometry.GetWidenedPathGeometry(new Pen(Brushes.Black, expand * 2)
        {
            LineJoin = PenLineJoin.Round,
        });
        return new CombinedGeometry(GeometryCombineMode.Exclude, stroked, geometry);
    }

    /// <summary>
    /// 阴影环：在选区外侧逐圈铺一层半透明（<see cref="ShadowProfile"/>）。
    /// 返回的是「外扩到 expand 的形状 减去 选区的形状」，调用方按圈编号算透明度。
    /// </summary>
    public static Geometry ShadowRing(Geometry geometry, double radius, Rect bounds, double expand)
    {
        Geometry outer = Inflate(Round(geometry, bounds, radius), expand);
        Geometry inner = Inflate(Round(geometry, bounds, radius), Math.Max(0, expand - 1));
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        ring.Freeze();
        return ring;
    }

    /// <summary>
    /// 边框：把选区路径向外扩一圈（照上游
    /// <c>ScreenshotSelectionShadowRenderer::renderBorder</c>：2× 宽描边再减掉原路径，圆角连接）。
    /// 矩形走「圆角半径 + 宽度」的外扩矩形，非矩形退回描边再减。
    /// 返回 null = 不用画。
    /// </summary>
    public static Geometry? Border(Geometry path, double width)
    {
        if (width <= 0 || path.IsEmpty()) return null;

        Geometry outer = Inflate(path, width);
        var exterior = new CombinedGeometry(GeometryCombineMode.Exclude, outer, path);
        exterior.Freeze();
        return exterior;
    }

    /// <summary>
    /// 画阴影（DIP 坐标系）：从选区边往外每一圈一层，覆盖 <paramref name="widthDip"/> 那么宽。
    /// </summary>
    public static void DrawShadow(DrawingContext dc, Geometry geometry, Rect bounds, double radius,
        double widthDip, Color color)
    {
        if (widthDip <= 0) return;

        // 圈数就是效果宽度（DIP）；宽度是小数时按像素粒度铺整数圈，最后补一圈残余
        int rings = Math.Max(1, (int)Math.Round(widthDip));
        for (int i = rings; i >= 1; i--)
        {
            double alpha = ShadowProfile.RingAlpha(i, rings);
            byte a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
            if (a == 0) continue;
            var brush = new SolidColorBrush(Color.FromArgb(a, color.R, color.G, color.B));
            brush.Freeze();
            dc.DrawGeometry(brush, null, ShadowRing(geometry, radius, bounds, i));
        }
    }

    /// <summary>
    /// 画边框（DIP 坐标系）。
    /// </summary>
    public static void DrawBorder(DrawingContext dc, Geometry geometry, Rect bounds, double radius,
        double widthDip, Color color)
    {
        var exterior = Border(Round(geometry, bounds, radius), widthDip);
        if (exterior == null) return;

        var brush = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));
        brush.Freeze();
        dc.DrawGeometry(brush, null, exterior);
    }

    /// <summary>
    /// 导出布局：<paramref name="contentPixels"/> 是内容像素尺寸，效果宽按 <paramref name="scale"/>
    /// 换成物理像素；输出 = 内容 + 两侧效果宽（上游 <c>layoutForContent</c>）。
    /// </summary>
    public static (Rect Content, Rect Output, double EffectPixels) Layout(
        Size contentPixels, SelectionStyle style, double scale)
    {
        double effect = style.EffectPixels(scale);
        var content = new Rect(effect, effect, Math.Max(1, contentPixels.Width), Math.Max(1, contentPixels.Height));
        var output = new Rect(0, 0, content.Width + effect * 2, content.Height + effect * 2);
        return (content, output, effect);
    }
}
