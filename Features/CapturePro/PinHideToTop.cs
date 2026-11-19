using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图「收到屏幕顶部」的几何与节奏（照 Snow Shot 的
/// <c>presentation/pinned/screenshotpinnedhidetotopcontroller.cpp</c> 里的 <c>geometry</c> 命名空间）。
/// </summary>
/// <remarks>
/// <para>
/// 收顶部之后贴图窗口藏起来，工作区顶边只留一条 <b>30×6 DIP</b> 的小手柄；鼠标靠近手柄就把
/// 窗口展开到「手柄正下方」，离开 <b>100ms</b> 再收起。手柄之间互相避让（<see cref="PlaceHandle"/>）。
/// </para>
/// <para>
/// 坐标一律**物理像素**（与截图 / 找图 / 鼠标注入同一套）：手柄尺寸按显示器 DPI 放大，
/// 落 WPF 窗口时再除回 DIP。上游 <c>QRect::right()</c> 是闭区间，这里换成
/// <see cref="Int32Rect"/> 的 <c>X + Width</c> 开区间口径。
/// </para>
/// </remarks>
internal static class PinHideToTop
{
    /// <summary>手柄可见部分的宽（DIP）。</summary>
    public const double HandleWidthDip = 30;

    /// <summary>手柄可见部分的高（DIP）。</summary>
    public const double HandleHeightDip = 6;

    /// <summary>两个手柄至少要空出的距离（DIP）。</summary>
    public const double GapDip = 4;

    /// <summary>命中区在左右各扩、下方扩出的边距（DIP）；上方不扩（贴住工作区顶边）。</summary>
    public const double HitMarginDip = 2;

    /// <summary>手柄描边宽度（物理像素，左边 / 下边 / 右边；顶边不画）。</summary>
    public const int HandleBorderDevicePixels = 2;

    /// <summary>进入时「滑到顶部 + 淡出」的时长（毫秒，InOutCubic）。</summary>
    public const int EnterDurationMs = 250;

    /// <summary>鼠标离开手柄 / 窗口之后延迟多久收起（毫秒）。</summary>
    public const int HideDelayMs = 100;

    /// <summary>手柄配色数量（照上游 13 个主题 accent）。</summary>
    public const int AccentCount = 13;

    /// <summary>13 个 accent 色（灰阶主题下也用同一套彩度，手柄才分得清是哪一张）。</summary>
    private static readonly Color[] Accents =
    {
        Color.FromRgb(0x16, 0x77, 0xFF),   // 蓝
        Color.FromRgb(0x72, 0x2E, 0xD1),   // 紫
        Color.FromRgb(0x13, 0xC2, 0xC2),   // 青
        Color.FromRgb(0x52, 0xC4, 0x1A),   // 绿
        Color.FromRgb(0xEB, 0x2F, 0x96),   // 洋红
        Color.FromRgb(0xFF, 0x85, 0xC0),   // 粉
        Color.FromRgb(0xF5, 0x22, 0x2D),   // 红
        Color.FromRgb(0xFA, 0x8C, 0x16),   // 橙
        Color.FromRgb(0xFA, 0xDB, 0x14),   // 黄
        Color.FromRgb(0xFA, 0x54, 0x1C),   // 火山
        Color.FromRgb(0x2F, 0x54, 0xEB),   // 极客蓝
        Color.FromRgb(0xFA, 0xAD, 0x14),   // 金
        Color.FromRgb(0xA0, 0xD9, 0x11),   // 青柠
    };

    /// <summary>空矩形（<see cref="PlaceHandle"/> 找不到合法位置时返回它）。</summary>
    public static Int32Rect Empty => new(0, 0, 0, 0);

    public static bool IsEmpty(Int32Rect rect) => rect.Width <= 0 || rect.Height <= 0;

