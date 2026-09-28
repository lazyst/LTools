using System;
using CapsLockPro.Config;

namespace CapsLockPro.Core;

/// <summary>
/// 配置文件读-改-写的串行化入口。多处（设置窗口防抖保存、钩子路径上的开关切换、
/// 终端路径对话框、超级面板编辑）都会整文件重写同一个 <c>CapsLock++.json</c>，
/// 并发的 Load→Save 会互相覆盖（读到旧状态后整体写回）。本类用一把锁把
/// 「读磁盘 → 修改 → 写磁盘」当成原子段串行执行，消除丢失更新。
/// </summary>
internal static class ConfigIO
{
    private static readonly object _lock = new();

    /// <summary>读-改-写一次配置；所有修改者都应经由此方法。</summary>
    public static void Modify(Action<AppConfig> change)
    {
        if (change == null) return;
        lock (_lock)
        {
            var path = ConfigLocator.FindPath();
            var cfg = AppConfig.Load(path);
            change(cfg);
            cfg.Save(path);
        }
    }

    /// <summary>带自定义路径的读-改-写（设置窗口使用固定路径时）。</summary>
    public static void Modify(string path, Action<AppConfig> change)
    {
        if (change == null) return;
        lock (_lock)
        {
            var cfg = AppConfig.Load(path);
            change(cfg);
            cfg.Save(path);
        }
    }
}
