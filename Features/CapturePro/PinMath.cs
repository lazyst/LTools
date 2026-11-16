using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图窗口的纯计算：缩放 / 不透明度步进、滚轮缩放时的锚点换算、初始落点。
/// </summary>
/// <remarks>
/// 放在这里而不是窗口里，<c>--selftest</c> 才能直接断言（不用开窗口）：
/// 滚轮缩放要「光标下的那个点不动」，这条最容易写成「窗口左上角不动」。
/// </remarks>
internal static class PinMath
{
    public const double ZoomMin = 0.1;
    public const double ZoomMax = 4.0;
    public const double ZoomStep = 0.1;

    public const double OpacityMin = 0.2;
    public const double OpacityMax = 1.0;
    public const double OpacityStep = 0.05;

    /// <summary>滚轮一格（120）走几档；按整数倍取，免得来回滚一次回不到原值。</summary>
    private static int Steps(int delta) => delta == 0 ? 0 : Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120.0));

    /// <summary>缩放一步（<paramref name="delta"/> 为滚轮增量，正 = 放大）。</summary>
    public static double NextZoom(double zoom, int delta)
    {
        int steps = Steps(delta);
        if (steps == 0) return ClampZoom(zoom);

        double next = Math.Round(zoom + (delta > 0 ? steps : -steps) * ZoomStep, 4);
        return ClampZoom(next);
    }

    /// <summary>不透明度一步（<paramref name="delta"/> 为滚轮增量，正 = 更不透明）。</summary>
    public static double NextOpacity(double opacity, int delta)
    {
        int steps = Steps(delta);
        if (steps == 0) return ClampOpacity(opacity);

        double next = Math.Round(opacity + (delta > 0 ? steps : -steps) * OpacityStep, 4);
        return ClampOpacity(next);
    }

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, ZoomMin, ZoomMax);

    public static double ClampOpacity(double opacity) => Math.Clamp(opacity, OpacityMin, OpacityMax);

    /// <summary>
    /// 缩放后窗口左上角该放哪：让窗口里 <paramref name="at"/> 那个点（DIP）缩放前后**落在同一处屏幕位置**。
    /// 没有它的话滚轮缩放会「窗口左上角不动」，光标下的内容跟着跑。
    /// </summary>
    public static Point AnchoredOrigin(Point origin, Size oldSize, Size newSize, Point at)
    {
        double rx = oldSize.Width > 0.5 ? Math.Clamp(at.X / oldSize.Width, 0, 1) : 0.5;
        double ry = oldSize.Height > 0.5 ? Math.Clamp(at.Y / oldSize.Height, 0, 1) : 0.5;
        return new Point(
            origin.X + at.X - rx * newSize.Width,
            origin.Y + at.Y - ry * newSize.Height);
    }

    /// <summary>
    /// 贴图刚出现时放在哪：正好盖住截图的那块区域（屏幕物理像素 → DIP）。
    /// 100% 缩放时用户看到的和刚才截下来的一模一样。
    /// </summary>
    public static Rect PlaceAt(Rect physicalArea, double scale)
    {
        double s = scale > 0 ? scale : 1;
        return new Rect(physicalArea.X / s, physicalArea.Y / s,
            Math.Max(1, physicalArea.Width / s), Math.Max(1, physicalArea.Height / s));
    }
}
