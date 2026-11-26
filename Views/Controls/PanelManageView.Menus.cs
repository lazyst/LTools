using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>PanelManageView 的「右键菜单」部分。</summary>
public partial class PanelManageView : UserControl
{
    // ============ 右键菜单 ============

    private static ContextMenu Menu(params (string Header, Action? Invoke)[] items)
    {
        var menu = new ContextMenu();
        AddMenuItems(menu, items);
        return menu;
    }

    /// <summary>照 <see cref="Menu"/> 的写法往现成菜单里追加（中间要插「粘贴」时用）。</summary>
    private static void AddMenuItems(ItemsControl menu, params (string Header, Action? Invoke)[] items)
    {
        foreach (var (header, invoke) in items)
        {
            if (header == "-") { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = header };
            if (invoke != null)
                mi.Click += (_, _) => Application.Current.Dispatcher.InvokeAsync(invoke);
            else
                mi.IsEnabled = false;
            menu.Items.Add(mi);
        }
    }

    private ContextMenu BuildSceneListBlankMenu() => Menu(
        ("新建全局场景", () => CreateScene(global: true)),
        ("新建上下文场景", () => CreateScene(global: false)),
        ("新建窗口子场景", NewChildSceneForSelection));

    private ContextMenu BuildDetailBlankMenu()
    {
        var menu = new ContextMenu();
        var groups = GroupsOfSelectedScene();
        // 详情页空白处：默认落到本场景的第一个分组（与「新建组合动作…」同一个落点）
        if (groups.Count > 0) AddSimpleMenu(menu, groups[0]);

        AddMenuItems(menu,
            ("新建组合动作…", () => NewActionForSelectedScene()),
            ("新建分组", AddGroupToSelectedScene));
        PanelPasteMenu.Append(menu, groups.Count > 0 ? groups[0] : null, PasteIntoGroup);
        return menu;
    }

