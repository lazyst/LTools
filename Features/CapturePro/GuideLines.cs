using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图参考线的几何与自适应配色（照上游 <c>presentation/screenshotguidelinerendering.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 只有两件事：把「光标 / 选区中心 / 显示器中心」换算成一条竖直 + 一条水平的十字线，
/// 以及「配置色跟取样处背景太像时反色」。绘制在 <see cref="CaptureCanvas"/>，这里全是纯函数，
/// <c>--selftest</c> 直接断言。
/// </para>
/// <para>
/// 坐标口径与上游一致：先按**物理像素**吸附到像素中心（<c>floor(x) + 0.5</c>），再换回 DIP，
/// 所以 1 物理像素的线在 125% / 150% 缩放下也不会糊成两像素。
/// </para>
/// </remarks>
internal static class GuideLines
{
    /// <summary>虚线段长 / 间隔（物理像素，照上游 <c>kDashLength</c> / <c>kDashGap</c>）。</summary>
    public const double DashLength = 10.0;

    public const double DashGap = 3.0;

    /// <summary>反色判定的相似半径（欧氏距离平方上限，照上游 <c>64 * 64</c>）。</summary>
    public const double SimilarityRadiusSquared = 64.0 * 64.0;

    /// <summary>一条十字线（竖直 + 水平），坐标是窗口 DIP。</summary>
    public readonly record struct Crosshair(Point VerticalStart, Point VerticalEnd,
        Point HorizontalStart, Point HorizontalEnd)
    {
        public bool HasVertical => VerticalStart.Y != VerticalEnd.Y;

        public bool HasHorizontal => HorizontalStart.X != HorizontalEnd.X;

        public bool IsEmpty => VerticalStart.Equals(VerticalEnd) && HorizontalStart.Equals(HorizontalEnd);
    }

    /// <summary>取色器取样点的四段参考线（中间给取样像素留空，照上游 <c>paintScreenshotColorPickerCenterGuideLines</c>）。</summary>
    public readonly record struct Reticle(Point TopStart, Point TopEnd, Point BottomStart, Point BottomEnd,
        Point LeftStart, Point LeftEnd, Point RightStart, Point RightEnd, Rect Marker);

    /// <summary>把一个 DIP 坐标吸附到它所在物理像素的中心（换算回 DIP）。</summary>
    public static double PixelCenter(double coordinate, double scale) =>
        scale > 0 ? (Math.Floor(coordinate * scale) + 0.5) / scale : Math.Floor(coordinate) + 0.5;

    /// <summary>十字线几何：bounds 的四条边 + center 吸附后的 x / y（照上游 <c>crosshairGeometry</c>）。</summary>
    public static Crosshair Cross(Rect bounds, Point center, double scale)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return default;

        double left = PixelCenter(bounds.Left, scale);
        double top = PixelCenter(bounds.Top, scale);
        double right = Right(bounds.Right, scale);
        double bottom = Right(bounds.Bottom, scale);
        double x = PixelCenter(center.X, scale);
        double y = PixelCenter(center.Y, scale);

        return new Crosshair(new Point(x, top), new Point(x, bottom),
            new Point(left, y), new Point(right, y));
    }

    /// <summary>右 / 下边：<c>ceil(v) - 0.5</c>（照上游），保证落在外沿像素中心。</summary>
    public static double Right(double coordinate, double scale) =>
        scale > 0 ? (Math.Ceiling(coordinate * scale) - 0.5) / scale : Math.Ceiling(coordinate) - 0.5;

    /// <summary>
    /// 取样点周围留空的那一圈参考线：四条线从各自边走到取样像素边缘，
    /// 取样像素本身留一个边长 1 物理像素的小方块（<see cref="Reticle.Marker"/>）。
    /// </summary>
    public static Reticle PickerReticle(Rect bounds, Point center, double scale)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return default;

        double left = PixelCenter(bounds.Left, scale);
        double top = PixelCenter(bounds.Top, scale);
        double right = Right(bounds.Right, scale);
        double bottom = Right(bounds.Bottom, scale);
        double x = PixelCenter(center.X, scale);
        double y = PixelCenter(center.Y, scale);

        double half = scale > 0 ? 0.5 / scale : 0.5;
        double centerTop = y - half;
        double centerBottom = y + half;
        double centerLeft = x - half;
        double centerRight = x + half;

        var marker = new Rect(x - half, y - half, half * 2, half * 2);

        return new Reticle(
            new Point(x, top), new Point(x, top <= centerTop ? centerTop : top),
            new Point(x, centerBottom <= bottom ? centerBottom : bottom), new Point(x, bottom),
            new Point(left, y), new Point(left <= centerLeft ? centerLeft : left, y),
            new Point(centerRight <= right ? centerRight : right, y), new Point(right, y),
            marker);
    }

    /// <summary>
    /// 参考线颜色跟取样处背景太像就反色（照上游 <c>adaptiveScreenshotGuideColor</c>）：
    /// 透明 / 坏色 / 没取到样一律原样返回。
    /// </summary>
    public static Color Adaptive(Color configured, Color? sample)
    {
        if (configured.A == 0 || sample is not { } sampled) return configured;

        double red = configured.R - sampled.R;
        double green = configured.G - sampled.G;
        double blue = configured.B - sampled.B;
        if (red * red + green * green + blue * blue > SimilarityRadiusSquared) return configured;

        return Color.FromArgb(configured.A, (byte)(255 - configured.R),
            (byte)(255 - configured.G), (byte)(255 - configured.B));
    }

    /// <summary>取样像素方块边框的颜色（照上游 <c>readableTextColor</c>）。</summary>
    public static Color Readable(Color color) =>
        color.R > 128 || color.G > 128 || color.B > 128
            ? Color.FromArgb(224, 0, 0, 0)
            : Color.FromArgb(217, 255, 255, 255);
}
