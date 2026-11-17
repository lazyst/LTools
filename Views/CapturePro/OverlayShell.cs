using System;
using System.Windows;
using System.Windows.Interop;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「浮在屏幕上的壳窗口」的公共出场设置：贴图、录制控制条、录制倒计时都要
/// <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> + 置顶。
/// </summary>
/// <remarks>
/// **不抢焦点是不变的约定**：点它、滚它都不该让前台变成本程序——否则紧接着注入的键鼠全打在壳上。
/// 代价是收不到键盘事件，所以每个壳的出口都得是鼠标可达的（见各自的注释）。
/// </remarks>
internal static class OverlayShell
{
    /// <summary>把窗口变成不抢焦点的工具窗口，并钉在最上面。</summary>
    /// <param name="window">句柄已经建好的窗口（<c>OnSourceInitialized</c> / <c>SourceInitialized</c> 里调）。</param>
    /// <param name="excludeFromCapture">同时把自己从录屏 / 截图里排除（老系统失败就算了）。</param>
    /// <returns>窗口句柄；<see cref="IntPtr.Zero"/> 表示还拿不到，调用方就此收手。</returns>
    public static IntPtr MakeNonActivating(Window window, bool excludeFromCapture)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return hwnd;

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(hwnd, Win32.GwlExstyle,
            (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));

        // 自己不出现在录制里（老系统失败就算了，不影响录制本身）
        if (excludeFromCapture) Win32.SetWindowDisplayAffinity(hwnd, Win32.WdaExcludeFromCapture);

        Win32.SetWindowPos(hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);

        return hwnd;
    }
}