    /// <summary>「新建简单动作 ▸」：按预设建一个一步动作，放进这个分组。</summary>
    private void AddSimpleMenu(ItemsControl menu, PanelGroupDto group)
    {
        var simple = MenuFx.Submenu(menu, "新建简单动作");
        foreach (var kind in SimpleActions.Catalog)
        {
            var item = new MenuItem { Header = kind.Name, ToolTip = kind.Description };
            SimpleActions.Kind captured = kind;
            item.Click += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (PanelActionOps.AddSimple(Window.GetWindow(this), group, captured) != null) RefreshAll();
            });
            simple.Items.Add(item);
        }
    }

    private ContextMenu BuildSceneRowMenu(PanelSceneDto scene, bool isGlobal)
    {
        var items = new List<(string, Action?)>
        {
            ("重命名场景", () => RenameScene(scene)),
            ("新建组合动作…", () => NewActionForScene(scene)),
            ("新建分组", () => AddGroupToScene(scene)),
            ("-", null),
        };
        if (!isGlobal) items.Add(("新建窗口子场景", () => CreateChildScene(scene)));
        items.Add(("-", null));
        items.Add(("删除场景…", () => DeleteScene(scene)));
        return Menu(items.ToArray());
    }

    private ContextMenu BuildGroupMenu(PanelGroupDto group, List<PanelGroupDto> groups)
    {
        var menu = new ContextMenu();
        AddMenuItems(menu, ("重命名分组", () => RenameGroup(group)));
        AddSimpleMenu(menu, group);
        AddMenuItems(menu,
            ("新建组合动作…", () => AddActionToGroup(group)),
            ("从动作池选择…", () => AddExistingToGroup(group)));
        PanelPasteMenu.Append(menu, group, PasteIntoGroup);
        menu.Items.Add(new Separator());
        AddMenuItems(menu, ("删除分组…", () => DeleteGroup(groups, group)));
        return menu;
    }

    /// <summary>粘贴进某个分组（索引 -1 = 末尾）；粘完就地刷新场景页。</summary>
    private void PasteIntoGroup(PanelGroupDto group, int index)
    {
        if (PanelActionOps.Paste(group, index)) RefreshAll();
    }

    private void RenameScene(PanelSceneDto scene)
    {
        _selectedKey = scene.Id;
        _rulesExpanded = false;
        BuildSceneList();
        RenderDetail();
        Dispatcher.InvokeAsync(() =>
        {
            _sceneNameBox?.Focus();
            _sceneNameBox?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void RenameGroup(PanelGroupDto group)
    {
        if (!_groupNameBoxes.TryGetValue(group, out var box)) return;
        box.Focus();
        box.SelectAll();
    }

    private void NewChildSceneForSelection()
    {
        var selected = PanelLibrary.FindScene(_selectedKey);
        if (selected != null && !PanelLibrary.Current.Global.Scenes.Contains(selected))
            CreateChildScene(selected);
        else
            SetStatus("请先在左侧选中一个上下文场景", ok: false);
    }

    private void CreateScene(bool global)
    {
        if (global)
        {
            var scene = PanelLibrary.NewScene("新场景");
            PanelLibrary.Current.Global.Scenes.Add(scene);
            PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
            PanelLibrary.Save();
            _selectedKey = scene.Id;
            _rulesExpanded = false;
            SetStatus("已新建全局场景，可在右侧改名", ok: true);
        }
        else
        {
            var scene = PanelLibrary.NewScene("新程序");
            PanelLibrary.Current.Context.Scenes.Add(scene);
            PanelLibrary.Save();
            _selectedKey = scene.Id;
            _rulesExpanded = true;
            SetStatus("已新建上下文场景，请在右侧填写匹配条件", ok: true);
        }
        BuildSceneList();
        RenderDetail();
    }

    private void CreateChildScene(PanelSceneDto parent)
    {
        if (!string.IsNullOrEmpty(parent.ParentId))
            parent = PanelLibrary.FindScene(parent.ParentId!) ?? parent;

        var child = PanelLibrary.NewScene("新窗口");
        child.ParentId = parent.Id;
        child.Groups[0].Name = "常用";
        PanelLibrary.Current.Context.Scenes.Add(child);
        PanelLibrary.Save();
        _selectedKey = child.Id;
        _rulesExpanded = true;
        BuildSceneList();
        RenderDetail();
        SetStatus($"已在「{parent.Name}」下新建窗口子场景", ok: true);
    }

    private void NewActionForSelectedScene()
    {
        var groups = GroupsOfSelectedScene();
        if (groups.Count == 0) return;
        AddActionToGroup(groups[0]);
    }

    private void NewActionForScene(PanelSceneDto scene)
    {
        if (scene.Groups.Count == 0) return;
        AddActionToGroup(scene.Groups[0]);
    }

    private void AddGroupToSelectedScene()
    {
        var groups = GroupsOfSelectedScene();
        if (groups.Count == 0) return;
        AddGroupToScene(groups);
    }

    private void AddGroupToScene(PanelSceneDto scene) => AddGroupToScene(scene.Groups);

    private void AddGroupToScene(List<PanelGroupDto> groups)
    {
        groups.Add(PanelLibrary.NewGroup("新分组"));
        PanelLibrary.Save();
        RenderDetail();
    }

    private List<PanelGroupDto> GroupsOfSelectedScene()
    {
        if (_selectedKey == CommonKey) return PanelLibrary.Current.Context.Common;
        return PanelLibrary.FindScene(_selectedKey)?.Groups ?? PanelLibrary.Current.Context.Common;
    }

    private void AddActionToGroup(PanelGroupDto group)
    {
        if (PanelActionOps.AddNew(Window.GetWindow(this), group) != null) RefreshAll();
    }

    private void AddExistingToGroup(PanelGroupDto group)
    {
        if (PanelActionOps.AddExisting(Window.GetWindow(this), group)) RefreshAll();
    }

}
