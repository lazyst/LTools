using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using LTools.Config;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>
/// 面板里的「粘贴 / 粘贴到…」菜单项：超级面板与设置 → 场景页共用这一份。
/// </summary>
/// <remarks>
/// <para>
/// 剪贴板（<see cref="ActionClipboard"/>）为空时**一项都不加**——菜单是每次现建的，
/// 不留灰掉的「粘贴」，否则每个分组菜单里都挂着一行永远点不动的项。
/// </para>
/// <para>
/// 「粘贴到…」是两级菜单（<b>区域 › 场景 › 分组</b>，上下文子场景再嵌一层），
/// 跨场景粘贴不用先切过去；两边都走 <see cref="MenuFx.Submenu"/>，所以悬停用的是同一套预测锥。
/// </para>
/// </remarks>
internal static class PanelPasteMenu
{
    /// <summary>
    /// 把「粘贴」与「粘贴到…」追加到菜单末尾。
    /// </summary>
    /// <param name="menu">目标菜单（分组菜单 / 空白处菜单 / 磁贴菜单都可以）。</param>
    /// <param name="current">「粘贴」直接落进去的分组；null = 这一处没有「当前分组」，第一项置灰。</param>
    /// <param name="paste">真正去粘的回调：参数是目标分组与插入位置（-1 = 末尾）。</param>
    /// <param name="index">插到目标分组的第几颗前面（磁贴菜单传「这一颗之后」）；-1 = 末尾。</param>
    /// <returns>是否加过菜单项（剪贴板为空返回 false）。</returns>
    public static bool Append(ItemsControl menu, PanelGroupDto? current, Action<PanelGroupDto, int> paste,
        int index = -1)
    {
        if (!ActionClipboard.Has) return false;
        var entry = ActionClipboard.Entry!;

        var here = new MenuItem
        {
            Header = entry.PasteLabel,
            IsEnabled = current != null,
            ToolTip = current != null
                ? $"粘到「{current.Name}」"
                : "这里没有可粘的分组，用下面的「粘贴到…」选一个",
        };
        if (current != null) here.Click += (_, _) => paste(current, index);
        menu.Items.Add(here);

        var root = MenuFx.Submenu(menu, "粘贴到…");
        var panel = PanelLibrary.Current;
        AddArea(root, "全局", panel.Global.Scenes, paste);
        AddArea(root, "上下文", panel.Context.Scenes, paste);
        if (panel.Context.Common.Count > 0)
        {
            var common = MenuFx.Submenu(root, "上下文 · 通用");
            foreach (var group in panel.Context.Common) AddGroup(common, group, "通用", paste);
        }

        // 一个分组都没有（全新配置、场景被清空）：别留一个点开是空的「粘贴到…」
        if (root.Items.Count == 0) menu.Items.Remove(root);
        return true;
    }

    /// <summary>一个区域（全局 / 上下文）的分支；没有可粘的场景时整支抽掉。</summary>
    private static void AddArea(ItemsControl parent, string header, IReadOnlyList<PanelSceneDto> scenes,
        Action<PanelGroupDto, int> paste)
    {
        var area = MenuFx.Submenu(parent, header);
        foreach (var scene in scenes.Where(s => s.ParentId == null))
            AddScene(area, scene, scenes, paste);
        if (area.Items.Count == 0) parent.Items.Remove(area);
    }

    /// <summary>一个场景的分支：自己的分组 + 它的窗口子场景（照面板设置页的场景树）。</summary>
    private static void AddScene(ItemsControl parent, PanelSceneDto scene, IReadOnlyList<PanelSceneDto> all,
        Action<PanelGroupDto, int> paste)
    {
        var children = all.Where(s => s.ParentId == scene.Id).ToList();
        if (scene.Groups.Count == 0 && children.Count == 0) return;

        var node = MenuFx.Submenu(parent, scene.Name);
        foreach (var group in scene.Groups) AddGroup(node, group, scene.Name, paste);
        foreach (var child in children) AddScene(node, child, all, paste);
    }

    private static void AddGroup(ItemsControl parent, PanelGroupDto group, string sceneName,
        Action<PanelGroupDto, int> paste)
    {
        var item = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(group.Name) ? "未分组" : group.Name,
            ToolTip = $"{sceneName} · {group.Name}",
        };
        item.Click += (_, _) => paste(group, -1);
        parent.Items.Add(item);
    }
}
