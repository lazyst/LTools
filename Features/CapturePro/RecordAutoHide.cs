using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>录屏工具条贴着哪一边（折叠后的小条就贴那边）。</summary>
internal enum RecordToolbarSide
{
    None,
    Left,
    Right,
}

/// <summary>
/// 录屏工具条「贴边自动收起」的纯几何（照 Snow Shot 的
/// <c>presentation/recording/screenrecordingtoolbarautohide.h</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游的判定都是 QRect 语义（<c>right = x + w - 1</c>、相邻但不重叠 = 不相交），这里逐条照搬，
/// 所以坐标一律是**屏幕物理像素**，与录屏区域 / 鼠标注入同一套。只有 <see cref="TabGeometry"/>
/// 的 <c>scale</c> 是「窗口 DIP → 物理像素」的比例（20 DIP 的小条要按它换宽度）。
/// </para>
/// <para>
/// 这里的函数都是纯函数、不碰窗口，<c>--selftest</c> 直接断言；真正的显隐状态机在
/// <c>Views/CapturePro/CaptureRecordWindow.AutoHide.cs</c>。
/// </para>
/// </remarks>
internal static class RecordAutoHide
{
    /// <summary>贴边多少像素以内算「可以收起」（上游 <c>edgeThreshold</c>）。</summary>
    public const int EdgeThreshold = 16;

    /// <summary>折叠后小条的宽度（DIP，上游 20 × scale 后夹到 1…屏宽）。</summary>
    public const double TabWidth = 20;

    /// <summary>悬停多久唤出工具条（上游 reveal 计时器）。</summary>
    public const int RevealDelayMs = 100;

    /// <summary>指针离开多久收起（上游 retreat 计时器）。</summary>
    public const int RetreatDelayMs = 200;

    /// <summary>展开状态下的观察间隔（上游 watch 计时器）。</summary>
    public const int WatchIntervalMs = 50;

    /// <summary>空矩形（宽或高 ≤ 0）。</summary>
    public static bool IsEmpty(Int32Rect rect) => rect.Width <= 0 || rect.Height <= 0;

    /// <summary>QRect 语义的相交：共享至少一个像素（相邻不算）。</summary>
    public static bool Intersects(Int32Rect a, Int32Rect b) =>
        !IsEmpty(a) && !IsEmpty(b)
        && a.X < b.X + b.Width && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    /// <summary>矩形是否包含某个物理像素点。</summary>
    public static bool Contains(Int32Rect rect, int x, int y) =>
        !IsEmpty(rect) && x >= rect.X && x < rect.X + rect.Width
        && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>
    /// 主行贴着哪一边（上游 <c>sideForRow</c>）：两边间距都超过阈值 → 不贴边；
    /// 间距相等时沿用上一次的边（<paramref name="previous"/> 为空则取右）；
    /// 越过边缘记为负间距，仍然算那一边，越得多的一边优先。
    /// </summary>
    public static RecordToolbarSide SideForRow(Int32Rect row, Int32Rect bounds,
        RecordToolbarSide previous = RecordToolbarSide.None)
    {
        if (IsEmpty(row) || IsEmpty(bounds) || !Intersects(row, bounds)) return RecordToolbarSide.None;

        int left = row.X - bounds.X;
        int right = (bounds.X + bounds.Width - 1) - (row.X + row.Width - 1);

        if (left > EdgeThreshold && right > EdgeThreshold) return RecordToolbarSide.None;
        if (left == right)
            return previous == RecordToolbarSide.None ? RecordToolbarSide.Right : previous;

        return left < right ? RecordToolbarSide.Left : RecordToolbarSide.Right;
    }

    /// <summary>
    /// 折叠后小条的位置（上游 <c>tabGeometry</c>）：宽 20 DIP × <paramref name="scale"/>（夹到 1…屏宽），
    /// 高跟主行一样（不超过屏高），贴在那一侧的屏幕边上，纵向以主行为中心并夹回屏幕内。
    /// </summary>
    public static Int32Rect TabGeometry(Int32Rect row, Int32Rect bounds, RecordToolbarSide side,
        double scale)
    {
        if (IsEmpty(row) || IsEmpty(bounds) || side == RecordToolbarSide.None) return default;

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

        int width = Math.Clamp((int)Math.Round(TabWidth * scale, MidpointRounding.AwayFromZero),
            1, bounds.Width);
        int height = Math.Min(row.Height, bounds.Height);

        int x = side == RecordToolbarSide.Left ? bounds.X : bounds.X + bounds.Width - width;
        int y = Math.Clamp(row.Y + (row.Height - height) / 2,
            bounds.Y, bounds.Y + bounds.Height - height);

        return new Int32Rect(x, y, width, height);
    }

    /// <summary>
    /// 小条与主行之间的过渡区（上游 <c>transferRegion</c>）：鼠标从小条滑到主行的路上
    /// （或反过来）不该触发收起 / 唤出。
    /// </summary>
    public static Int32Rect TransferRegion(Int32Rect row, Int32Rect tab, RecordToolbarSide side)
    {
        if (IsEmpty(row) || IsEmpty(tab) || side == RecordToolbarSide.None) return default;

        int rowEdge = side == RecordToolbarSide.Left ? row.X : row.X + row.Width - 1;
        int left = Math.Min(tab.X, rowEdge);
        int right = Math.Max(tab.X + tab.Width - 1, rowEdge);
        int top = Math.Min(tab.Y, row.Y + row.Height - 1);
        int bottom = Math.Max(tab.Y + tab.Height - 1, row.Y);

        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// 现在能不能收起（上游 <c>eligible</c>）：设置里开着、真的在录、且不在收尾过程中。
    /// 「摆着等用户点录制」不算 —— 那时面板不该自己消失。
    /// </summary>
    public static bool IsEligible(bool enabled, bool recording, bool busy) =>
        enabled && recording && !busy;
}
