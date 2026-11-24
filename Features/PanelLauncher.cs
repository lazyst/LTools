using System;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 面板唤起入口：默认让普通权限宿主开，宿主没连上时退回本地。
/// </summary>
/// <remarks>
/// 宿主连着 → 让它开（它拿得到资源管理器的拖放，也能显示在普通权限窗口层）；
/// 宿主没起来 / 刚启动还没连上 → 照旧在提权进程里开，**保证面板永远能唤出来**。
/// **所有**「显示 / 切换面板」的入口都走这里，别直接调 <see cref="SuperPanel"/>，
/// 否则会出现两个面板。见 <see cref="PanelRouting"/>。
/// </remarks>
internal static class PanelLauncher
{
    /// <summary>在光标处显示面板（右键长按手势 / 其它显式入口）。</summary>
    public static void Show(IntPtr contextHwnd)
    {
        if (PanelRouting.Enabled && LinkWorker.TryShowPanel(contextHwnd)) return;
        SuperPanel.Show(contextHwnd);
    }

    /// <summary>切换面板显示 / 关闭（CapsLock+T、托盘菜单）。</summary>
    public static void Toggle()
    {
        if (PanelRouting.Enabled && LinkWorker.TryTogglePanel()) return;
        SuperPanel.Toggle();
    }
}