    /// <summary>这块矩形里有没有这个物理像素点（右 / 下边界是开区间）。</summary>
    public static bool Contains(Int32Rect rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>显示器的工作区（物理像素）。</summary>
    public static Int32Rect WorkAreaOf(ScreenGeometry screen) =>
        new(screen.WorkLeft, screen.WorkTop, screen.WorkWidth, screen.WorkHeight);

    /// <summary>两块屏是不是同一块（显示器矩形相同；工作区 / DPI 变了不算换屏）。</summary>
    public static bool SameScreen(ScreenGeometry a, ScreenGeometry b) =>
        a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height;

    /// <summary>离这个点最近（命中优先）的显示器；一块都没有返回 <c>null</c>。</summary>
    public static ScreenGeometry? MonitorAt(IReadOnlyList<ScreenGeometry> screens, int x, int y)
    {
        ScreenGeometry? best = null;
        long bestDistance = long.MaxValue;

        foreach (var screen in screens)
        {
            if (Contains(new Int32Rect(screen.Left, screen.Top, screen.Width, screen.Height), x, y))
                return screen;

            long dx = x < screen.Left ? screen.Left - x
                : x >= screen.Left + screen.Width ? x - (screen.Left + screen.Width - 1)
                : 0;
            long dy = y < screen.Top ? screen.Top - y
                : y >= screen.Top + screen.Height ? y - (screen.Top + screen.Height - 1)
                : 0;
            long distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = screen;
        }

        return best;
    }

    /// <summary>
    /// 手柄落在工作区顶边的哪里（照上游 <c>placeHandle</c>）：先试首选位置，被同屏其他手柄
    /// 占住就往右找、再往左找，都占满就接受重叠（宁可叠一点也不能跑出屏幕）。
    /// </summary>
    /// <param name="workArea">所在显示器的工作区（物理像素）。</param>
    /// <param name="preferredLeft">首选左边（通常是被收起来那张贴图原来的左边）。</param>
    /// <param name="dpi">当前显示器缩放（96 DPI = 1）。</param>
    /// <param name="occupied">同屏其他手柄的矩形（物理像素）。</param>
    public static Int32Rect PlaceHandle(Int32Rect workArea, int preferredLeft, double dpi,
        IReadOnlyList<Int32Rect>? occupied)
    {
        if (IsEmpty(workArea) || double.IsNaN(dpi) || double.IsInfinity(dpi) || dpi <= 0)
            return Empty;

        int width = Math.Min(workArea.Width, Math.Max(1, Round(HandleWidthDip * dpi)));
        int height = Math.Min(workArea.Height, Math.Max(1, Round(HandleHeightDip * dpi)));
        int gap = Math.Max(1, Round(GapDip * dpi));

        int lastLeft = workArea.X + workArea.Width - width;
        int preferred = Math.Clamp(preferredLeft, workArea.X, lastLeft);

        bool Free(int left)
        {
            if (occupied == null) return true;

            foreach (var other in occupied)
            {
                if (IsEmpty(other)) continue;

                // 上游：left < other.right()+1+gap && left+width+gap > other.left()
                if (left < other.X + other.Width + gap && left + width + gap > other.X) return false;
            }

            return true;
        }

        var right = new List<int> { preferred };
        var left = new List<int> { preferred };
        if (occupied != null)
        {
            foreach (var other in occupied)
            {
                if (IsEmpty(other)) continue;

                right.Add(other.X + other.Width + gap);
                left.Add(other.X - gap - width);
            }
        }

        right.Sort();
        left.Sort((a, b) => b.CompareTo(a));

        foreach (int candidate in right)
        {
            if (candidate >= preferred && candidate <= lastLeft && Free(candidate))
                return new Int32Rect(candidate, workArea.Y, width, height);
        }

        foreach (int candidate in left)
        {
            if (candidate <= preferred && candidate >= workArea.X && Free(candidate))
                return new Int32Rect(candidate, workArea.Y, width, height);
        }

        return new Int32Rect(preferred, workArea.Y, width, height);
    }

    /// <summary>
    /// 手柄的命中区（照上游 <c>hitGeometry</c>）：左右各扩 <see cref="HitMarginDip"/>、
    /// 下方扩同样的距离，上方不扩，最后夹进工作区（贴边时不留出屏幕）。
    /// </summary>
    public static Int32Rect HitGeometry(Int32Rect handle, Int32Rect workArea, double dpi)
    {
        if (IsEmpty(handle) || IsEmpty(workArea)) return Empty;

        int margin = Math.Max(1, Round(HitMarginDip * dpi));
        var hit = new Int32Rect(handle.X - margin, handle.Y,
            handle.Width + margin * 2, handle.Height + margin);

        return Intersect(hit, workArea);
    }

    /// <summary>两个矩形求交；不相交返回空。</summary>
    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        return right <= left || bottom <= top ? Empty : new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>进入动画的缓动（上游 <c>QEasingCurve::InOutCubic</c>）。</summary>
    public static double EaseInOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>进入动画每一拍的窗口顶边（物理像素，按进度线性插值；调用方传缓动后的进度）。</summary>
    public static int InterpolateTop(int entryTop, int workTop, double progress) =>
        Round(entryTop + (workTop - entryTop) * Math.Clamp(progress, 0, 1));

    /// <summary>
    /// 进入 / 收起 / 展开时窗口不透明度该乘的淡出因子：隐藏 = 0、进入中 = 1 − 进度、其余 = 1。
    /// 最终不透明度 = 用户设置的不透明度 × 这个因子（照上游 <c>refreshOpacity</c>）。
    /// </summary>
    public static double OpacityFactor(bool hidden, bool entering, double progress)
    {
        if (hidden) return 0;
        return entering ? 1 - Math.Clamp(progress, 0, 1) : 1;
    }

    /// <summary>
    /// 这张贴图固定用哪个 accent 色：按贴图历史 Id 稳定取（同一张重开也是同一个颜色）；
    /// 空 Id（冒烟夹具）也照样有颜色，不会每次闪。
    /// </summary>
    public static int AccentIndexFor(string? pinId)
    {
        // FNV-1a 32 位：短、稳定、不依赖运行时随机化（string.GetHashCode 每次进程都不同）
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in pinId ?? "")
            {
                hash ^= c;
                hash *= 16777619;
            }

            return (int)(hash % AccentCount);
        }
    }

    /// <summary>第几个 accent（越界夹回 0–12）。</summary>
    public static Color AccentColor(int index) => Accents[Math.Clamp(index, 0, AccentCount - 1)];

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
