using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Core.Storage;

/// <summary>
/// 删除动作的显式顺序，以及引用缺席时的幂等修复（见 <c>docs/ARCHITECTURE.md</c> P3）。
/// </summary>
/// <remarks>
/// <para>
/// 一个动作会同时被面板磁贴 / 快捷键绑定 / 触发规则 / 悬浮项引用，分散在几个配置域与内存状态里。
/// 不做分布式事务，只把**顺序**固定下来，让最坏情况是「多留一个空槽」而不是「指向不存在的动作」：
/// </para>
/// <list type="number">
/// <item>内存清单（<see cref="ActionLibrary.Remove"/>）；</item>
/// <item>快捷键绑定 + 面板磁贴 + 自动触发规则（<see cref="HotkeyStore"/> /
///       <see cref="PanelLibrary.RemoveActionReferences"/> / <see cref="TriggerStore"/>，
///       只写 <c>hotkeys.json</c> / <c>panel.json</c> / <c>triggers.json</c>）；</item>
/// <item>动作目录（<see cref="ConfigStore.Save"/>，写 <c>actions/</c>）；</item>
/// <item>宏 / 模板图 / 悬浮项按引用回收（动作目录已经不在，回收不会误伤别的动作）。</item>
/// </list>
/// <para>
/// 若在第 2 步之后崩溃：面板少了槽位、动作还在——用户重新加回槽位即可（不会指向不存在的动作）。
/// 若外部编辑 / 备份导入留下悬空引用，启动时 <see cref="RepairReferences"/> 会按磁盘动作清单幂等清掉。
/// </para>
/// </remarks>
internal static class ActionRemoval
{
    /// <summary>删除确认框的文案：面板与设置两个入口共用，清理范围变了只改这里。</summary>
    public static string ConfirmMessage(string id)
    {
        string name = ActionRegistry.DisplayName(id);
        string refs = PanelLibrary.DescribeReferences(id);
        return $"确定删除「{name}」吗？\n\n面板引用：{refs}\n" +
               "删除会一并清掉面板磁贴、快捷键绑定、触发规则与悬浮项，并回收不再被引用的宏与模板图。";
    }

    /// <summary>删除动作；返回是否确实存在。顺序见类型注释。</summary>
    public static bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!ActionLibrary.Remove(id)) return false;      // 清清单（内存）

        // 清快捷键绑定（hotkeys.json）、面板磁贴（panel.json）与自动触发规则（triggers.json）。
        string path = string.IsNullOrWhiteSpace(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;
        HotkeyStore.Modify(path, file => RemoveFromHotkeys(file, id));
        SuperPanel.RemoveActionReferences(id);
        TriggerStore.Modify(path, file => RemoveFromTriggers(file, id));

        ConfigStore.Save();                               // 先落「动作没了」，再做回收

        // 一宏一动作：动作删除后按引用回收宏文件（仍被其它动作 / 子动作引用的宏会保留）。
        MacroStore.CollectGarbage();
        // 模板图同理：被删动作独占的 shot-*.png 立刻回收，不必等下次启动。
        ImageLibrary.CollectGarbage();
        // 悬浮项按引用回收：动作没了，按钮也跟着消失（分组空了整项移除）。
        FloatingManager.RefreshAll();
        // 运行期触发表立即重建（否则删掉的动作仍会被定时投递，直到重启）。
        TriggerService.ReloadFromDisk(path);
        return true;
    }

    /// <summary>把引用该动作的绑定从预设组里去掉（空组保留，用户可能还要往里加）。</summary>
    internal static void RemoveFromHotkeys(HotkeyFile file, string actionId)
    {
        foreach (var group in file.Groups)
            group.Bindings.RemoveAll(b => string.Equals(b.ActionId, actionId, StringComparison.Ordinal));
    }

    /// <summary>把引用该动作的规则从自动触发里去掉（对齐快捷键：只删引用该 Id 的规则）。</summary>
    internal static void RemoveFromTriggers(TriggersFile file, string actionId)
    {
        file.Rules.RemoveAll(r => string.Equals(r.ActionRef, actionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 清掉指向已不存在动作的快捷键绑定 / 面板槽位。按**磁盘**动作目录判定存活，
    /// 因此必须在 <c>ConfigStore.Initialize</c> 加载内存清单 / <see cref="PanelLibrary.Initialize"/>
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

            bool menusChanged = cfg.Hotkeys.Groups.Any(g => g.Bindings.Any(b =>
                !BindingRef.IsLive(b.ActionId, live) && !string.IsNullOrWhiteSpace(b.ActionId)));
            if (menusChanged)
            {
                HotkeyStore.Modify(path, file =>
                {
                    foreach (var group in file.Groups)
                        group.Bindings.RemoveAll(b =>
                            !string.IsNullOrWhiteSpace(b.ActionId) && !BindingRef.IsLive(b.ActionId, live));
                });
                repaired = true;
                AppLog.Info("ActionRemoval", "已清掉快捷键里指向不存在动作的绑定");
            }

            // Panel 为 null = 全新安装（还没有面板结构），这里没有可修的东西。
            if (cfg.Panel != null && PanelLibrary.PruneMissing(cfg.Panel, live))
            {
                PanelStore.Modify(path, file =>
                {
                    if (file.Panel != null) PanelLibrary.PruneMissing(file.Panel, live);
                });
                repaired = true;
                AppLog.Info("ActionRemoval", "已清掉超级面板里指向不存在动作的槽位");
            }

            // 自动触发：清掉指向已删动作的规则（内置功能引用恒存活，IsLive 会留着它们；
            // 空 ActionRef 是「规则建好还没选动作」的合法状态，也不清）。
            bool triggersChanged = cfg.Triggers.Rules.Any(r =>
                !string.IsNullOrWhiteSpace(r.ActionRef) && !BindingRef.IsLive(r.ActionRef, live));
            if (triggersChanged)
            {
                TriggerStore.Modify(path, file => file.Rules.RemoveAll(r =>
                    !string.IsNullOrWhiteSpace(r.ActionRef) && !BindingRef.IsLive(r.ActionRef, live)));
                repaired = true;
                AppLog.Info("ActionRemoval", "已清掉自动触发里指向不存在动作的规则");
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
