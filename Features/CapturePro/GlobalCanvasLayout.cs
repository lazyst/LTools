using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 全屏画布的取景与摆位（纯函数，<c>--selftest</c> 直接断言）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>globalcanvascontroller.cpp</c> 的 <c>Session</c>：画布是一块**无限大的透明表面**，
/// 相机 = 「视口中心落在画布上的哪个点」+ 缩放。视图矩阵把画布坐标换成窗口 DIP：
/// <c>scale(zoom)</c> 再平移到「中心点对上视口中心」。
/// </para>
/// <para>
/// 常量全部来自上游：缩放 <c>exp(amount * 0.001)</c> 夹 0.1–8；Shift + 滚轮一格 40（
/// 精确滚轮直接用像素增量）；工具条初始落点是工作区右下各留 15；穿透时工具条 0.32，
/// 指针停在工具条上恢复 1.0（50ms 轮询）。
/// </para>
/// </remarks>
internal static class GlobalCanvasLayout
{
    /// <summary>缩放上下限（上游 <c>std::clamp(..., 0.1, 8.0)</c>）。</summary>
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;

    /// <summary>滚轮缩放系数：<c>exp(amount * 0.001)</c>。</summary>
    public const double ZoomPerDelta = 0.001;

    /// <summary>Shift + 滚轮一格（120）走 40 DIP。</summary>
    public const double PanPerNotch = 40.0;

    /// <summary>工具条离工作区右 / 下边缘的距离（上游 <c>- 15</c>）。</summary>
    public const double ToolbarInset = 15;

    /// <summary>穿透模式下工具条的不透明度（上游 0.32）。</summary>
    public const double ClickThroughOpacity = 0.32;

    /// <summary>穿透模式下轮询指针是否停在工具条上的间隔（上游 50ms）。</summary>
    public const int OpacityPollMs = 50;

    /// <summary>
    /// 穿透期间轮询指针的间隔。比 <see cref="OpacityPollMs"/> 快一档：穿透靠它决定「这一拍要不要吃鼠标」，
    /// 慢一拍就会漏点（指针刚移到工具条上、点击已经落下，那一下会落到下面的程序里）。
    /// </summary>
    public const int ClickThroughPollMs = 16;

    /// <summary>滚轮之后的缩放（不含锚点修正）。</summary>
    public static double ZoomAfterWheel(double zoom, double amount)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) zoom = 1;
        if (!double.IsFinite(amount)) return zoom;
        return Math.Clamp(zoom * Math.Exp(amount * ZoomPerDelta), MinZoom, MaxZoom);
    }

    /// <summary>Shift + 滚轮一格要平移的距离（精确滚轮给的是像素增量，直接用）。</summary>
    public static double PanDistance(double amount, bool precise) =>
        precise ? amount : amount / 120.0 * PanPerNotch;

    /// <summary>画布 → 窗口 DIP 的视图矩阵（与上游 <c>canvasToViewTransform</c> 同构）。</summary>
    public static Matrix ViewMatrix(double centerX, double centerY, double zoom,
        double width, double height)
    {
        zoom = Math.Clamp(double.IsFinite(zoom) && zoom > 0 ? zoom : 1, MinZoom, MaxZoom);
        var matrix = Matrix.Identity;
        matrix.Scale(zoom, zoom);
        matrix.Translate(width / 2.0 - centerX * zoom, height / 2.0 - centerY * zoom);
        return matrix;
    }

    /// <summary>窗口 DIP → 画布坐标（视图矩阵取逆；退化时返回原点）。</summary>
    public static Point ToCanvas(Matrix view, Point p)
    {
        if (!view.HasInverse) return p;
        var inverse = view;
        inverse.Invert();
        return inverse.Transform(p);
    }

    /// <summary>视口矩形换回画布坐标（水印 / 聚光拿它当「画布范围」）。</summary>
    public static Rect ViewRect(Matrix view, double width, double height)
    {
        var topLeft = ToCanvas(view, new Point(0, 0));
        var bottomRight = ToCanvas(view, new Point(width, height));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// 以光标为锚点缩放之后，新的视口中心（画布坐标）。
    /// 上游：<c>anchor + (viewportCenter - pointer) / newZoom</c>。
    /// </summary>
    public static Point ZoomedCenter(Matrix view, Point pointer, double newZoom,
        double width, double height)
    {
        newZoom = Math.Clamp(double.IsFinite(newZoom) && newZoom > 0 ? newZoom : 1, MinZoom, MaxZoom);
        var anchor = ToCanvas(view, pointer);
        return new Point(
            anchor.X + (width / 2.0 - pointer.X) / newZoom,
            anchor.Y + (height / 2.0 - pointer.Y) / newZoom);
    }

    /// <summary>视口里拖动 <paramref name="delta"/>（DIP）之后的新中心：内容跟着手指走。</summary>
    public static Point PannedCenter(double centerX, double centerY, Vector delta, double zoom)
    {
        zoom = Math.Clamp(double.IsFinite(zoom) && zoom > 0 ? zoom : 1, MinZoom, MaxZoom);
        return new Point(centerX - delta.X / zoom, centerY - delta.Y / zoom);
    }

    /// <summary>工具条初始落点：工作区右下各留 15（左上角坐标）。</summary>
    public static Point ToolbarOrigin(double workspaceWidth, double workspaceHeight,
        double toolbarWidth, double toolbarHeight)
    {
        double left = workspaceWidth - Math.Max(0, toolbarWidth) - ToolbarInset;
        double top = workspaceHeight - Math.Max(0, toolbarHeight) - ToolbarInset;
        return new Point(Math.Max(0, left), Math.Max(0, top));
    }

    /// <summary>穿透时工具条的不透明度：指针停在工具条上（或没穿透）给 1.0。</summary>
    public static double ToolbarOpacity(bool clickThrough, bool pointerOnToolbar) =>
        !clickThrough || pointerOnToolbar ? 1.0 : ClickThroughOpacity;

    /// <summary>
    /// 穿透期间窗口要不要「鼠标全透过去」（<c>WS_EX_TRANSPARENT</c>）。工具条与画布同窗，
    /// 所以指针停在工具条上时必须把鼠标要回来——否则工具条自己也点不到。
    /// </summary>
    public static bool MouseTransparent(bool clickThrough, bool pointerOnToolbar) =>
        clickThrough && !pointerOnToolbar;
}
