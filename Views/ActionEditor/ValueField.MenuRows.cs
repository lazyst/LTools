using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 弹层里的一行（条目 / 分组标题 / 分隔线）：建行、高亮、上下移动、
/// 按搜索词过滤与面包屑。
/// </summary>
public partial class ValueField
{
    // ============ 菜单行 ============

    private void AddHeader(string text, int page)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)FindResource("HintTextBrush"),
            Margin = new Thickness(8, 6, 8, 3),
        };
        var host = new Border { Child = label };
        MenuList.Children.Add(host);
        _rows.Add(new MenuRow(host, MenuRowKind.header, page));
    }

    private void AddSeparator(int page)
    {
        var host = new Border
        {
            Height = 1,
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)FindResource("BorderBrush"),
        };
        MenuList.Children.Add(host);
        _rows.Add(new MenuRow(host, MenuRowKind.separator, page));
    }

    private void AddItem(string glyph, string label, string detail, string search, Action run,
        int page = 0, bool nav = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (detail.Length > 0)
        {
            var sub = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("HintTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(sub, 2);
            grid.Children.Add(sub);
        }

        var host = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 5, 6, 5),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        var row = new MenuRow(host, MenuRowKind.item, page, search, run, nav);
        host.MouseEnter += (_, _) => SetActive(row);
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Activate(row);
        };

        MenuList.Children.Add(host);
        _rows.Add(row);
    }

    /// <summary>
    /// 执行一行：换页项（导航）保持弹层打开、就地换页；其余先关（紧接着可能弹对话框 / 框选）。
    /// </summary>
    private void Activate(MenuRow row)
    {
        if (row.Invoke == null) return;
        if (!row.Nav) CloseMenuImmediately();
        row.Invoke();
    }

    /// <summary>
    /// 立刻收起来源弹层：紧随其后的入口（屏幕取点 / 框选 / 点窗口）会在同一 UI 线程里
    /// Thread.Sleep 等桌面重绘再截图，Fade 淡出动画要等渲染帧才跑得完——否则弹层会被
    /// 拍进冻结画面、或浮在取点窗口上。普通关闭（Esc / 点外部 / 失焦）仍走淡出动画。
    /// </summary>
    private void CloseMenuImmediately()
    {
        if (!Menu.IsOpen) return;
        Menu.PopupAnimation = PopupAnimation.None;
        Menu.IsOpen = false;
        Menu.PopupAnimation = PopupAnimation.Fade;
    }

    private void SetActive(MenuRow? row)
    {
        if (_active != null)
            _active.Host.Background = Brushes.Transparent;
        _active = row;
        if (_active != null)
            _active.Host.Background = (Brush)FindResource("PressedBgBrush");
    }

    private void MoveActive(int delta)
    {
        var visible = _rows.Where(r => r.IsItem && r.Host.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return;

        int index = _active == null ? -1 : visible.IndexOf(_active);
        index = (index + delta + visible.Count) % visible.Count;
        SetActive(visible[index]);
        visible[index].Host.BringIntoView();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    /// <summary>
    /// 按当前页 / 搜索词决定哪些行可见：没搜索时只看当前页；**搜索时跨页匹配**
    /// （在「取值动作单元」页也能直接搜到变量和选择方式）。
    /// </summary>
    private void ApplyFilter(string query)
    {
        query = (query ?? "").Trim();
        bool searching = query.Length > 0;
        SetActive(null);

        foreach (var row in _rows)
        {
            if (!row.IsItem) continue;

            bool visible = searching
                ? row.Search.Contains(query, StringComparison.OrdinalIgnoreCase)
                : row.Page == (_showingUnits ? 1 : 0);

            // 「取值动作单元…」是换页入口：只在主列表、且没搜索时出现（搜索时动作单元自己就在结果里）。
            if (row.Nav) visible = !searching && !_showingUnits;

            row.Host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 标题 / 分隔线都是一段的分界：到下一个分界之前有可见条目才显示。
        bool seen = false;
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            var row = _rows[i];
            if (row.IsItem)
            {
                seen |= row.Host.Visibility == Visibility.Visible;
                continue;
            }

            row.Host.Visibility = seen ? Visibility.Visible : Visibility.Collapsed;
            seen = false;
        }

        UpdateCrumb();
        MoveActive(1);
    }

    /// <summary>面包屑：主列表写「来源」，二级页写「取值动作单元」并露出返回。</summary>
    private void UpdateCrumb()
    {
        CrumbText.Text = _showingUnits ? "取值动作单元" : "来源";
        BackButton.Visibility = _showingUnits ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.ToolTip = _showingUnits
            ? "在取值动作单元里搜索（会连变量一起找）；点 ‹ 返回"
            : "搜索变量、选择方式、取值动作单元";
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveActive(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveActive(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (_active != null) Activate(_active);
                break;
            case Key.Escape:
                e.Handled = true;
                Menu.IsOpen = false;
                break;
        }
    }

}
