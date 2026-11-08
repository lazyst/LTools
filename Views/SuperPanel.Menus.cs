using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 菜单 / 磁贴 CRUD（全部先关面板再开对话框）
/// </summary>
public partial class SuperPanelWindow
{
    // ============ 菜单 ============

    private ContextMenu BuildFilledMenu(PanelTileRef tref)
    {
        string id = tref.ActionId!;
        var menu = new ContextMenu();
        AddMenuItem(menu, "编辑…", () => EditAction(tref));
        AddMenuItem(menu, "复制", () => DuplicateAction(tref));
        // 照 Quicker「以暂存区方式编辑」：正式动作只读，先复制一份到暂存区再改
        AddMenuItem(menu, "在暂存区编辑…", () => EditInStash(id));
        AddMenuItem(menu, "导出动作…", () => ExportAction(tref));
        menu.Items.Add(new Separator());
        bool floating = FloatingLibrary.IsFloating(id);
        string label = !floating ? "悬浮此动作"
            : FloatingManager.IsOnDesktop(id) ? "取消悬浮" : "显示悬浮";
        AddMenuItem(menu, label, () => ToggleFloat(id));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "从此组移除", () => RemoveFromGroup(tref));
        AddMenuItem(menu, "删除动作…", () => DeleteAction(tref.ActionId!));
        return menu;
    }

    /// <summary>
    /// 悬浮 / 取消悬浮一个动作。悬浮项**全局常驻**（不绑进程），所以这里不需要知道前台程序。
    /// **面板不关**（照 Quicker）：可以接着悬浮下一个动作，不用重新唤面板。
    /// </summary>
    private void ToggleFloat(string actionId) => FloatingManager.ToggleAction(actionId);

    /// <summary>「悬浮此分组」：把整组动作一起放上桌面（面板同样不关）。</summary>
    private void FloatGroupToDesktop(PanelGroupDto group) => FloatingManager.FloatGroup(group);

    private ContextMenu BuildMissingMenu(PanelTileRef tref)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "从此组移除", () => RemoveFromGroup(tref));
        return menu;
    }

    private void ShowAddMenu(PanelGroupDto group, UIElement? target)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "新建动作…", () => NewActionFor(group));
        AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(group));
        TrackInteract(menu);
        menu.PlacementTarget = target;
        menu.Placement = target != null ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu NewTrackedMenu()
    {
        var menu = new ContextMenu();
        TrackInteract(menu);
        return menu;
    }

    /// <summary>分组菜单（分组标题 / 左缘导航条）：新建动作、重命名、删除、去设置里管理。</summary>
    private ContextMenu BuildGroupMenu(PanelGroupDto group, bool global)
    {
        var menu = NewTrackedMenu();
        AddMenuItem(menu, "在此组新建动作…", () => NewActionFor(group));
        AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(group));
        menu.Items.Add(new Separator());
        AddFloatGroupItem(menu, group);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "重命名分组…", () => BeginRenameGroup(group, global));
        AddMenuItem(menu, "删除分组…", () => DeleteGroupFromPanel(group, global));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "在设置中管理…", () => OpenSceneSettings(group, global));
        return menu;
    }

    /// <summary>分组菜单里的「悬浮此分组」（分组为空或动作都已在桌面时置灰）。</summary>
    private void AddFloatGroupItem(ContextMenu menu, PanelGroupDto group)
    {
        var item = new MenuItem
        {
            Header = "悬浮此分组",
            IsEnabled = group.Actions.Count > 0 && FloatingManager.CanFloatGroup(group),
            ToolTip = "把这一组的动作放到桌面上（已在桌面上的动作会跳过）",
        };
        item.Click += (_, _) => FloatGroupToDesktop(group);
        menu.Items.Add(item);
    }

    /// <summary>
    /// 面板空白处菜单（照 Quicker）：新建动作 / 新建分组 / 管理场景，外加钉住、设置、关闭。
    /// 光标在某个分组里时，新建动作直接落到那个分组。
    /// </summary>
    private void ShowZoneMenu(bool global, PanelGroupDto? group)
    {
        var menu = NewTrackedMenu();
        var target = group ?? ZoneGroups(global).FirstOrDefault();
        if (target != null)
        {
            AddMenuItem(menu, "在此组新建动作…", () => NewActionFor(target));
            AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(target));
            menu.Items.Add(new Separator());
        }

        if (group != null)
        {
            AddFloatGroupItem(menu, group);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "重命名分组…", () => BeginRenameGroup(group, global));
            AddMenuItem(menu, "删除分组…", () => DeleteGroupFromPanel(group, global));
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, "新建分组", () => AddGroupToZone(global));
        AddMenuItem(menu, "在设置中管理…", () => OpenSceneSettings(group, global));
        menu.Items.Add(new Separator());
        AddPanelCommands(menu);

        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>标题栏 / 左栏空白：只放面板级操作。</summary>
    private void ShowPanelMenu()
    {
        var menu = NewTrackedMenu();
        AddPanelCommands(menu);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void AddPanelCommands(ContextMenu menu)
    {
        var pin = new MenuItem
        {
            Header = "钉住面板",
            IsCheckable = true,
            IsChecked = _pinned,
            ToolTip = "钉住后点击外部不关闭",
        };
        pin.Click += (_, _) => Defer(() => SetPinned(!_pinned));
        menu.Items.Add(pin);
        AddMenuItem(menu, "面板设置…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(PanelLibrary.ActiveGlobalScene()?.Id);
        });
        AddMenuItem(menu, "关闭面板", () => Features.SuperPanel.Close());
    }

    private ContextMenu BuildGlobalSceneMenu(PanelSceneDto scene)
    {
        var menu = NewTrackedMenu();
        AddMenuItem(menu, "编辑场景…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(scene.Id);
        });
        AddMenuItem(menu, "新建全局场景", NewGlobalSceneFromPanel);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "删除场景…", () => DeleteSceneFromPanel(scene));
        return menu;
    }

    private ContextMenu BuildContextSceneMenu(PanelSceneDto? scene)
    {
        var menu = NewTrackedMenu();
        if (scene == null)
        {
            AddMenuItem(menu, "编辑通用分组…", () =>
            {
                Features.SuperPanel.Close();
                ConfigHelper.OpenPanelScene(PanelManageView.CommonKey);
            });
            AddMenuItem(menu, "新建上下文场景", NewContextSceneFromPanel);
            return menu;
        }

        AddMenuItem(menu, "编辑场景…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(scene.Id);
        });
        AddMenuItem(menu, "新建上下文场景", NewContextSceneFromPanel);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "改为显示通用", () =>
        {
            _contextSceneId = null;
            BuildContext();
            ContextScroll.ScrollToTop();
        });
        AddMenuItem(menu, "删除场景…", () => DeleteSceneFromPanel(scene));
        return menu;
    }

    private void NewGlobalSceneFromPanel()
    {
        var scene = PanelLibrary.NewScene("新场景");
        PanelLibrary.Current.Global.Scenes.Add(scene);
        PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
        PanelLibrary.Save();
        RebuildAll();
    }

    private void NewContextSceneFromPanel()
    {
        var scene = _foreground != null
            ? PanelLibrary.CreateContextScene(_foreground.ExeName, _foreground.ExeName, _foreground.ExePath)
            : PanelLibrary.CreateContextScene("新程序", null, null);
        _contextSceneId = scene.Id;
        RebuildAll();
    }

    private void DeleteSceneFromPanel(PanelSceneDto scene)
    {
        bool children = PanelLibrary.ChildrenOf(scene.Id).Any();
        string extra = children ? "\n它的窗口子场景也会一并删除。" : "";
        if (!ConfirmDialog.Confirm(this, "删除场景",
                $"确定删除场景「{scene.Name}」吗？{extra}\n动作本体仍保留在动作池。",
                danger: true))
            return;

        PanelLibrary.DeleteScene(scene);
        if (_contextSceneId == scene.Id) _contextSceneId = null;
        RebuildAll();
    }

    private void DeleteGroupFromPanel(PanelGroupDto group, bool global)
    {
        var groups = ZoneGroups(global);
        if (groups.Count <= 1)
        {
            ToastService.Show("每个场景至少保留一个分组");
            return;
        }
        if (!ConfirmDialog.Confirm(this, "删除分组",
                $"确定删除分组「{group.Name}」吗？\n里面的动作只是从分组移除，不会删除动作本体。",
                danger: true))
            return;

        groups.Remove(group);
        PanelLibrary.Save();
        RebuildAll();
    }

    private static PanelSceneDto? SceneOfGroup(PanelGroupDto group) =>
        PanelLibrary.Current.Global.Scenes.Concat(PanelLibrary.Current.Context.Scenes)
            .FirstOrDefault(s => s.Groups.Contains(group));

    /// <summary>去设置里管理：优先定位到分组所属场景；通用 / 找不到场景时定位到「通用」。</summary>
    private void OpenSceneSettings(PanelGroupDto? group, bool global)
    {
        Features.SuperPanel.Close();
        var scene = group != null ? SceneOfGroup(group)
            : global ? PanelLibrary.ActiveGlobalScene() : GetContextScene();
        ConfigHelper.OpenPanelScene(scene?.Id ?? PanelManageView.CommonKey);
    }

    /// <summary>分组名原地改名：临时插一个输入框，Enter 保存、Esc 取消、失焦也保存。</summary>
    private void BeginRenameGroup(PanelGroupDto group, bool global)
    {
        var host = global ? GlobalGroups : ContextGroups;
        var block = host.Children.OfType<StackPanel>()
            .FirstOrDefault(p => ReferenceEquals(p.Tag, group));
        if (block == null) return;

        var title = block.Children.OfType<TextBlock>().FirstOrDefault();
        if (title != null) title.Visibility = Visibility.Collapsed;

        var box = new TextBox
        {
            Text = group.Name,
            MinHeight = 26,
            FontSize = (double)FindResource("FontSizeSecondary"),
            Margin = new Thickness(0, 2, 0, 2),
            Tag = "GroupRename",
        };
        block.Children.Insert(0, box);

        bool done = false;
        void Commit()
        {
            if (done) return;
            done = true;
            string v = box.Text.Trim();
            if (v.Length > 0 && v != group.Name)
            {
                group.Name = v;
                PanelLibrary.Save();
            }
            RebuildAll();
        }
        void Cancel()
        {
            if (done) return;
            done = true;
            RebuildAll();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    private void AddMenuItem(ContextMenu menu, string header, Action invoke)
    {
        var mi = new MenuItem { Header = header };
        // 菜单项 Click 内同步打开模态框会与菜单关闭冲突，推迟到下一 Dispatcher 周期
        mi.Click += (_, _) => Defer(invoke);
        menu.Items.Add(mi);
    }

    private void TrackInteract(ContextMenu menu)
    {
        menu.Opened += (_, _) => { _openMenu = menu; _interactCount++; };
        menu.Closed += (_, _) =>
        {
            if (_openMenu == menu) _openMenu = null;
            if (_interactCount > 0) _interactCount--;
            // 菜单关掉后焦点回到面板，Windows 可能顺手把面板提到最前；悬浮项要一直盖在面板上面。
            FloatingManager.RaiseAllAbove();
        };
    }

    /// <summary>
    /// 点是否落在当前打开的右键菜单里（**物理屏幕像素**，直接跟钩子给的坐标比）。
    /// </summary>
    /// <remarks>
    /// 菜单浮在面板**外面**，只按面板矩形判断会把它当成「点面板外部」——钩子在那一步就把面板关了，
    /// 菜单项的命令还会连带一起被销毁。所以钩子判「点外面」时必须把菜单（含已展开的子菜单）排除掉。
    /// 子菜单是另一个 popup 窗口，只查根菜单矩形不够。
    /// </remarks>
    internal bool MenuContains(int screenX, int screenY)
    {
        var menu = _openMenu;
        if (menu == null || !menu.IsOpen) return false;
        try
        {
            if (ContainsScreenPoint(menu, screenX, screenY)) return true;

            foreach (var item in MenuDescendants(menu.Items))
            {
                if (!item.IsSubmenuOpen) continue;
                if (item.Template?.FindName("PART_Popup", item) is Popup { Child: Visual child }
                    && ContainsScreenPoint(child, screenX, screenY))
                {
                    return true;
                }
            }
        }
        catch { /* 拿不到矩形就当不在菜单里 */ }
        return false;
    }

    private static IEnumerable<MenuItem> MenuDescendants(ItemCollection items)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in MenuDescendants(item.Items)) yield return child;
        }
    }

    private static bool ContainsScreenPoint(Visual visual, int screenX, int screenY)
    {
        if (PresentationSource.FromVisual(visual) is not System.Windows.Interop.HwndSource source
            || source.Handle == IntPtr.Zero)
            return false;
        if (!Win32.GetWindowRect(source.Handle, out Win32.Rect rect)) return false;
        return screenX >= rect.Left && screenX < rect.Right
            && screenY >= rect.Top && screenY < rect.Bottom;
    }

    /// <summary>菜单在场时点击面板其它位置：只关菜单，**这一下点击照常交给面板内容**（不再吞）。</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool hadMenu = _openMenu != null;
        if (hadMenu)
        {
            var m = _openMenu!;
            _openMenu = null;
            if (m.IsOpen) m.IsOpen = false;
        }

        // 空白处按下：记住起点，移动超过 4px 才拖窗口（磁贴 / 按钮等交互元素不参与）
        if (!hadMenu && e.ChangedButton == MouseButton.Left && !IsInteractive(e.OriginalSource as DependencyObject))
        {
            _blankDragStart = e.GetPosition(this);
            _blankDragArmed = true;
        }

        // 照 Quicker：中键点空白处关闭面板
        if (!hadMenu && e.ChangedButton == MouseButton.Middle && !IsInteractive(e.OriginalSource as DependencyObject))
        {
            Features.SuperPanel.Close();
            e.Handled = true;
        }
    }

    /// <summary>交互控件（按钮 / 输入框 / 滚动条 / 分割条）上的拖动归控件自己。</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase or TextBoxBase or ScrollBar or GridSplitter or Thumb
                or ComboBox or Slider or MenuItem)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Defer(Action action) => Dispatcher.InvokeAsync(action);

    // ============ 磁贴 CRUD（全部先关面板再开对话框） ============

    private void NewActionFor(PanelGroupDto group)
    {
        Features.SuperPanel.Close();
        PanelActionOps.AddNew(null, group);
    }

    private void PickExistingFor(PanelGroupDto group)
    {
        Features.SuperPanel.Close();
        PanelActionOps.AddExisting(null, group);
    }

    private void EditAction(PanelTileRef tref)
    {
        if (tref.ActionId == null) return;
        Features.SuperPanel.Close();
        if (PanelActionOps.Edit(null, tref.ActionId)) RebuildAll();
    }

    /// <summary>
    /// 照 Quicker「以暂存区方式编辑」：把正式动作复制成一份草稿（正式动作只读），
    /// 切到暂存区页继续改；改完由用户决定「覆盖原动作」还是「另存为新动作」。
    /// </summary>
    private void EditInStash(string actionId)
    {
        var action = ActionRegistry.FindById(actionId);
        if (action == null) return;

        try
        {
            DraftStore.Create(action.Name, action.Program?.Description, actionId, "user");
            ShowStashPage();
            ToastService.Show("已复制到暂存区；改完再决定是否覆盖原动作", action.Name, 3000, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, "复制到暂存区失败", 4000, ToastKind.error);
        }
    }

    private void DuplicateAction(PanelTileRef tref)
    {
        if (tref.ActionId == null) return;
        var copy = ConfigStore.DuplicateAction(tref.ActionId);
        if (copy == null) return;
        ConfigStore.Save();
        int insert = Math.Min(tref.Index + 1, tref.Group.Actions.Count);
        tref.Group.Actions.Insert(insert, copy.Id);
        PanelLibrary.Save();
        RebuildAll();
    }

    private void RemoveFromGroup(PanelTileRef tref)
    {
        PanelActionOps.RemoveFromGroup(tref.Group, tref.Index);
        RebuildAll();
    }

    private void DeleteAction(string id)
    {
        Features.SuperPanel.Close();
        PanelActionOps.Delete(null, id);
    }

    /// <summary>导出一个动作（<c>.ltact</c>）：先关面板再开保存对话框（隐藏的 Topmost 面板会让对话框闪黑）。</summary>
    private void ExportAction(PanelTileRef tref)
    {
        if (tref.ActionId == null) return;
        Features.SuperPanel.Close();
        PanelActionOps.Export(null, tref.ActionId);
    }
}
