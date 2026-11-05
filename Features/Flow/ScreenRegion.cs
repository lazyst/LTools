using System;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 屏幕取样区域：图像匹配与「文字点击」共用（前台窗口 / 整个屏幕 / 固定矩形），
/// 返回虚拟桌面物理像素坐标（与鼠标注入、截图坐标系一致）。
/// </summary>
internal static class ScreenRegion
{
    /// <summary>解析 <see cref="Probe.Region"/>；前台窗口无效时退回整个虚拟桌面。</summary>
    public static (int x, int y, int w, int h) Resolve(Probe probe)
        => Resolve(probe.Region, probe.Rect);

    /// <summary>按区域设置取矩形；前台窗口无效时退回整个虚拟桌面。</summary>
    public static (int x, int y, int w, int h) Resolve(ImageRegion region, int[]? rect)
    {
        var full = (ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                    ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);

        if (region == ImageRegion.screen) return full;

        if (region == ImageRegion.rect)
        {
            if (rect == null || rect.Length < 4 || rect[2] <= 0 || rect[3] <= 0) return full;
            return (rect[0], rect[1], rect[2], rect[3]);
        }

        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return full;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return full;
        return (r.Left, r.Top, w, h);
    }
}
