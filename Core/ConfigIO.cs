using System;
using LTools.Config;

namespace LTools.Core;

/// <summary>
/// 配置读-改-写的串行化入口（P3 起按域收口）。
/// 多处（设置窗口防抖保存、钩子路径上的开关切换、终端路径对话框、超级面板编辑）都会改动配置，
/// 并发的 Load→Save 会互相覆盖；本类用一把锁把「读 → 修改 → 写」当成原子段串行执行。
/// </summary>
/// <remarks>
/// <para>
/// 日常修改走 <c>Config/DomainStores.cs</c> 里的 <c>SettingsStore</c> / <c>UiStore</c> / <c>HotkeyStore</c>
/// / <c>PanelStore</c> / <c>FloatingStore</c> / <c>MacroFileStore</c> / <c>TriggerStore</c>：它们的 <see cref="ModifyDomain{T}"/>
/// **只写自己那一个域文件**，改一个开关不会再扫 <c>actions/</c> 或重写其它域。
/// </para>
/// <para>
/// 只有需要同时落多个域的才用 <see cref="Modify(ConfigDomain, Action{AppConfig})"/>（例：动作清单 + 快捷键）。
/// 读仍走整份聚合：首次写入能带上示例模板的种子，也让 <see cref="AppConfig.LoadStrict"/>
/// 的「坏文件不覆盖」语义保持不变。
/// </para>
/// </remarks>
internal static class ConfigIO
{
    private static readonly object _lock = new();

    /// <summary>
    /// 单域读-改-写：读出聚合 → 转成该域的磁盘形状 → 改 → 只写该域。
    /// </summary>
    /// <param name="path">配置锚点；null = <see cref="ConfigLocator.FindPath"/>。</param>
    internal static void ModifyDomain<T>(ConfigDomain domain, Func<AppConfig, T> read, Action<T> change,
        Action<string, T> write) => ModifyDomain(null, domain, read, change, write);

    /// <inheritdoc cref="ModifyDomain{T}(ConfigDomain, Func{AppConfig, T}, Action{T}, Action{string, T})"/>
    internal static void ModifyDomain<T>(string? path, ConfigDomain domain, Func<AppConfig, T> read,
        Action<T> change, Action<string, T> write)
    {
        if (change == null) return;
        lock (_lock)
        {
            string anchor = Resolve(path);
            var cfg = LoadForWrite(anchor);
            // 域文件缺失（首装 / 备份导入只带回一部分）：先整份补齐，再写本次修改的域。
            if (!ConfigBundle.IsFullySplit(anchor)) ConfigBundle.SaveDomains(anchor, cfg, ConfigDomain.all);

            T model = read(cfg);
            change(model);
            write(anchor, model);
        }
    }

    /// <summary>多域读-改-写（例如菜单组 + 动作清单必须一起落盘）。</summary>
    public static void Modify(ConfigDomain domains, Action<AppConfig> change) => Modify(null, domains, change);

    /// <inheritdoc cref="Modify(ConfigDomain, Action{AppConfig})"/>
    public static void Modify(string? path, ConfigDomain domains, Action<AppConfig> change)
    {
        if (change == null) return;
        lock (_lock)
        {
            string anchor = Resolve(path);
            var cfg = LoadForWrite(anchor);
            change(cfg);
            ConfigBundle.SaveDomains(anchor, cfg, domains);
        }
    }

    private static string Resolve(string? path)
        => string.IsNullOrWhiteSpace(path) ? ConfigLocator.FindPath() : path;

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
