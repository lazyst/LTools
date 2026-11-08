using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Core.Storage;

/// <summary>
/// 删除动作的显式顺序，以及引用缺席时的幂等修复（见 <c>docs/ARCHITECTURE.md</c> P3）。
/// </summary>
/// <remarks>
/// <para>
/// 一个动作被菜单组 / 超级面板 / 悬浮项 / 组合步骤同时引用，但分布在三个文件里。
/// 不做分布式事务，只把**顺序**固定下来，让最坏情况是「多留一个空槽」而不是「指向不存在的动作」：
/// </para>
/// <list type="number">
/// <item>内存清单与菜单项（<see cref="MenuSystem.RemoveAction"/>，顺带清组合步骤引用）；</item>
/// <item>超级面板槽位（<see cref="PanelLibrary.RemoveActionReferences"/>，只写 <c>panel.json</c>）；</item>
/// <item>菜单组 + 动作目录（<see cref="ConfigStore.Save"/>，写 <c>menus.json</c> 与 <c>actions/</c>）；</item>
/// <item>宏文件 / 模板图 / 悬浮项按引用回收（行动作目录已经不在，回收不会误伤别的动作）。</item>
/// </list>
/// <para>
/// 若在第 2 步之后崩溃：面板少了槽位、动作还在——用户重新加回槽位即可（不会指向不存在的动作）。
/// 若外部编辑 / 备份导入留下悬空引用，启动时 <see cref="RepairReferences"/> 会按磁盘动作清单幂等清掉。
/// </para>
/// </remarks>
internal static class ActionRemoval
{
    /// <summary>删除动作；返回是否确实存在。顺序见类型注释。</summary>
    public static bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!MenuSystem.RemoveAction(id)) return false;   // 清清单 + 菜单项 + 组合步骤（内存）

        SuperPanel.RemoveActionReferences(id);            // 清超级面板槽位（落盘 panel.json）
        ConfigStore.Save();                               // 先落「动作没了」，再做回收

        // 一宏一动作：动作删除后按引用回收宏文件（仍被组合步骤引用的宏会保留）。
        MacroStore.CollectGarbage();
        // 模板截图同理：被删动作 / 组合步骤独占的 shot-*.png 立刻回收，不必等下次启动。
        ImageLibrary.CollectGarbage();
        // 悬浮项按引用回收：动作没了，按钮也跟着消失（分组空了整项移除）。
        FloatingManager.RefreshAll();
        return true;
    }

    /// <summary>
    /// 清掉指向已不存在动作的菜单项 / 面板槽位。按**磁盘**动作目录判定存活，
    /// 因此必须在 <see cref="MenuSystem.ReloadFromConfig"/> / <see cref="PanelLibrary.Initialize"/>
    /// **之前**调用：内存里加载到的就是修好的结果。
    /// </summary>
    /// <param name="configPath">配置锚点；null = <see cref="ConfigStore.ConfigPath"/> / <see cref="ConfigLocator"/>。</param>
    /// <returns>是否有改动（没有悬空引用时不写盘）。</returns>
    public static bool RepairReferences(string? configPath = null)
    {
        try
        {
            string path = string.IsNullOrWhiteSpace(configPath)
                ? (string.IsNullOrWhiteSpace(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath)
                : configPath;
            var cfg = AppConfig.Load(path);
            var live = new HashSet<string>(cfg.Actions.Select(a => a.Id), StringComparer.Ordinal);
            bool repaired = false;

            bool menusChanged = cfg.MenuGroups.Any(g => g?.Items?.Any(item => !live.Contains(item)) == true);
            if (menusChanged)
            {
                MenuStore.Modify(path, menus =>
                {
                    foreach (var g in menus.MenuGroups)
                        g?.Items?.RemoveAll(item => !live.Contains(item));
                });
                repaired = true;
                AppLog.Info("ActionRemoval", "已清掉菜单组里指向不存在动作的项");
            }

            // Panel 为 null = 还没从旧页迁移（PanelLibrary.Initialize 会迁移），这里没有可修的东西。
            if (cfg.Panel != null && PanelLibrary.PruneMissing(cfg.Panel, live))
            {
                PanelStore.Modify(path, file =>
                {
                    if (file.Panel != null) PanelLibrary.PruneMissing(file.Panel, live);
                });
                repaired = true;
                AppLog.Info("ActionRemoval", "已清掉超级面板里指向不存在动作的槽位");
            }

            return repaired;
        }
        catch (Exception ex)
        {
            // 修引用失败不该阻止启动；下次启动再试。
            AppLog.Warn("ActionRemoval", "修复悬空引用失败：" + ex.Message);
            return false;
        }
    }
}
