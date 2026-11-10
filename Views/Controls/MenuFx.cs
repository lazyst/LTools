using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace LTools.Views.Controls;

/// <summary>
/// 菜单里的「父项 → 子菜单」：点一下就展开，悬停立即展开（不等系统的菜单延迟）。
/// </summary>
/// <remarks>
/// <para>
/// 悬浮项右键菜单与「模拟按键」的分类下拉共用这一份实现——两处的坑完全一样，别再各写一套。
/// </para>
/// <para>
/// WPF 默认只在悬停时展开子菜单，而且要走系统的「菜单延迟」（<c>SPI_GETMENUSHOWDELAY</c>，
/// 默认 400ms），实测点完一项再滑到带箭头的项要等半秒，跟手很差；点父项本身毫无反应
/// （「菜单点不动」最容易被当成 bug 的地方）。现在 <see cref="HoverOpenMs"/> = 0：
/// <c>MouseEnter</c> 里立即展开，同时挂上 <c>Click</c>。
/// </para>
/// </remarks>
internal static class MenuFx
{
    /// <summary>
    /// 悬停多久展开子菜单（毫秒）。0 = 立即。
    /// </summary>
    /// <remarks>
    /// 用 <c>static readonly</c> 而不是 <c>const</c>：0 时下面那条计时器分支是死代码，<c>const</c> 会报 CS0162。
    /// 想留一点容错（扫过父项时不闪开）把它调成 60 / 100 即可，走同一个旋钮。
    /// </remarks>
    private static readonly int HoverOpenMs = 0;

    /// <summary>建一个「点一下就展开、悬停也展开」的父项，并挂进 <paramref name="menu"/>。</summary>
    internal static MenuItem Submenu(ItemsControl menu, string header)
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
}
