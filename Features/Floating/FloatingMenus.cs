using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Config;
using LTools.Core;
using LTools.Features.Builtin;
using LTools.Features.Panel;
using LTools.Views;
using LTools.Views.Controls;

namespace LTools.Features.Floating;

/// <summary>悬浮项的右键菜单（运行 / 编辑 / 锁定 / 大小 / 方向 / 透明度 / 拆分 / 关闭）。</summary>
/// <remarks>
/// <para>
/// 菜单只在右键那一刻现建（<see cref="FloatWindow"/> 每次右键都调用 <see cref="Build"/>），
/// 所以勾选状态永远和当前状态一致，不需要额外的刷新逻辑。
/// </para>
/// <para>
/// **带子菜单的项必须能点开**：WPF 的 <see cref="MenuItem"/> 默认只在悬停时展开子菜单，
/// 点一下没有任何反应——用户会以为「菜单点不动」。所以所有父项都走 <see cref="MenuFx.Submenu"/>，
/// 挂上 <c>Click → IsSubmenuOpen = true</c>。
/// </para>
/// </remarks>
internal static class FloatingMenus
{
    private static readonly double[] Sizes = { 36, 44, 52, 64 };
    private static readonly double[] Opacities = { 1.0, 0.8, 0.6 };

    public static ContextMenu Build(FloatingItemDto item)
    {
        var menu = new ContextMenu();
        bool group = item.Members.Count > 1;

        if (group)
        {
            Add(menu, $"悬浮分组「{(string.IsNullOrWhiteSpace(item.Name) ? "未命名" : item.Name)}」· {item.Members.Count} 个动作", null);
            var run = MenuFx.Submenu(menu, "运行");
            foreach (var member in item.Members)
                AddItem(run, BindingRef.Describe(member.ActionId), () => Run(member.ActionId));
        }
        else
        {
            string name = BindingRef.Describe(item.Members[0].ActionId);
            Add(menu, $"运行「{name}」", () => Run(item.Members[0].ActionId));
        }

        // 内置功能不是动作：只给「参数…」，不给「编辑动作」
        var actionMembers = item.Members
            .Where(m => !BindingRef.IsFeature(m.ActionId))
            .ToList();
        if (actionMembers.Count > 0)
        {
            var edit = MenuFx.Submenu(menu, "编辑动作…");
            foreach (var member in actionMembers)
                AddItem(edit, BindingRef.Name(member.ActionId), () => EditAction(member.ActionId));
        }

        var paramMembers = item.Members
            .Where(m => FeatureCatalog.Find(FeatureRef.IdOf(m.ActionId)) is { HasParams: true })
            .ToList();
        if (paramMembers.Count > 0)
        {
            var param = MenuFx.Submenu(menu, "功能参数…");
            foreach (var member in paramMembers)
            {
                string id = member.ActionId;
                AddItem(param, BindingRef.Describe(id), () => EditFeatureParams(item, id));
            }
        }

        menu.Items.Add(new Separator());

        var locked = new MenuItem
        {
            Header = "锁定位置",
            IsCheckable = true,
            IsChecked = item.PositionLocked,
            ToolTip = "锁定后拖不动，避免误触",
        };
        locked.Click += (_, _) =>
        {
            item.PositionLocked = !item.PositionLocked;
            AppLog.Info("Floating", $"锁定位置 = {item.PositionLocked}（{item.Id}）");
            ToastService.Show(item.PositionLocked ? "已锁定位置，拖动不会移动" : "已解锁位置，可以拖动了");
        };
        menu.Items.Add(locked);

        var size = MenuFx.Submenu(menu, "按钮大小");
        foreach (double value in Sizes)
        {
            double v = value;
            var mi = AddItem(size, $"{v:0} px", () => SetSize(item, v));
            mi.IsCheckable = true;
            mi.IsChecked = Math.Abs(item.Size - v) < 0.5;
        }

        var opacity = MenuFx.Submenu(menu, "不透明度");
        foreach (double value in Opacities)
        {
            double v = value;
            var mi = AddItem(opacity, $"{v * 100:0}%", () => SetOpacity(item, v));
            mi.IsCheckable = true;
            mi.IsChecked = Math.Abs(item.Opacity - v) < 0.02;
        }

        if (group)
        {
            // 方向只对分组有意义（单个按钮横竖一样）
            var direction = MenuFx.Submenu(menu, "方向");
            var horizontal = AddItem(direction, "横向一排", () => SetVertical(item, false));
            horizontal.IsCheckable = true;
            horizontal.IsChecked = !item.Vertical;
            var vertical = AddItem(direction, "竖向一列", () => SetVertical(item, true));
            vertical.IsCheckable = true;
            vertical.IsChecked = item.Vertical;
        }

        menu.Items.Add(new Separator());

        if (group)
        {
            // 「该动作」= 子菜单里挑一个；「全部」= 一次全拆成独立项
            var one = MenuFx.Submenu(menu, "拆分为独立悬浮（该动作）");
            foreach (var member in item.Members)
            {
                string id = member.ActionId;
                string name = BindingRef.Name(id);
                AddItem(one, name, () => SplitOne(item, id, name));
            }

            Add(menu, "拆分为独立悬浮（全部）", () => SplitAll(item));
            Add(menu, "关闭此分组", () => FloatingManager.Close(item.Id));
        }
        else
        {
            Add(menu, "关闭悬浮", () => FloatingManager.Close(item.Id));
        }

        Add(menu, "关闭全部悬浮", FloatingManager.CloseAll);
        return menu;
    }

