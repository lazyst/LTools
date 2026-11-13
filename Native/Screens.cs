using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>一块显示器的物理矩形、工作区与有效 DPI。</summary>
/// <remarks>坐标一律**物理像素**，与截图 / 找图 / 鼠标注入同一套坐标系（<see cref="ScreenCapture"/> 也是物理像素）。</remarks>
internal sealed record ScreenGeometry(
    int Left, int Top, int Width, int Height,
    int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight,
    uint Dpi, bool Primary)
{
    /// <summary>缩放比例（96 DPI = 1.0）。写 DIP 换算时用这个。</summary>
    public double Scale => Dpi / 96.0;
}

/// <summary>
/// 显示器枚举：给「屏幕坐标容易写偏」的地方一份准数（MCP <c>ltools_health</c> 的环境信息）。
/// </summary>
/// <remarks>
/// 虚拟桌面整体矩形用 <see cref="ScreenCapture"/> 的 <c>Virtual*</c>（GetSystemMetrics），
/// 这里只回答「有哪几块屏、各自的矩形 / 工作区 / DPI」。多屏时 <c>Left</c> / <c>Top</c> 可能是负数
/// （主屏左侧的副屏），别假设从 0 开始。
/// </remarks>
internal static class Screens
{
    /// <summary>当前所有显示器（顺序按系统枚举，至少一块）。</summary>
    public static List<ScreenGeometry> All()
    {
        var list = new List<ScreenGeometry>();

        bool Collect(IntPtr monitor, IntPtr hdc, ref Win32.Rect rect, IntPtr data)
        {
            var info = new Win32.MonitorInfo { cbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
            if (!Win32.GetMonitorInfo(monitor, ref info)) return true;   // 拿不到就跳过，别中断枚举

            uint dpi = 96;
            try
            {
                // shcore 在 Win8.1+ 才有；拿不到就当 96（1.0 倍），别让整块信息失败。
                if (Win32.GetDpiForMonitor(monitor, Win32.MdtEffectiveDpi, out uint x, out _) == 0 && x > 0)
                    dpi = x;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            list.Add(new ScreenGeometry(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                info.rcWork.Left, info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top,
                dpi, (info.dwFlags & Win32.MonitorInfofPrimary) != 0));
            return true;
        }

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return list;
    }
}
