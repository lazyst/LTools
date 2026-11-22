using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LTools.Core;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 设置搜索的交互：输入 → 匹配 → 浮层结果 → 回车跳到那一页的那一项并高亮。
/// </summary>
/// <remarks>
/// <para>
/// 入口在左栏顶部（<c>Ctrl+F</c> / <c>Ctrl+K</c> 随时聚焦）。索引由
/// <c>ConfigHelper.SearchIndex.cs</c> 从页面逻辑树里抓，匹配规则在 <c>Core/SettingsSearch.cs</c>，
/// 这里只负责显示与跳转。
/// </para>
/// <para>
/// 首次聚焦时建索引：6 个惰性页会在这一步实例化（百余毫秒），所以排在 Background 优先级，
/// 等输入处理完再做；用户从不搜索就永远不建，惰性建页的收益不受影响。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    private SearchDebounce? _settingsSearchDebounce;

    private SearchDebounce SettingsSearchDebounce =>
        _settingsSearchDebounce ??= new SearchDebounce(150, RefreshSearchResults);

    // ============ 输入 ============

    private void SettingsSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_searchEntries != null)
        {
            // 浮层可能因为点了外面关掉：框里还有词就把它重新打开，别让用户以为搜索坏了
            ReopenSearchIfNeeded();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            EnsureSearchIndex();
        }));
    }

    /// <summary>框里还有查询词、但浮层被点外部关掉了：重新算一次并打开。</summary>
    private void ReopenSearchIfNeeded()
    {
        if (_searchEntries == null || string.IsNullOrWhiteSpace(SettingsSearchBox.Text)) return;
        SettingsSearchDebounce.Now();
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SettingsSearchDebounce.Signal();

    /// <summary>Ctrl+F / Ctrl+K 聚焦搜索框（选中已有文字，直接重打）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchPopup.IsOpen)
        {
            SearchPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key is not (Key.F or Key.K)) return;
        SettingsSearchBox.Focus();
        SettingsSearchBox.SelectAll();
        ReopenSearchIfNeeded();
        e.Handled = true;
    }

    /// <summary>Popup 是 StaysOpen=True：点主窗口里搜索框以外的任何地方就收起（点结果行不在主窗口里，不会经过这里）。</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchPopup.IsOpen) return;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null;
             node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
        {
            if (node == SettingsSearchBox) return;
        }
        SearchPopup.IsOpen = false;
    }

    /// <summary>
    /// 切到别的程序时收起浮层。**不能无脑关**：点结果行时主窗口也会收到 Deactivated（Popup 是
    /// 自己的窗口），当场关会让鼠标按下就没了、Up 也点不到。鼠标已经在结果列表上（正在点）就
    /// 当作正常交互，等跳转自己收起。
    /// </summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!SearchPopup.IsOpen) return;
        if (IsActive || SearchResults.IsMouseOver || SearchResults.IsKeyboardFocusWithin) return;
        SearchPopup.IsOpen = false;
    }

    private void RefreshSearchResults()
    {
        if (string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
        {
            SearchPopup.IsOpen = false;
            return;
        }

        EnsureSearchIndex();
        IReadOnlyList<SettingsHit> hits = SettingsSearch.Match(_searchEntries ?? new List<SettingsEntry>(),
            SettingsSearchBox.Text);

        SearchResults.ItemsSource = hits.Select(h => h.Entry).ToList();
        bool any = hits.Count > 0;
        SearchResults.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SearchEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SearchResults.SelectedIndex = any ? 0 : -1;
        SearchPopup.IsOpen = true;
    }

    // ============ 键盘 / 鼠标 ============

    private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when SearchPopup.IsOpen:
                SettingsSearchDebounce.Flush();
                if (SearchResults.Items.Count > 0)
                {
                    SearchResults.Focus();
                    SearchResults.SelectedIndex = 0;
                }
                e.Handled = true;
                break;
            case Key.Enter:
                SettingsSearchDebounce.Flush();
                ActivateSearchSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                SettingsSearchDebounce.Cancel();
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up when SearchResults.SelectedIndex <= 0:
                // 从第一项再往上：回到搜索框接着改词
                SettingsSearchBox.Focus();
                SettingsSearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchPopup.IsOpen = false;
                SettingsSearchBox.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSearchSelection();
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 先从点到的元素找容器；点文字时 OriginalSource 可能是 Run（FrameworkContentElement），
        // 容器查找会落空，这时退回 ListBox 的选中项（MouseDown 已经把它选中了）。
        SettingsEntry? entry = SearchEntryFrom(e.OriginalSource as DependencyObject)
                               ?? SearchResults.SelectedItem as SettingsEntry;
        if (entry == null)
        {
            // 留一条可查的痕迹：点到了结果浮层但没有对应条目（真机上唯一难复现的一类）
            AppLog.Warn("SettingsSearch", "点击结果未命中：" + (e.OriginalSource?.GetType().Name ?? "<null>"));
            return;
        }
        e.Handled = true;
        ActivateSearchEntry(entry);
    }

    /// <summary>从点击到的元素找它所在的结果行；会先把内容元素（Run）沿逻辑父链走到 FrameworkElement。</summary>
    private SettingsEntry? SearchEntryFrom(DependencyObject? source)
    {
        if (source == null) return null;
        FrameworkElement? element = source as FrameworkElement;
        for (DependencyObject? node = source; element == null && node != null; node = LogicalTreeHelper.GetParent(node))
            element = node as FrameworkElement;
        if (element == null) return null;
        return (SearchResults.ContainerFromElement(element) as ListBoxItem)?.DataContext as SettingsEntry;
    }

    // ============ 跳转与高亮 ============

    private void ActivateSearchSelection()
    {
        if (SearchResults.SelectedItem is SettingsEntry entry)
        {
            ActivateSearchEntry(entry);
            return;
        }
        if (SearchResults.Items.Count > 0 && SearchResults.Items[0] is SettingsEntry first)
            ActivateSearchEntry(first);
    }

    private void ActivateSearchEntry(SettingsEntry entry)
    {
        AppLog.Info("SettingsSearch", $"跳转：{entry.Page} › {entry.Group} › {entry.Label}（{entry.Tag}）");
        SearchPopup.IsOpen = false;
        SettingsSearchBox.Text = "";   // 清空：下一步可能还想搜别的
        if (!_searchTargets.TryGetValue(entry, out FrameworkElement? target)) return;

        // 点结果行时焦点可能进了 Popup 的窗口：跳完把设置窗口拉回前台
        Activate();

        // 先切页：ApplyNavPage 会展开所属分组、实例化 / 刷新该页并点亮父项
        SelectNavItem(entry.Tag);
        ApplyNavPage(entry.Tag);

        // 目标可能藏在折叠的高级参数里，先展开再滚动，否则 BringIntoView 也落不到可见处
        ExpandAncestors(target);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;
            try { target.BringIntoView(); }
            catch (InvalidOperationException) { /* 目标已经不在可视树里（页被重建） */ }
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => FlashSearchTarget(target)));
        }));
    }

    private static void ExpandAncestors(DependencyObject target)
    {
        for (DependencyObject? node = LogicalTreeHelper.GetParent(target); node != null;
             node = LogicalTreeHelper.GetParent(node))
        {
            if (node is Expander expander) expander.IsExpanded = true;
        }
    }

    /// <summary>在内容区盖一层 1.5 秒的高亮框：不用 Adorner（模板里不一定有 AdornerDecorator）。</summary>
    private void FlashSearchTarget(FrameworkElement target)
    {
        if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;

        Point origin;
        try { origin = target.TranslatePoint(new Point(0, 0), HighlightLayer); }
        catch (InvalidOperationException) { return; }

        var brush = TryFindResource("PrimaryBrush") as Brush ?? Brushes.Black;
        var box = new Border
        {
            Width = target.ActualWidth + 10,
            Height = target.ActualHeight + 8,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = brush,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
            Opacity = 0,
        };
        Canvas.SetLeft(box, origin.X - 5);
        Canvas.SetTop(box, origin.Y - 4);
        HighlightLayer.Children.Add(box);

        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        fade.Completed += (_, _) => HighlightLayer.Children.Remove(box);
        box.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
