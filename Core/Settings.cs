using LTools.Config;
using LTools.Features;
namespace LTools.Core;

/// <summary>
/// 全局功能开关（CapsLock 修饰键 / 边缘滚轮音量 / 超级面板）的统一切换入口。
/// 设置「通用」页经由此改状态 + 持久化，避免多处实现漂移。
/// </summary>
internal static class Settings
{
    /// <summary>启用 / 禁用 CapsLock 修饰键（轻按 Esc + <c>CapsLock+…</c> 绑定）。立即生效并落盘。</summary>
    public static void SetCapsLockModifier(bool enabled)
    {
        AppState.CapsLockModifier = enabled;
        AppLog.Info("Settings", $"CapsLock 修饰键 = {(enabled ? "启用" : "禁用")}");
        Save(() => SettingsStore.Modify(s => s.CapsLockModifier = enabled));
        TrayService.Notify(enabled ? "CapsLock 修饰键已启用" : "CapsLock 改用原生大小写切换");
    }

    /// <summary>切换 CapsLock 修饰键（图标菜单用）。</summary>
    public static void ToggleCapsLockModifier() => SetCapsLockModifier(!AppState.CapsLockModifier);

    /// <summary>启用 / 禁用边缘滚轮音量。立即生效并落盘。</summary>
    public static void SetEdgeWheelVolume(bool enabled)
    {
        AppState.EdgeWheelVolume = enabled;
        AppLog.Info("Settings", $"边缘滚轮音量 = {(enabled ? "启用" : "禁用")}");
        Save(() => SettingsStore.Modify(s => s.EdgeWheelVolume = enabled));
    }

    /// <summary>改边缘滚轮音量算哪条边。立即生效并落盘。</summary>
    public static void SetEdgeWheelVolumeEdge(WheelVolumeEdge edge)
    {
        AppState.EdgeWheelVolumeEdge = (int)edge;
        AppLog.Info("Settings", $"边缘滚轮音量边 = {edge}");
        Save(() => SettingsStore.Modify(s => s.EdgeWheelVolumeEdge = edge));
    }

    /// <summary>启用 / 禁用超级面板。立即生效并落盘。</summary>
    public static void SetSuperPanelEnabled(bool enabled)
    {
        AppState.IsSuperPanelEnabled = enabled;
        AppLog.Info("Settings", $"超级面板 = {(enabled ? "启用" : "禁用")}");
        Save(() => PanelStore.Modify(p => p.SuperPanel.Enabled = enabled));
        TrayService.Notify(enabled ? "超级面板已启用" : "超级面板已禁用");
    }

    /// <summary>切换超级面板开关（托盘菜单用）。</summary>
    public static void ToggleSuperPanel() => SetSuperPanelEnabled(!AppState.IsSuperPanelEnabled);

    /// <summary>把改动写回配置（读磁盘→改一个字段→只写该域）。
    /// 落盘放后台线程：这些入口可能在钩子回调路径上被调用，
    /// 钩子回调不得阻塞（&gt;300ms 系统会卸载钩子）；状态本身已同步改完。
    /// 读-改-写经 <see cref="ConfigIO"/> 加锁，避免与设置窗口的保存互相覆盖。</summary>
    private static void Save(Action write)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { write(); }
            catch (Exception ex) { CrashLog.Write("Settings.Save", ex); }
        });
    }
}
