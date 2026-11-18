using System;
using System.Windows;

namespace LTools.Features.CapturePro.ImageEdit;

/// <summary>裁剪方向：<see cref="Rows"/> = 剪掉一条横向条带（行），<see cref="Columns"/> = 剪掉一条纵向条带（列）。</summary>
internal enum CropAxis
{
    Rows,
    Columns,
}

/// <summary>一条要剪掉的条带：从第 <see cref="Start"/> 行 / 列起，宽（长）<see cref="Length"/>。</summary>
internal readonly record struct CropSpan(int Start, int Length)
{
    public int End => Start + Length;

    /// <summary>能剪：长度 &gt; 0、落在图里、且不是整张图（剪掉全部没有意义）。</summary>
    public bool IsValid(int extent) =>
        extent > 0 && Length > 0 && Start >= 0 && Start + Length <= extent && Length < extent;
}

/// <summary>
/// 图片编辑窗口的纯几何（缩放 / 裁剪条带）。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageEditingCanvas::fit</c> / <c>cropStrip</c> 与 <c>ImageEditingDocument::prepareCrop</c>：
/// 适应窗口留 48 DIP 边距，滚轮每格 1.15 倍，条带按像素取整、夹在 [0, 边长] 里。
/// 不碰窗口，<c>--selftest</c> 直接断言。
/// </remarks>
internal static class ImageEditGeometry
{
    /// <summary>「适应窗口」时四周留出的边距（上游 <c>fit()</c> 的 48）。</summary>
    public const double FitMargin = 48;

    /// <summary>缩放下限 / 上限（上游只夹到 1e-9…1e6，这里收成能用的范围）。</summary>
    public const double MinZoom = 0.02;
    public const double MaxZoom = 64;

    /// <summary>滚轮一格放大多少（上游 <c>std::pow(1.15, steps)</c>）。</summary>
    public const double WheelStep = 1.15;

    /// <summary>
    /// 把拖动起止点换算成条带：先夹到 <c>[0, extent]</c>，再取小的当起点、差值当长度。
    /// </summary>
    public static CropSpan Resolve(int first, int last, int extent)
    {
        if (extent <= 0) return default;

        int a = Math.Clamp(first, 0, extent);
        int b = Math.Clamp(last, 0, extent);
        return new CropSpan(Math.Min(a, b), Math.Abs(a - b));
    }

    /// <summary>
    /// 条带在内容矩形（DIP）里的位置：行 = 整宽一条横带，列 = 整高一条纵带。
    /// 长度为 0 时是一条零宽 / 零高的引导线（位置落在起点上）。
    /// </summary>
    public static Rect Strip(Rect content, CropAxis axis, CropSpan span, int extent)
    {
        if (extent <= 0 || content.Width <= 0 || content.Height <= 0) return Rect.Empty;

        if (axis == CropAxis.Rows)
        {
            double scale = content.Height / extent;
            return new Rect(content.X, content.Y + span.Start * scale, content.Width, span.Length * scale);
        }

        double unit = content.Width / extent;
        return new Rect(content.X + span.Start * unit, content.Y, span.Length * unit, content.Height);
    }

    public static double ClampZoom(double zoom)
    {
        if (double.IsNaN(zoom) || double.IsInfinity(zoom) || zoom <= 0) return 1;
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    /// <summary>
    /// 「适应窗口」的缩放：内容按视口减去四周留白后能装下多少就多少（小图会被放大，与上游一致）。
    /// </summary>
    public static double FitZoom(Size content, Size viewport)
    {
        if (content.Width <= 0 || content.Height <= 0) return 1;
        if (viewport.Width <= 0 || viewport.Height <= 0) return 1;

        double width = Math.Max(1, viewport.Width - FitMargin);
        double height = Math.Max(1, viewport.Height - FitMargin);
        return ClampZoom(Math.Min(width / content.Width, height / content.Height));
    }

    /// <summary>滚轮一格（<paramref name="steps"/> 可正可负，120 的整数倍为一格）。</summary>
    public static double Wheel(double zoom, int steps)
    {
        if (steps == 0) return ClampZoom(zoom);
        return ClampZoom(ClampZoom(zoom) * Math.Pow(WheelStep, steps));
    }
}
