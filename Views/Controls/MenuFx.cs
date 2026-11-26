using System;
using System.Reflection;
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
/// <para>
/// 但「立即展开」会让**斜穿**相邻项变成一次次误切换：指针从父项往子菜单走时必然划过几个邻项。
/// 通道由 <see cref="MenuCone"/>（预测锥 / safe triangle）负责，这里只在每个钩子上问它一句——
/// 悬停能不能开（<c>BlocksOpen</c>）、宽限计时器要不要让路（<c>HoldsOwner</c>）、锥送出去了没（<c>Forget</c>）。
/// </para>
/// </remarks>
internal static class MenuFx
{
    /// <summary>
    /// 悬停多久展开子菜单（毫秒）。0 = 立即（默认）。
    /// </summary>
    /// <remarks>
    /// 打开仍然立即——滑到父项上就该看见子菜单，不要等。给容错的旋钮是 <see cref="HoverCloseMs"/>。
    /// 用可写字段而不是 <c>const</c>：冒烟要临时调大它来验收起宽限。
    /// </remarks>
    internal static int HoverOpenMs = 0;

    /// <summary>
    /// 指针离开「父项 + 子菜单」多久才收起（毫秒，默认 260）。
    /// </summary>
    /// <remarks>
    /// 必须有这段宽限：父项与子菜单是两个窗口，指针斜着往子菜单里挪的那一瞬间两边都不在，
    /// 立刻收的话子菜单会在光标下「一过去就消失」（用户实测报过）。宽限期间指针回到任一边就取消，
    /// 所以它对手感几乎无感——移开之后不会黏着不走，只是不再「一碰就散」。
    /// </remarks>
    internal static int HoverCloseMs = 260;

    /// <summary>建一个「点一下就展开、悬停也展开」的父项，并挂进 <paramref name="menu"/>。</summary>
    internal static MenuItem Submenu(ItemsControl menu, string header) => Submenu(menu, (object)header);

    /// <summary>
    /// 同上，但父项标题用现成的元素：托盘菜单的每一项标题左侧都留了状态点列（保证文字对齐），
    /// 子菜单父项也要照那个样子排，否则它比别的项左出一截。
    /// </summary>
    internal static MenuItem Submenu(ItemsControl menu, object header)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => mi.IsSubmenuOpen = true;

        DispatcherTimer? openTimer = null;
        DispatcherTimer? closeTimer = null;
        UIElement? popupChild = null;

        bool Anywhere() => PointerInside(mi) || (popupChild != null && PointerInside(popupChild));

        void OpenNow()
        {
            if (!mi.IsSubmenuOpen) mi.IsSubmenuOpen = true;
        }

        // 悬停展开：默认立即（HoverOpenMs = 0），滑到父项上就看见子菜单
        mi.MouseEnter += (_, _) =>
        {
            closeTimer?.Stop();

            // 预测锥：指针正在往**另一个**子菜单走的通道里，只是路过这一项 → 不展开、也不点亮它
            if (MenuCone.BlocksOpen(mi)) return;

            if (mi.IsSubmenuOpen) return;
            if (HoverOpenMs <= 0)
            {
                OpenNow();
                return;
            }

            openTimer ??= NewTimer(() =>
            {
                openTimer!.Stop();
                if (mi.IsMouseOver && !MenuCone.BlocksOpen(mi)) OpenNow();
            }, HoverOpenMs);
            openTimer.Stop();
            openTimer.Start();
        };

        // 离开父项：先记着「可能要收」，给指针挪进子菜单的时间；指针回到任一边就取消。
        mi.MouseLeave += (_, _) =>
        {
            openTimer?.Stop();
            ScheduleClose();
        };

        mi.SubmenuOpened += (_, _) =>
        {
            // 预测锥布在这里：子菜单一开，指针往它走的通道就开始生效
            MenuCone.Arm(mi);

            if (popupChild != null) return;
            popupChild = MenuCone.PopupChild(mi);
            if (popupChild == null) return;
            popupChild.MouseEnter += (_, _) => closeTimer?.Stop();
            popupChild.MouseLeave += (_, _) => ScheduleClose();
        };

        mi.Unloaded += (_, _) =>
        {
            openTimer?.Stop();
            closeTimer?.Stop();
            MenuCone.Forget(mi);
        };

        menu.Items.Add(mi);
        return mi;

