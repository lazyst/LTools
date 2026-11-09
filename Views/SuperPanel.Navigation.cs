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
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 搜索 / 选中 / 窗口事件
/// </summary>
public partial class SuperPanelWindow
{
    // ============ 搜索 / 选中 ============

    private void FocusSearch()
    {
        // 暂存区页没有搜索框：别把焦点塞给隐藏的 TextBox（Esc / 方向键仍由窗口处理）
        if (SearchHost.Visibility != Visibility.Visible) return;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildAll();
        GlobalScroll.ScrollToTop();
        ContextScroll.ScrollToTop();
        if (SearchBox.Text?.Length > 0 && _tiles.Count > 0) SelectTile(0);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                RequestEscape();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Tab:
                // 输入框里 Tab 让出焦点给磁贴，保持可键盘操作
                break;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { RequestEscape(); e.Handled = true; return; }
        // 暂存区页没有可键盘选中的磁贴：别让方向键 / Enter 去碰被遮住的正式动作
        if (_stashMode) return;
        if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; return; }
        if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; return; }
        if (e.Key == Key.Enter) { ExecuteSelected(); e.Handled = true; }
    }

    private void MoveSelection(int delta)
    {
        if (_tiles.Count == 0) return;
        int next = _selected < 0
            ? (delta > 0 ? 0 : _tiles.Count - 1)
            : Math.Clamp(_selected + delta, 0, _tiles.Count - 1);
        SelectTile(next);
    }

    private void SelectTile(int index)
    {
        if (index < 0 || index >= _tiles.Count) return;
        if (_selected >= 0 && _selected < _tiles.Count)
        {
            var old = _tiles[_selected];
            old.BorderBrush = Res("BorderBrush");
            old.BorderThickness = new Thickness(1);
        }
        _selected = index;
        var b = _tiles[index];
        b.BorderBrush = Res("PrimaryBrush");
        b.BorderThickness = new Thickness(2);
        try { b.BringIntoView(); } catch { /* 布局尚未完成时忽略 */ }
    }

    private void ExecuteSelected()
    {
        if (_selected < 0 || _selected >= _tiles.Count) return;
        if (_tiles[_selected].Tag is not PanelTileRef tref || tref.ActionId == null) return;
        ExecuteAction(tref.ActionId);
    }

    /// <summary>Esc：拖动中先取消，否则关闭面板。</summary>
    internal void RequestEscape()
    {
        if (_globalDrag.IsActive) { _globalDrag.Cancel(); return; }
        if (_contextDrag.IsActive) { _contextDrag.Cancel(); return; }
        Features.SuperPanel.Close();
    }

    private void ExecuteAction(string id)
    {
        if (!BindingRef.Exists(id))
        {
            ToastService.Show(BindingRef.IsFeature(id) ? "功能不存在：" + id : "动作不存在：" + id);
            return;
        }
        Features.SuperPanel.Close();
        ActionExecutor.RunRef(id, _invokeX, _invokeY);
    }

    // ============ 磁贴拖动（实现见 Views/Controls/TileDragSort.cs） ============

    // ============ 窗口事件 ============

    /// <summary>左栏「动作面板」：回到双区磁贴页。</summary>
    private void NavActions_Click(object sender, RoutedEventArgs e)
    {
        ShowActionsPage();
        FocusSearch();
    }

    /// <summary>左栏「暂存区」：主区切成草稿卡片页（照 Quicker：暂存区是面板的一页）。</summary>
    private void NavStash_Click(object sender, RoutedEventArgs e) => ShowStashPage();

    /// <summary>切到暂存区页（<see cref="Features.SuperPanel.ShowStash"/> 也从这里进来）。</summary>
    internal void ShowStashPage()
    {
        _stashMode = true;
        ApplyPageMode();
    }

    private void ShowActionsPage()
    {
        _stashMode = false;
        ApplyPageMode();
    }

    private void ApplyPageMode()
    {
        ActionsPane.Visibility = _stashMode ? Visibility.Collapsed : Visibility.Visible;
        StashView.Visibility = _stashMode ? Visibility.Visible : Visibility.Collapsed;

        NavActionsBtn.Tag = _stashMode ? null : "Selected";
        NavStashBtn.Tag = _stashMode ? "Selected" : null;

        // 搜索只搜正式动作：停在草稿页时把搜索框收起来，免得输进去什么也搜不到
        SearchHost.Visibility = _stashMode ? Visibility.Collapsed : Visibility.Visible;

        StashView.SetActive(_stashMode);
        if (_stashMode) StashView.Reload(force: true);
        else FocusSearch();
    }

    /// <summary>左栏暂存区入口的提示带上草稿数（卡片数由 <see cref="StashPane"/> 报回来）。</summary>
    private void UpdateStashRail()
    {
        int count = StashView.Count;
        NavStashBtn.ToolTip = count == 0
            ? "暂存区：AI 写好的草稿停在这里（现在是空的）"
            : $"暂存区：{count} 份草稿（点开查看 / 试运行 / 采纳）";
        AutomationProperties.SetHelpText(NavStashBtn, count == 0 ? "空" : $"{count} 份草稿");
    }

    /// <summary>菜单交给面板窗口记账（钩子据此不关面板；悬浮项继续盖在上面）。</summary>
    internal void AttachMenu(ContextMenu menu) => TrackInteract(menu);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 搜索框 / 按钮自行处理，不拖窗口
        if (e.OriginalSource is DependencyObject d
            && (FindAncestor<Button>(d) != null || FindAncestor<TextBox>(d) != null)) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* 极快点击：按下时鼠标已抬起 */ }
        e.Handled = true;
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        PinIcon.Text = pinned ? "\uE842" : "\uE840";
        PinBtn.Foreground = Res(pinned ? "PrimaryBrush" : "SecondaryTextBrush");
        PinBtn.ToolTip = pinned ? "取消钉住（当前：点击外部不关闭）" : "钉住：点击外部不关闭";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Features.SuperPanel.Close();
        // 和 CapsLock+\ / 托盘「设置」一致：打开设置，停在上次记住的导航页
        // （面板里的「面板设置…」才是显式跳到面板管理 → 场景）。
        ConfigHelper.Open();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Features.SuperPanel.Close();
}
