using System.Collections.Generic;
using System.Linq;
using LTools.Features;

namespace LTools.Features.Migration.Legacy;

/// <summary>
/// 旧配置清理：删除已取消动作化的内置动作，并清理菜单 / 超级面板 / 组合步骤里的引用，
/// 避免留下点了没反应的死项。
/// </summary>
/// <remarks>
/// <para>
/// 只处理 schema v1 的动作（<see cref="ActionType.@internal"/> 和旧 <c>steps</c> / <c>flow</c>）；
/// 新动作编译成 <see cref="ActionProgram"/>，没有这些字段，扫到也不会命中。
/// </para>
/// <list type="bullet">
/// <item>v2.3.3：<c>macro.playLast</c>（回放上次宏）→ 由「键鼠宏」动作取代。</item>
/// <item>v2.3.3：<c>settings.toggle</c>（设置）、<c>helpPanel.toggle</c>（帮助面板）→ 改为纯入口
/// （CapsLock+\ / CapsLock+` / 托盘 / 超级面板标题栏齿轮），不再作为动作出现在动作池里。</item>
/// <item>v2.3.3：<c>macro.recordToggle</c>（录制宏）、<c>macro.manage</c>（宏管理）→ 属于创作 / 管理入口
/// （托盘 / 动作编辑器 / 动作面板左栏；宏管理入口已彻底移除），不是可重复执行的动作。</item>
/// </list>
/// </remarks>
internal static class ObsoleteActionCleanup
{
    private static readonly string[] ObsoleteCommands =
        ["macro.playLast", "settings.toggle", "helpPanel.toggle", "macro.recordToggle", "macro.manage"];

    /// <summary>扫一遍动作清单，删掉过期动作与其内嵌引用；有改动才落盘。</summary>
    public static void Run()
    {
        var stale = ActionRegistry.All
            .Where(a => IsObsolete(a))
            .Select(a => a.Id)
            .ToList();

        // 组合步骤是内嵌快照，删动作不会自动清；单独扫一遍，把过期步骤移除。
        bool stepChanged = false;
        foreach (var a in ActionRegistry.All)
        {
            if (a.Type != ActionType.composite) continue;
            if (a.Steps != null)
                stepChanged |= a.Steps.RemoveAll(s => IsObsolete(s.Action)) > 0;
            if (a.Flow != null)
                stepChanged |= RemoveObsoleteNodes(a.Flow);
        }

        if (stale.Count == 0 && !stepChanged) return;

        foreach (string id in stale) ConfigStore.RemoveAction(id);
        ConfigStore.Save();
    }

    private static bool IsObsolete(ActionDto a) =>
        a.Type == ActionType.@internal && a.Command != null && ObsoleteCommands.Contains(a.Command);

    private static bool RemoveObsoleteNodes(List<FlowNode> nodes)
    {
        bool changed = false;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (n.Action != null && IsObsolete(n.Action))
            {
                nodes.RemoveAt(i);
                changed = true;
                continue;
            }
            if (n.If != null)
            {
                changed |= RemoveObsoleteNodes(n.If.Then);
                if (n.If.Else != null) changed |= RemoveObsoleteNodes(n.If.Else);
            }
            if (n.Loop != null) changed |= RemoveObsoleteNodes(n.Loop.Body);
        }
        return changed;
    }
}