        // 收起也要自己管：子菜单一开，WPF 就把「离开父项 → 撤高亮 + 收子菜单」挂到
        // MenuShowDelay（默认 400ms）的计时器上；指针直接离开**整个菜单**时它连计时器都不起，
        // 高亮和子菜单会一直挂在那儿（实测：移出菜单 400ms 后 IsHighlighted 仍是 true）。
        void ScheduleClose()
        {
            if (!mi.IsSubmenuOpen) return;

            // 预测锥正压着这一项（指针在往子菜单走的通道里）：宽限计时器让路，别把子菜单收掉
            if (MenuCone.HoldsOwner(mi)) return;

            if (HoverCloseMs <= 0)
            {
                CollapseIfPointerOutside(mi, popupChild);
                return;
            }

            closeTimer ??= NewTimer(() =>
            {
                closeTimer!.Stop();
                // 宽限到点时指针已经在「父项 + 子菜单」里 = 用户是在往子菜单里挪，不收
                if (Anywhere()) return;
                if (MenuCone.HoldsOwner(mi)) return;
                CollapseIfPointerOutside(mi, popupChild);
            }, HoverCloseMs);
            closeTimer.Stop();
            closeTimer.Start();
        }
    }

    private static DispatcherTimer NewTimer(Action tick, int ms)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    /// <summary>指针是否落在这个元素的实际矩形里（跨 popup 窗口也算，按屏幕坐标换算）。</summary>
    private static bool PointerInside(UIElement element)
    {
        if (!element.IsVisible || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
            return false;

        try
        {
            var p = Mouse.GetPosition(element);
            return p.X >= 0 && p.Y >= 0
                   && p.X <= element.RenderSize.Width && p.Y <= element.RenderSize.Height;
        }
        catch
        {
            // 元素已经脱离 PresentationSource（子菜单刚拆掉）：当作不在里面
            return false;
        }
    }

    /// <summary>
    /// 「指针在预测锥里划过这一项」：只用于**压掉它的高亮**——WPF 在 MouseEnter 时已经把
    /// <c>IsHighlighted</c> 点亮了，而用户其实是在往另一个子菜单走，邻项闪一下会像「菜单换项」。
    /// 主题里按这个附加属性把底色刷回透明（触发器排在 <c>IsHighlighted</c> 之后，覆盖它）。
    /// </summary>
    public static readonly DependencyProperty ConePassOverProperty = DependencyProperty.RegisterAttached(
        "ConePassOver", typeof(bool), typeof(MenuFx), new PropertyMetadata(false));

    public static bool GetConePassOver(DependencyObject element) =>
        (bool)element.GetValue(ConePassOverProperty);

    public static void SetConePassOver(DependencyObject element, bool value) =>
        element.SetValue(ConePassOverProperty, value);

    /// <summary>
    /// <c>MenuItem.IsHighlighted</c> 的只读 key：公开的 <c>IsHighlightedProperty</c> 不能 <c>SetValue</c>，
    /// 只有配套的 key 能写（WPF 没有别的公开入口）。字段拿不到就退回「不补高亮」，不抛。
    /// </summary>
    private static readonly DependencyPropertyKey? HighlightedKey =
        typeof(MenuItem)
            .GetField("IsHighlightedPropertyKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as DependencyPropertyKey;

    /// <summary>
    /// 把指针下这一项点亮（指针确实在它上面时）。
    /// </summary>
    /// <remarks>
    /// 锥让位、父项子菜单被程序化收起之后，WPF 已经把这一项的 <c>IsHighlighted</c> 清掉了，
    /// 而它**不会再发一次 <c>MouseEnter</c>**（指针一直没离开过它）——不补这一下，用户把鼠标
    /// 停到「新建组合动作」上时那一项永远不亮（2026-11-26 用户报）。只补高亮，不碰选中态；
    /// 之后鼠标真离开时，WPF 自己的 <c>MouseLeave</c> 会把它清掉，不用另记状态。
    /// </remarks>
    internal static void RestoreHover(MenuItem item)
    {
        if (HighlightedKey == null || !item.IsMouseOver || !item.IsEnabled) return;

        try { item.SetValue(HighlightedKey, true); }
        catch { /* 菜单正在关 / 模板已拆 */ }
    }

    /// <summary>
    /// 撤掉这一项的高亮（指针已经不在它上面时）。
    /// </summary>
    /// <remarks>
    /// 锥让位时会把父项的子菜单程序化关掉，但 WPF 那条「离开 → 撤高亮」挂在
    /// <c>MenuShowDelay</c> 的延时路径上，子菜单一关它就不走了——不移除的话，用户已经移到
    /// 「新建组合动作」上，上面的「新建简单动作」还一直亮着（2026-11-26 用户报）。
    /// </remarks>
    internal static void ClearHover(MenuItem item)
    {
        if (HighlightedKey == null || item.IsMouseOver) return;

        try { item.SetValue(HighlightedKey, false); }
        catch { /* 菜单正在关 / 模板已拆 */ }
    }

    /// <summary>
    /// 指针离开了父项（或离开了子菜单）：只要不在「父项 + 子菜单」里，就立刻收起子菜单并撤掉高亮，
    /// 不等系统的 <c>MenuShowDelay</c>。指针斜着进子菜单时，离开父项那一刻已经在子菜单矩形里，
    /// 所以不会被误收。
    /// </summary>
    private static void CollapseIfPointerOutside(MenuItem mi, UIElement? popupChild)
    {
        if (!mi.IsSubmenuOpen) return;
        if (MenuCone.HoldsOwner(mi)) return;
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
