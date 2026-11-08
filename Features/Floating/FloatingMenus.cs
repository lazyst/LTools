using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features.Panel;

namespace LTools.Features.Floating;

/// <summary>悬浮项的右键菜单（运行 / 编辑 / 锁定 / 大小 / 方向 / 透明度 / 拆分 / 关闭）。</summary>
/// <remarks>
/// <para>
/// 菜单只在右键那一刻现建（<see cref="FloatWindow"/> 每次右键都调用 <see cref="Build"/>），
/// 所以勾选状态永远和当前状态一致，不需要额外的刷新逻辑。
/// </para>
/// <para>
/// **带子菜单的项必须能点开**：WPF 的 <see cref="MenuItem"/> 默认只在悬停时展开子菜单，
/// 点一下没有任何反应——用户会以为「菜单点不动」。所以所有父项都走 <see cref="Submenu"/>，
/// 挂上 <c>Click → IsSubmenuOpen = true</c>。
/// </para>
/// </remarks>
internal static class FloatingMenus
{
    private static readonly double[] Sizes = { 36, 44, 52, 64 };
    private static readonly double[] Opacities = { 1.0, 0.8, 0.6 };

    /// <summary>
    /// 悬停多久展开子菜单（毫秒）。0 = 立即展开；调到 60~100 可以过滤「扫过去误开」。
    /// </summary>
    /// <remarks>
    /// 系统的菜单延迟（<c>SPI_GETMENUSHOWDELAY</c>，默认 400ms）是给「级联菜单」用的：
    /// 指针去下层项的路上会扫过带子菜单的项，立刻展开会一路闪出来、还会让途中项抢走点击。
    /// 但悬浮窗的菜单只有十来项、子菜单又窄，400ms 只剩「卡」的感觉，所以按用户要求直接 0。
    /// 用 <c>static readonly</c> 而不是 <c>const</c>：0 时下面那条计时器分支是死代码，<c>const</c> 会报 CS0162。
    /// </remarks>
    private static readonly int HoverOpenMs = 0;

