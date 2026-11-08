using System;
using LTools.Config;

namespace LTools.Core;

/// <summary>
/// 配置文件读-改-写的串行化入口。多处（设置窗口防抖保存、钩子路径上的开关切换、
/// 终端路径对话框、超级面板编辑）都会整份读改写同一组配置，并发的 Load→Save 会互相覆盖
/// （读到旧状态后整体写回）。本类用一把锁把「读磁盘 → 修改 → 写磁盘」当成原子段串行执行。
/// </summary>
/// <remarks>
/// 分域后（见 <see cref="ConfigBundle"/>）一次保存只会真正写回内容变了的域文件，
/// 但「读整份 → 改 → 写整份」的语义保持不变，调用点无需关心自己属于哪个域。
/// 写前用 <see cref="AppConfig.LoadStrict"/>：有域文件无法解析时**抛出并放弃写入**，
/// 绝不拿默认值覆盖原文件。调用方按需捕获：设置页会提示「保存失败」，后台开关与面板记忆则静默留在日志里。
/// </remarks>
internal static class ConfigIO
{
    private static readonly object _lock = new();

    /// <summary>读-改-写一次配置；解析失败时不写入并抛出。</summary>
    public static void Modify(Action<AppConfig> change)
    {
        if (change == null) return;
        lock (_lock)
        {
            var path = ConfigLocator.FindPath();
            var cfg = LoadForWrite(path);
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
            var cfg = LoadForWrite(path);
            change(cfg);
            cfg.Save(path);
        }
    }

    /// <summary>
    /// 写配置前的严格读取：配置不存在 = 首次保存（按默认模板写）；
    /// 存在但不可解析 = 立即失败，保留原文件，避免把用户数据洗成默认值。
    /// </summary>
    private static AppConfig LoadForWrite(string path)
    {
        try
        {
            return AppConfig.LoadStrict(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Config", $"配置文件无法解析，已跳过本次写入（原文件保留）：{path}", ex);
            throw new InvalidOperationException(
                "配置文件无法解析，本次修改没有写入（原文件已保留）。"
                + "请修复配置，或从同目录的 LTools.json.bad-* 备份恢复后再试。", ex);
        }
    }
}
