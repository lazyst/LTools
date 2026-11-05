using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LTools.Views.Controls;

/// <summary>
/// 全局下拉框行为修正（所有 <see cref="ComboBox"/> 自动生效，无需逐个改 XAML）。
/// </summary>
/// <remarks>
/// <para>
/// 1. **打开时不再「自动下滚」**：WPF 在弹出层还没完成布局（视口高度为 0）时就调用
/// <c>ScrollIntoView</c>，item 滚动模式下会把 <c>VerticalOffset</c> 直接设成选中项下标——
/// 于是「判断」这类长列表每次打开都跳过前面几项，看起来像自己往下滚了一截。
/// 这里在 <c>DropDownOpened</c> 后按真实视口重算：当前项本来就在视口内就不动，
/// 否则只滚到刚好可见。
/// </para>
/// <para>
/// 2. **关闭状态下滚轮掠过下拉框时不再偷偷改值**：WPF 的 ComboBox 把滚轮当成换项，
/// 页面滚不动、值却被改掉。这里把滚轮转交给最近的外层 <see cref="ScrollViewer"/>，
/// 下拉层打开时仍按原生行为滚动列表本身。
/// </para>
/// </remarks>
internal static class ComboBoxAssist
{
    private static bool _registered;

    /// <summary>在应用启动时调用一次（见 <c>App.OnStartup</c>）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        // ComboBox.DropDownOpened 是普通 CLR 事件（没有 RoutedEvent 标识），无法用类处理器；
        // 由 XAML 样式挂上附加属性 Watch，每个 ComboBox 应用模板时绑定一次。
        EventManager.RegisterClassHandler(
            typeof(ComboBox), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    /// <summary>样式里设为 True，即启用「打开时校正滚动位置」。</summary>
    public static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(bool), typeof(ComboBoxAssist),
        new PropertyMetadata(false, OnWatchChanged));

    public static bool GetWatch(DependencyObject element) => (bool)element.GetValue(WatchProperty);
    public static void SetWatch(DependencyObject element, bool value) => element.SetValue(WatchProperty, value);

    private static void OnWatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo || e.NewValue is not true) return;
        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownOpened += OnDropDownOpened;
    }

    // —— 打开时校正滚动位置 ——

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        // 弹出层在打开事件之后才完成测量；等布局结束再校正，避免看到第一帧的错误位置。
        combo.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RevealSelection(combo)));
    }

    private static void RevealSelection(ComboBox combo)
    {
        if (!combo.IsDropDownOpen) return;
        ScrollViewer? scroll = FindPopupScrollViewer(combo);
        if (scroll == null) return;

        int index = combo.SelectedIndex;
        if (index < 0)
        {
            scroll.ScrollToTop();
            return;
        }

        // 条目滚动（单位 = 条目）：视口内能放下就从顶部开始，否则只露出最后一行。
        if (scroll.CanContentScroll)
        {
            double viewport = Math.Max(1, scroll.ViewportHeight);
            double target = index < viewport ? 0 : Math.Min(index - viewport + 1, index);
            scroll.ScrollToVerticalOffset(target);
            return;
        }

        // 像素滚动：按条目实际位置判断。
        if (combo.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item) return;
        try
        {
            double top = item.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y + scroll.VerticalOffset;
            double bottom = top + item.ActualHeight;
            double viewTop = scroll.VerticalOffset;
            double viewBottom = viewTop + scroll.ViewportHeight;

            double target = top >= viewTop && bottom <= viewBottom
                ? viewTop
                : bottom - scroll.ViewportHeight;
            scroll.ScrollToVerticalOffset(Math.Clamp(target, 0, scroll.ScrollableHeight));
        }
        catch (InvalidOperationException)
        {
            // 弹出层已关闭 / 条目已回收：忽略即可
        }
    }

    private static ScrollViewer? FindPopupScrollViewer(ComboBox combo)
    {
        if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return null;
        return FindDescendant<ScrollViewer>(popup.Child);
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        if (root is T match) return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is T found) return found;
        }
        return null;
    }

    // —— 关闭状态下的滚轮转交给页面 ——

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen || e.Handled) return;

        ScrollViewer? page = FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(combo));
        if (page == null) return;

        // 先吞掉：否则 ComboBox.OnMouseWheel 会改掉选中项。
        e.Handled = true;
        page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = combo,
        });
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
