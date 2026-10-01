using LTools.Config;
using LTools.Features;

namespace LTools.Core;

/// <summary>
/// 两个全局开关（CapsLock 键功能 / 超级面板）的统一切换入口。
/// 设置「通用」页、托盘右键菜单、CapsLock+Esc 手势都经由此改状态 + 持久化，避免多处实现漂移。
/// </summary>
internal static class Settings
{
    /// <summary>启用 / 禁用 CapsLock 键功能（CapsLock+Esc 手势、菜单、鼠标模式等）。立即生效并落盘。</summary>
    public static void SetCapsLockEnabled(bool enabled)
    {
        AppState.IsToolEnabled = enabled;
        SaveBool((cfg, on) => cfg.CapsLockEnabled = on, enabled);
        TrayService.Notify(enabled ? "CapsLock 键功能已启用" : "CapsLock 键功能已禁用");
    }

    /// <summary>启用 / 禁用超级面板。立即生效并落盘。</summary>
    public static void SetSuperPanelEnabled(bool enabled)
    {
        AppState.IsSuperPanelEnabled = enabled;
        SaveBool((cfg, on) => cfg.SuperPanel.Enabled = on, enabled);
        TrayService.Notify(enabled ? "超级面板已启用" : "超级面板已禁用");
    }

    /// <summary>切换 CapsLock 键功能开关（托盘菜单用）。</summary>
    public static void ToggleCapsLock() => SetCapsLockEnabled(!AppState.IsToolEnabled);

    /// <summary>切换超级面板开关（托盘菜单用）。</summary>
    public static void ToggleSuperPanel() => SetSuperPanelEnabled(!AppState.IsSuperPanelEnabled);

    /// <summary>把单个 bool 设置写回配置（读磁盘→改一个字段→整体保存）。
    /// 落盘放后台线程：<see cref="SetCapsLockEnabled"/> 会在钩子回调路径上被调用，
    /// 钩子回调不得阻塞（&gt;300ms 系统会卸载钩子）；状态本身已同步改完。
    /// 读-改-写经 <see cref="ConfigIO"/> 加锁，避免与设置窗口的保存互相覆盖。</summary>
    private static void SaveBool(Action<AppConfig, bool> write, bool value)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { ConfigIO.Modify(cfg => write(cfg, value)); }
            catch (Exception ex) { CrashLog.Write("Settings.SaveBool", ex); }
        });
    }
}
