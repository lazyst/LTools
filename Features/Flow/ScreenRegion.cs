using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 屏幕取样区域：图像匹配与截图 OCR 共用同一套「前台窗口 / 整个屏幕 / 固定矩形」语义，
/// 返回虚拟桌面物理像素坐标（与鼠标注入、截图坐标系一致）。
/// </summary>
internal static class ScreenRegion
{
    /// <summary>解析 <see cref="Probe.Region"/>；前台窗口无效时退回整个虚拟桌面。</summary>
    public static (int x, int y, int w, int h) Resolve(Probe probe)
    {
        var full = (ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                    ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);

        if (probe.Region == ImageRegion.screen) return full;

        if (probe.Region == ImageRegion.rect)
        {
            int[]? r = probe.Rect;
            if (r == null || r.Length < 4 || r[2] <= 0 || r[3] <= 0) return full;
            return (r[0], r[1], r[2], r[3]);
        }

        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var rect)) return full;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return full;
        return (rect.Left, rect.Top, w, h);
    }
}
