using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>录屏区域拖拽时按下的是哪几条边（可以同时是角）。</summary>
[Flags]
internal enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>一次拖拽解出来的尺寸：最终落在哪条边 + 请求的宽高。</summary>
internal readonly record struct RegionDrag(ResizeEdges Effective, int Width, int Height);

/// <summary>
/// 「未开录时可编辑的录屏区域」的几何规则（照 Snow Shot 的
/// <c>screenrecordingareawindow.cpp</c> + <c>resizegeometry.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 全部是**屏幕物理像素**（与截图 / 找图 / 鼠标注入同一套坐标系），不碰窗口、不碰 DPI，
/// 所以 <c>--selftest</c> 能直接断言。
/// </para>
/// <para>
/// 三条照上游：
/// ① 边框画在选区**外面** 2 物理像素 + 1 像素透明缝（<see cref="FrameEdgePhysical"/> = 3）；
/// ② 边缘可抓的带子是 **6 逻辑像素**（<see cref="HitWidthLogical"/>）往里量，选区太小按 1/4 收窄；
/// ③ 拖过对边会「翻面」：贴着哪边就换哪边（<see cref="Drag"/> 的 <c>previous</c> 处理距离恰好为 0 的那一帧），
///    最小 <see cref="MinimumPhysical"/> 像素，不夹屏幕边界（上游允许拖出屏幕）。
/// </para>
/// </remarks>
internal static class RecordRegionGeometry
{
    /// <summary>边框宽度（物理像素，上游 <c>kPhysicalBorderWidth</c>）。</summary>
    public const int BorderPhysical = 2;

    /// <summary>边框与选区之间的透明缝（物理像素，上游 <c>kPhysicalBorderPadding</c>）。</summary>
    public const int GapPhysical = 1;

    /// <summary>边框外面那一圈的总厚度：边框 + 缝。</summary>
    public const int FrameEdgePhysical = BorderPhysical + GapPhysical;

    /// <summary>可抓的边缘带（逻辑像素，上游 <c>kResizeHitWidth</c>）。</summary>
    public const double HitWidthLogical = 6;

    /// <summary>录制区域最小边长（物理像素，上游 <c>screenRecordingMinimumExtent</c>）。</summary>
    public const int MinimumPhysical = 10;

    /// <summary>右下角移动把手（逻辑像素，上游 <c>OverlayControlButton::controlSize</c>）。</summary>
    public const double HandleSideLogical = 32;

    /// <summary>把手离选区右下角的内缩（逻辑像素，上游 <c>kMoveButtonGap</c>）。</summary>
    public const double HandleInsetLogical = 8;

    /// <summary>把选区外面那一圈也算进来：窗口要覆盖的矩形（物理像素）。</summary>
    public static Int32Rect FrameBounds(Int32Rect area) =>
        new(area.X - FrameEdgePhysical, area.Y - FrameEdgePhysical,
            Math.Max(1, area.Width) + FrameEdgePhysical * 2,
            Math.Max(1, area.Height) + FrameEdgePhysical * 2);

    /// <summary>
    /// 光标（屏幕物理像素）落在选区的哪几条边上。
    /// </summary>
    /// <remarks>
    /// 和上游一样**比较的是相对选区的坐标**：窗口边框在选区外面那 3 像素仍然满足
    /// 「x ≤ left + 带宽」，所以点可见边框也能抓。
    /// </remarks>
    public static ResizeEdges EdgesAt(Int32Rect area, int x, int y, double scale)
    {
        if (area.Width <= 0 || area.Height <= 0) return ResizeEdges.None;

        double s = scale > 0 ? scale : 1;
        double horizontal = Math.Min(HitWidthLogical * s, area.Width / 4.0);
        double vertical = Math.Min(HitWidthLogical * s, area.Height / 4.0);

        var edges = ResizeEdges.None;
        if (x <= area.X + horizontal) edges |= ResizeEdges.Left;
        else if (x >= area.X + area.Width - horizontal) edges |= ResizeEdges.Right;

        if (y <= area.Y + vertical) edges |= ResizeEdges.Top;
        else if (y >= area.Y + area.Height - vertical) edges |= ResizeEdges.Bottom;

        return edges;
    }