    private static void Add(ContextMenu menu, string header, Action? onClick)
    {
        var mi = new MenuItem { Header = header };
        if (onClick == null) mi.IsEnabled = false;
        else mi.Click += (_, _) => onClick();
        menu.Items.Add(mi);
    }

    private static MenuItem AddItem(ItemsControl parent, string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        parent.Items.Add(mi);
        return mi;
    }

    private static void Run(string actionId)
    {
        if (!BindingRef.Exists(actionId))
        {
            ToastService.Show("动作 / 功能不存在：" + actionId);
            FloatingManager.RefreshAll();
            return;
        }
        AppLog.Info("Floating", $"菜单运行：{BindingRef.Describe(actionId)}");
        ActionExecutor.RunRef(actionId, 0, 0);
    }

    private static void EditAction(string actionId)
    {
        if (!PanelActionOps.Edit(null, actionId)) return;
        FloatingManager.RefreshAll();
    }

    /// <summary>改悬浮按钮上这一处引用的参数（同一个功能可以在别处用别的参数）。</summary>
    private static void EditFeatureParams(FloatingItemDto item, string reference)
    {
        string? updated = FeatureParamsDialog.Show(null, reference);
        if (string.IsNullOrEmpty(updated) || updated == reference) return;

        foreach (var member in item.Members)
        {
            if (member.ActionId == reference) member.ActionId = updated;
        }
        FloatingManager.RefreshItem(item.Id);
    }

    private static void SetSize(FloatingItemDto item, double size)
    {
        item.Size = Math.Clamp(size, 28, 96);
        FloatingManager.RefreshItem(item.Id);
        ToastService.Show($"按钮大小：{item.Size:0} px");
    }

    private static void SetOpacity(FloatingItemDto item, double opacity)
    {
        item.Opacity = Math.Clamp(opacity, 0.3, 1);
        FloatingManager.RefreshItem(item.Id);
        ToastService.Show($"不透明度：{item.Opacity * 100:0}%");
    }

    private static void SetVertical(FloatingItemDto item, bool vertical)
    {
        if (item.Vertical == vertical) return;
        item.Vertical = vertical;
        FloatingManager.RefreshItem(item.Id);
        ToastService.Show(vertical ? "已改为竖排" : "已改为横排");
    }

    /// <summary>拆出指定动作（分组只剩一个成员时自动退化成单个悬浮项）。</summary>
    private static void SplitOne(FloatingItemDto item, string actionId, string name)
    {
        var single = FloatingLibrary.SplitMember(item.Id, actionId);
        if (single == null) return;

        FloatingManager.RefreshItem(item.Id);
        FloatingManager.RefreshItem(single.Id);
        ToastService.Show($"已把「{name}」拆成独立悬浮");
    }

    /// <summary>整组拆成独立悬浮项（成员按分组方向依次排开）。</summary>
    private static void SplitAll(FloatingItemDto item)
    {
        var singles = FloatingLibrary.Split(item.Id);
        if (singles.Count == 0) return;

        FloatingManager.Close(item.Id);
        foreach (var single in singles) FloatingManager.RefreshItem(single.Id);
        ToastService.Show($"已拆分为 {singles.Count} 个独立悬浮项");
    }
}
