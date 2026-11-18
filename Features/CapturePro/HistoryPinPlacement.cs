using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图完成时一并记下的「它原来在屏幕哪儿、长什么样」，供采集历史按原选区 + 原外观贴回。
/// </summary>
/// <remarks>
/// 对应上游 <c>CaptureHistoryRecord</c> 的 <c>desktopGeometry</c> + <c>selection</c>
/// （本地坐标本来就是虚拟桌面物理像素，不需要再记一个画布原点）。
/// </remarks>
internal sealed record CapturePlacement(Int32Rect Selection, PinAppearance Appearance, bool Scrolling)
{
    /// <summary>从选区与样式算出落位信息（长截图不记外观：拼出来的长图没有那一圈效果）。</summary>
    public static CapturePlacement Of(Int32Rect selection, SelectionStyle style, double scale,
        bool scrolling) =>
        new(selection,
            scrolling ? PinAppearance.None
                      : PinAppearance.FromStyle(style, scale, selection.Width, selection.Height),
            scrolling);
}

/// <summary>
/// 采集历史「按原选区贴回」的判定与几何（照上游 <c>presentation/historypinplacement</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 能贴回的记录要同时满足：是截图（含识别，不含录屏）、不是长截图、记过一块可用选区。
/// 老记录（P5-h 之前）没有落位信息，只能退回「贴到光标所在屏中间」——
/// 与上游「只有画布坐标、没有桌面原点就没法还原绝对位置」同口径。
/// </para>
/// <para>
/// 落点用**选区**跟当前桌面求交：一块屏都碰不上（显示器拔了 / 分辨率改了）也走回落逻辑，
/// 不硬摆到看不见的地方（上游：<c>Off-desktop selections do not use the live pin's
/// nearest-display fallback</c>）。
/// </para>
/// </remarks>
internal static class HistoryPinPlacement
{
    /// <summary>这条记录能不能按原选区贴回。</summary>
    public static bool SupportsPin(CaptureEntry? entry) =>
        entry != null
        && (entry.Kind == CaptureCatalog.KindShot || entry.Kind == CaptureCatalog.KindRecognize)
        && !entry.Scrolling
        && entry.SelectionW >= 1 && entry.SelectionH >= 1;

    /// <summary>当初框的那块选区（虚拟桌面物理像素）；不能按原位置贴回时是 <c>null</c>。</summary>
    public static Int32Rect? Selection(CaptureEntry? entry) => SupportsPin(entry)
        ? new Int32Rect(entry!.SelectionX, entry.SelectionY, entry.SelectionW, entry.SelectionH)
        : null;

    /// <summary>这条记录该用的外观（圆角 / 效果已经烘进图里）。</summary>
    public static PinAppearance Appearance(CaptureEntry? entry) => entry == null
        ? PinAppearance.None
        : new PinAppearance
        {
            RadiusPx = entry.RadiusPx,
            EffectPx = entry.EffectPx,
            Decoration = PinAppearance.NormalizeDecoration(entry.Decoration),
        };

    /// <summary>
    /// 贴图窗口该占的屏幕矩形：内容正好落回原选区，带效果的图往四周让出内边距。
    /// 不支持按原位置贴回（长截图 / 老记录）时是 <c>null</c>。
    /// </summary>
    public static Int32Rect? WindowRect(CaptureEntry? entry, int imageWidth, int imageHeight)
    {
        if (Selection(entry) is not Int32Rect content) return null;
        return Appearance(entry).WindowRect(content, imageWidth, imageHeight);
    }

    /// <summary>这块矩形跟给定的显示器列表还有交集吗（一块都碰不上就别按原位置摆）。</summary>
    public static bool MeetsDesktop(Int32Rect rect, IReadOnlyList<Int32Rect>? monitors)
    {
        if (monitors == null || rect.Width <= 0 || rect.Height <= 0) return false;

        foreach (var monitor in monitors)
        {
            int left = Math.Max(rect.X, monitor.X);
            int top = Math.Max(rect.Y, monitor.Y);
            int right = Math.Min(rect.X + rect.Width, monitor.X + monitor.Width);
            int bottom = Math.Min(rect.Y + rect.Height, monitor.Y + monitor.Height);
            if (right > left && bottom > top) return true;
        }

        return false;
    }
}