    /// <summary>按下后移动了多少（物理像素）：解出请求的宽高与最终落在哪条边。</summary>
    public static RegionDrag Drag(Int32Rect origin, ResizeEdges pressed, int dx, int dy,
        ResizeEdges previous)
    {
        if (pressed == ResizeEdges.None) return new RegionDrag(ResizeEdges.None, origin.Width, origin.Height);

        int width = origin.Width;
        int height = origin.Height;
        var effective = pressed;

        void Axis(ResizeEdges low, ResizeEdges high, int extent, int movement, ref int size)
        {
            if ((pressed & low) == 0 && (pressed & high) == 0) return;

            // 左 / 上边按住时，距离 = 位移 - 原宽；右 / 下边按住时，距离 = 位移 + 原宽。
            int distance = (pressed & low) != 0 ? movement - extent : movement + extent;

            effective &= ~(low | high);
            effective |= distance < 0 ? low
                : distance > 0 ? high
                : (previous & low) != 0 ? low : high;

            size = Math.Abs(distance);
        }

        Axis(ResizeEdges.Left, ResizeEdges.Right, origin.Width, dx, ref width);
        Axis(ResizeEdges.Top, ResizeEdges.Bottom, origin.Height, dy, ref height);

        return new RegionDrag(effective, width, height);
    }

    /// <summary>按「按住的是哪边 + 现在仍落在哪边」拼出新矩形（对边保持不动）。</summary>
    public static Int32Rect Anchored(Int32Rect origin, ResizeEdges pressed, ResizeEdges effective,
        int width, int height)
    {
        int x = origin.X;
        int y = origin.Y;

        // 按住左边时，锚点是原来的右边；翻面之后按新边重新锚。
        if ((pressed & ResizeEdges.Left) != 0) x += origin.Width;
        if ((pressed & ResizeEdges.Top) != 0) y += origin.Height;
        if ((effective & ResizeEdges.Left) != 0) x -= width;
        if ((effective & ResizeEdges.Top) != 0) y -= height;

        return new Int32Rect(x, y, Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>一次完整的 resize：拖动量 → 新区域 + 该用哪种光标。</summary>
    public static (Int32Rect Rect, ResizeEdges Effective) Resize(Int32Rect origin,
        ResizeEdges pressed, int dx, int dy, ResizeEdges previous)
    {
        var drag = Drag(origin, pressed, dx, dy, previous);
        int width = Math.Max(MinimumPhysical, drag.Width);
        int height = Math.Max(MinimumPhysical, drag.Height);
        return (Anchored(origin, pressed, drag.Effective, width, height), drag.Effective);
    }

    /// <summary>移动整块区域（最小尺寸不变，不夹屏幕边界——上游允许拖出屏幕）。</summary>
    public static Int32Rect Move(Int32Rect origin, int dx, int dy) =>
        new(origin.X + dx, origin.Y + dy, origin.Width, origin.Height);

    /// <summary>窗口里可抓边缘带的厚度（DIP）：外面那圈 3 像素 + 往里 6 逻辑像素（太小按 1/4 收窄）。</summary>
    public static double HitBandDip(Int32Rect area, double scale, bool horizontal)
    {
        double s = scale > 0 ? scale : 1;
        int extent = horizontal ? area.Width : area.Height;
        double inner = Math.Min(HitWidthLogical, Math.Max(1, extent) / s / 4);
        return FrameEdgePhysical / s + inner;
    }

    /// <summary>右下角移动把手：边长与离选区右下角的内缩（DIP），区域太小时跟着缩。</summary>
    public static (double Side, double Inset) HandleMetrics(Int32Rect area, double scale)
    {
        double s = scale > 0 ? scale : 1;
        double width = Math.Max(1, area.Width) / s;
        double height = Math.Max(1, area.Height) / s;
        double box = Math.Min(width, height);
        double side = Math.Min(HandleSideLogical, box);
        double inset = Math.Min(HandleInsetLogical, Math.Max(0, (box - side) / 2));
        return (side, inset);
    }
}
