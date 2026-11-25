using System;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 配置文件写入的**宿主侧**：<c>configs/*.json</c> 的唯一物理写者。
/// </summary>
/// <remarks>
/// <para>
/// 收到执行器转来的域配置就落盘（<see cref="ConfigBundle.TryWriteDomainFile"/>：
/// 临时文件 + 整体替换，名字不在七个域文件白名单里直接拒绝）。
/// </para>
/// <para>
/// **写完要刷宿主自己那份运行期状态**（<see cref="RefreshRuntime"/>）：这些页面的属主已经搬进宿主
/// （设置页 / 面板 / 宏页 / 截图 Pro），文件换了而内存不换，表现就是「托盘里改了，设置页还是旧值」。
/// 反过来执行器那边由 <c>config.changed</c> 自己重读。
/// </para>
/// </remarks>
internal static class DataHost
{
    /// <summary>处理一条配置写入请求；不是该方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method != DataMethods.Write) return null;

        DataWriteRequest? req = request.Read<DataWriteRequest>();
        if (req == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

        bool written;
        try
        {
            written = ConfigBundle.TryWriteDomainFile(ConfigLocator.FindPath(), req.Name, req.Json);
            if (written)
            {
                AppLog.Info("Data", $"已写入 {req.Name}（替主程序落盘）");
                RefreshRuntime(req.Name);
            }
            else AppLog.Warn("Data", $"拒绝写入 configs/{req.Name}（不在域文件白名单里）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Data", $"写入 {req.Name} 失败：{ex.Message}");
            return Task.FromResult(LinkMessage.Response(request, new DataWriteReply(false)));
        }

        return Task.FromResult(LinkMessage.Response(request, new DataWriteReply(written)));
    }

    /// <summary>
    /// 宿主写完 <c>configs/*.json</c> 之后刷一遍**自己**的运行期缓存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条路径都走这里：执行器转来的写入（<c>data.write</c>）与宿主自己的本地写入。
    /// **执行器那一份不用管**：宿主写完会发 <c>config.changed</c>，它在那边刷。
    /// </para>
    /// <para>
    /// 除了 <see cref="RuntimeRefresh"/> 那批「读一次就记住」的静态缓存，还有几样**宿主内存里的状态**
    /// 要跟上（漏了的表现是「托盘 / 别处改了，设置页还是旧值」）：
    /// </para>
    /// <list type="bullet">
    /// <item><c>settings.json</c> → <see cref="AppState.ApplySettings(Config.SettingsFile)"/>
    /// （延时 / 录屏默认 / 导出场景 / 终端路径）。只读这一个域，别整份 <c>AppConfig.Load</c> 去扫动作目录。</item>
    /// <item><c>panel.json</c> → 超级面板那两个开关。托盘「启用超级面板」就是这条路径；
    /// **面板结构以宿主内存为准**，别整份重读 <c>PanelLibrary</c>（会把正在拖的磁贴打回去）。</item>
    /// <item><c>macros.json</c> → 作废 <see cref="MacroStore"/> 的元数据缓存（录制完成是执行器写的）。</item>
    /// </list>
    /// <para>
    /// 新增一个「宿主读一次就记住」的域状态时在这里加一行；动作 / 快捷键的悬空引用走
    /// <c>actions.changed</c> 的整份重读，不在这里。
    /// </para>
    /// </remarks>
    internal static void RefreshRuntime(string? name)
    {
        try
        {
            if (Same(name, "settings.json"))
                AppState.ApplySettings(SettingsStore.Read(ConfigStore.ConfigPath));
            else if (Same(name, "panel.json"))
                AppState.ApplySuperPanel(PanelStore.Read(ConfigStore.ConfigPath).SuperPanel);
            else if (Same(name, "macros.json"))
                MacroStore.Invalidate();

            RuntimeRefresh.Invalidate(name);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Data", $"刷新运行期设置失败：{ex.Message}");
        }
    }

    private static bool Same(string? name, string file) =>
        string.Equals(name, file, StringComparison.OrdinalIgnoreCase);
}