    public static ContextMenu Build(FloatingItemDto item)
    {
        var menu = new ContextMenu();
        bool group = item.Members.Count > 1;

        if (group)
        {
            Add(menu, $"悬浮分组「{(string.IsNullOrWhiteSpace(item.Name) ? "未命名" : item.Name)}」· {item.Members.Count} 个动作", null);
            var run = Submenu(menu, "运行");
            foreach (var member in item.Members)
            {
                var action = ActionRegistry.FindById(member.ActionId);
                AddItem(run, action?.Name ?? "（动作已删除）", () => Run(member.ActionId));
            }
        }
        else
        {
            string name = ActionRegistry.FindById(item.Members[0].ActionId)?.Name ?? "（动作已删除）";
            Add(menu, $"运行「{name}」", () => Run(item.Members[0].ActionId));
        }

        var edit = Submenu(menu, group ? "编辑成员…" : "编辑动作…");
        foreach (var member in item.Members)
        {
            string id = member.ActionId;
            var action = ActionRegistry.FindById(id);
            AddItem(edit, action?.Name ?? "（动作已删除）", () => EditAction(id));
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

        var size = Submenu(menu, "按钮大小");
        foreach (double value in Sizes)
        {
            double v = value;
            var mi = AddItem(size, $"{v:0} px", () => SetSize(item, v));
            mi.IsCheckable = true;
            mi.IsChecked = Math.Abs(item.Size - v) < 0.5;
        }

        var opacity = Submenu(menu, "不透明度");
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
            var direction = Submenu(menu, "方向");
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
            var one = Submenu(menu, "拆分为独立悬浮（该动作）");
            foreach (var member in item.Members)
            {
                string id = member.ActionId;
                string name = ActionRegistry.FindById(id)?.Name ?? "（动作已删除）";
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

    /// <summary>
    /// 建一个「点一下就展开」的父项，并让它**悬停即开**（默认没有延迟）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF 默认只在悬停时展开子菜单，而且要走系统的「菜单延迟」（<c>SPI_GETMENUSHOWDELAY</c>，
    /// 默认 400ms），实测点完一项再滑到带箭头的项要等半秒，跟手很差。
    /// </para>
    /// <para>
    /// 现在 <see cref="HoverOpenMs"/> = 0：<c>MouseEnter</c> 里**立即**展开。想留一点容错
    /// （扫过父项时不闪开）把它调成 60 / 100 即可，走同一个旋钮。
    /// </para>
    /// <para>
    /// WPF 默认只认悬停，点父项没反应——这是「右键菜单点不动」最容易被当成 bug 的地方，
    /// 所以同时挂上 Click：点开和悬停都是即时的。
    /// </para>
    /// </remarks>
    private static MenuItem Submenu(ContextMenu menu, string header)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => mi.IsSubmenuOpen = true;

        if (HoverOpenMs <= 0)
        {
            mi.MouseEnter += (_, _) => { if (!mi.IsSubmenuOpen) mi.IsSubmenuOpen = true; };
        }
        else
        {
            var hover = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(HoverOpenMs),
            };
            bool hovering = false;
            hover.Tick += (_, _) =>
            {
                hover.Stop();
                if (hovering) mi.IsSubmenuOpen = true;
            };
            mi.MouseEnter += (_, _) => { hovering = true; if (!mi.IsSubmenuOpen) hover.Start(); };
            mi.MouseLeave += (_, _) => { hovering = false; hover.Stop(); };
            mi.Unloaded += (_, _) => { hovering = false; hover.Stop(); };
        }

        // 收起也必须自己做：子菜单一开，WPF 就把「离开父项 → 撤高亮 + 收子菜单」挂到
        // MenuShowDelay（默认 400ms）的计时器上；指针直接离开**整个菜单**时它连计时器都不起，
        // 高亮和子菜单会一直挂在那儿（实测：移出菜单 400ms 后 IsHighlighted 仍是 true）。
        // 所以离开父项 / 离开子菜单时，只要指针不在「父项 + 自己的子菜单」里就立刻收起。
        UIElement? popupChild = null;
        mi.MouseLeave += (_, _) => CollapseIfPointerOutside(mi, popupChild);
        mi.SubmenuOpened += (_, _) =>
        {
            if (popupChild != null) return;
            popupChild = PopupChild(mi);
            if (popupChild != null)
                popupChild.MouseLeave += (_, _) => CollapseIfPointerOutside(mi, popupChild);
        };

        menu.Items.Add(mi);
        return mi;
    }

    /// <summary>指针是否落在这个元素的实际矩形里（跨 popup 窗口也算，按屏幕坐标换算）。</summary>
    private static bool PointerInside(UIElement element)
    {
        if (!element.IsVisible || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
            return false;

        var p = Mouse.GetPosition(element);
        return p.X >= 0 && p.Y >= 0
               && p.X <= element.RenderSize.Width && p.Y <= element.RenderSize.Height;
    }

    /// <summary>子菜单的 popup 内容（独立窗口，父项的 <c>IsMouseOver</c> 覆盖不到）。</summary>
    private static UIElement? PopupChild(MenuItem mi) =>
        mi.Template?.FindName("PART_Popup", mi) is Popup { Child: UIElement child } ? child : null;

    /// <summary>
    /// 指针离开了父项（或离开了子菜单）：只要不在「父项 + 子菜单」里，就立刻收起子菜单并撤掉高亮，
    /// 不等系统的 <c>MenuShowDelay</c>。指针斜着进子菜单时，离开父项那一刻已经在子菜单矩形里，
    /// 所以不会被误收。
    /// </summary>
    private static void CollapseIfPointerOutside(MenuItem mi, UIElement? popupChild)
    {
        if (!mi.IsSubmenuOpen) return;
        if (PointerInside(mi)) return;
        if (popupChild != null && PointerInside(popupChild)) return;

        mi.IsSubmenuOpen = false;

        // 高亮（IsHighlighted）由 WPF 的 IsSelected 推出来，而「离开就取消选中」只在它自己的
        // OnMouseLeave 里做——刚才那次离开时子菜单还开着，走的是「延时」分支，所以补发一次
        // MouseLeave，让它按「子菜单已收起」那条路径立刻取消选中（否则高亮会一直留在最后一项上）。
        if (mi.IsHighlighted)
        {
            mi.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseLeaveEvent,
            });
        }
    }

    private static void Run(string actionId)
    {
        var action = ActionRegistry.FindById(actionId);
        if (action == null)
        {
            ToastService.Show("动作不存在：" + actionId);
            FloatingManager.RefreshAll();
            return;
        }
        AppLog.Info("Floating", $"菜单运行动作：{action.Name}");
        ActionExecutor.Run(action);
    }

    private static void EditAction(string actionId)
    {
        if (!PanelActionOps.Edit(null, actionId)) return;
        FloatingManager.RefreshAll();
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
