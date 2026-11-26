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
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 左栏工具箱：分类折叠、搜索（名称 / 键 / 分类 / 描述 / 拼音首字母）、收藏、最近使用。
/// </summary>
/// <remarks>
/// <para>
/// 工具箱**完全由 <see cref="UnitSpec"/> 派生**，新增动作单元不用改这里。左栏从上到下是三段：
/// 「收藏」（点过 ★ 的，全局记忆）、「最近使用」（真正插进过动作的，最多 6 条）、
/// 然后是按 <see cref="UnitCategories"/> 排序的分类。搜索时只留分类段（收藏 / 最近没有意义，
/// 搜什么就出什么），分类自动全展开。
/// </para>
/// <para>
/// 左缘那一列小图标是**分类导航**（<see cref="CategoryNav"/>，实现在
/// <c>ActionEditorWindow.CategoryNav.cs</c>）：只把对应分类滚到视图顶部，**不切页、不筛选**；
/// 第一项「全部分类」就是这里的平铺页。每次重建都把分类标题登记成锚点，
/// <see cref="FinishToolboxBuild"/> 负责留白 / 重建图标条 / 同步高亮。
/// </para>
/// <para>
/// 收藏与最近都只写 <c>configs/ui.json</c>（<see cref="UiStore"/>），进程内用静态集合缓存一份——
/// 换动作编辑、重开编辑器都沿用，不需要每次都读配置。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    // ============ 工具箱 ============

    /// <summary>
    /// 左栏的三样全局偏好（折叠分类 / 收藏 / 最近使用）都只在 <c>configs/ui.json</c> 里，
    /// 第一次用到时读一次整份配置（读配置会扫动作目录，别为三样各读一次）。
    /// </summary>
    private static LTools.Config.AppConfig? _toolboxPrefs;

    private static LTools.Config.AppConfig ToolboxPrefs
    {
        get
        {
            if (_toolboxPrefs != null) return _toolboxPrefs;

            try
            {
                return _toolboxPrefs = ConfigStore.LoadConfig();
            }
            catch (Exception ex)
            {
                // 读不到就按「默认样子」走（全部展开、没有收藏 / 最近），不影响编辑动作
                AppLog.Warn("Editor", "读取左栏偏好失败：" + ex.Message);
                return _toolboxPrefs = new LTools.Config.AppConfig();
            }
        }
    }

    /// <summary>
    /// 左栏「添加动作单元」里已折叠的分类。**进程内共享 + 落盘 <c>ui.json</c>**：
    /// 在 A 动作里折好的分组，换编辑 B 动作、重开编辑器都还是那个样子（全局记忆，不跟着动作走）。
    /// </summary>
    private static HashSet<string>? _toolboxCollapsed;

    private static HashSet<string> CollapsedToolboxGroups
    {
        get
        {
            if (_toolboxCollapsed != null) return _toolboxCollapsed;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in ToolboxPrefs.ToolboxCollapsedGroups)
                if (!string.IsNullOrWhiteSpace(key)) set.Add(key);

            return _toolboxCollapsed = set;
        }
    }

    /// <summary>折叠 / 展开一个分类，并立刻写回 ui.json（只动这一个字段）。</summary>
    private void ToggleToolboxGroup(string category)
    {
        if (!CollapsedToolboxGroups.Remove(category)) CollapsedToolboxGroups.Add(category);
        SaveToolboxCollapsed();
    }

    private void ExpandAllGroups_Click(object sender, RoutedEventArgs e) => SetToolboxCollapsed(Array.Empty<string>());

    private void CollapseAllGroups_Click(object sender, RoutedEventArgs e) =>
        SetToolboxCollapsed(ToolboxCategories());

    /// <summary>一次设定折叠集合（全部展开 / 全部收起）。</summary>
    private void SetToolboxCollapsed(IEnumerable<string> categories)
    {
        HashSet<string> set = CollapsedToolboxGroups;
        set.Clear();
        foreach (string key in categories) set.Add(key);
        SaveToolboxCollapsed();
    }

    /// <summary>注册表里出现过的分类（用于「全部收起」与按钮可用性）。</summary>
    private static List<string> ToolboxCategories() => StepRegistry.Default.Units
        .Select(u => u.Spec.Category ?? "")
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>写盘（只动 ui.json 的这一个字段）；配置路径取本进程实际用的那份。</summary>
    private static void SaveUiField(Action<UiStateFile> change)
    {
        try
        {
            // 显式给锚点：ConfigStore 的路径就是本进程实际用的配置（不靠 ConfigLocator 再猜一次）
            if (string.IsNullOrEmpty(ConfigStore.ConfigPath))
                UiStore.Modify(change);
            else
                UiStore.Modify(ConfigStore.ConfigPath, change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Editor", "保存左栏状态失败：" + ex.Message);
        }
    }

    /// <summary>重建左栏并把滚动位置放回原处（折一个靠上的分组不该把视图弹回顶部）。</summary>
    private void RebuildToolboxKeepingScroll()
    {
        ScrollViewer? scroller = VisualTree.FindAncestor<ScrollViewer>(Toolbox);
        double offset = scroller?.VerticalOffset ?? 0;
        BuildToolbox();
        if (scroller != null)
        {
            Toolbox.UpdateLayout();
            scroller.ScrollToVerticalOffset(offset);
        }
    }

    /// <summary>写盘（只动 ui.json 的折叠字段）+ 重建左栏。</summary>
    private void SaveToolboxCollapsed()
    {
        List<string> snapshot = CollapsedToolboxGroups
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        SaveUiField(ui => ui.ToolboxCollapsedGroups = snapshot);
        RebuildToolboxKeepingScroll();
    }

    // ============ 收藏 / 最近使用 ============

    /// <summary>收藏的动作单元键，最近收藏的在前（进程内缓存，落盘 ui.json）。</summary>
    private static List<string>? _favoriteUnits;

    private static List<string> FavoriteUnits
    {
        get
        {
            if (_favoriteUnits != null) return _favoriteUnits;

            var list = new List<string>();
            foreach (string key in ToolboxPrefs.FavoriteUnits)
                if (!string.IsNullOrWhiteSpace(key) && !list.Contains(key, StringComparer.OrdinalIgnoreCase))
                    list.Add(key);

            return _favoriteUnits = list;
        }
    }

    /// <summary>最近用过的动作单元键，最近的在前（最多 <see cref="RecentLimit"/> 条）。</summary>
    private static List<string>? _recentUnits;

    private const int RecentLimit = 8;

    /// <summary>左栏「最近使用」最多显示几条（多出来的仍然记着，下次自然顶上来）。</summary>
    private const int RecentShown = 6;

    private static List<string> RecentUnits
    {
        get
        {
            if (_recentUnits != null) return _recentUnits;

            var list = new List<string>();
            foreach (string key in ToolboxPrefs.RecentUnits)
                if (!string.IsNullOrWhiteSpace(key) && !list.Contains(key, StringComparer.OrdinalIgnoreCase))
                    list.Add(key);

            return _recentUnits = list;
        }
    }

    /// <summary>这个动作单元是不是收藏了（行内 ★ 用它）。</summary>
    private static bool IsFavorite(string key) =>
        FavoriteUnits.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>收藏 / 取消收藏并立刻重建左栏（只写 ui.json）。</summary>
    private void ToggleFavorite(string key)
    {
        List<string> list = FavoriteUnits;
        int at = list.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) list.RemoveAt(at);
        else list.Insert(0, key);

        List<string> snapshot = list.ToList();
        SaveUiField(ui => ui.FavoriteUnits = snapshot);
        RebuildToolboxKeepingScroll();
    }

    /// <summary>真正插进动作时记一次「最近使用」（同一个单元只留一条，顶到最前面）。</summary>
    private void NoteUnitUsed(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        List<string> list = RecentUnits;
        list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, key);
        if (list.Count > RecentLimit) list.RemoveRange(RecentLimit, list.Count - RecentLimit);

        List<string> snapshot = list.ToList();
        SaveUiField(ui => ui.RecentUnits = snapshot);
        ScheduleToolboxRefresh();
    }

    /// <summary>
    /// 「最近使用」那段要跟着动，但**别在这一拍重建左栏**：插入常常发生在拖放的 mouseup 里，
    /// 当场换掉整个左栏可视树会把这次拖放的收尾（释放捕获 / 补 Click）搞乱。
    /// 排到后台优先级，多次插入只重建一次。
    /// </summary>
    private void ScheduleToolboxRefresh()
    {
        if (_toolboxRefreshPending) return;

        _toolboxRefreshPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _toolboxRefreshPending = false;
            RebuildToolboxKeepingScroll();
        }), DispatcherPriority.Background);
    }

    private bool _toolboxRefreshPending;

    private void BuildToolbox()
    {
        string query = Pinyin.NormalizeQuery(SearchBox?.Text);
        bool searching = query.Length > 0;
        Toolbox.Children.Clear();
        _categoryAnchors.Clear();   // 分类导航的锚点：左栏重建了，标题元素也换了

        // 「全部展开 / 全部收起」：搜索时分类一定全展开，按钮没有意义
        List<string> categories = ToolboxCategories();
        bool anyCollapsed = categories.Any(CollapsedToolboxGroups.Contains);
        bool allCollapsed = categories.Count > 0 && categories.All(CollapsedToolboxGroups.Contains);
        if (ExpandAllButton != null) ExpandAllButton.IsEnabled = !searching && anyCollapsed;
        if (CollapseAllButton != null) CollapseAllButton.IsEnabled = !searching && !allCollapsed;

        List<IStepUnit> units = StepRegistry.Default.Units.Where(u => Match(u.Spec, query)).ToList();

        // 收藏 → 最近使用 → 分类。搜索时只留分类段：搜什么就出什么，别让固定两段混进来。
        if (!searching)
        {
            BuildShortcutSection("收藏", FavoriteUnits, units);
            BuildShortcutSection("最近使用",
                RecentUnits.Where(k => !IsFavorite(k)).Take(RecentShown), units);
        }

        var groups = units
            .GroupBy(u => u.Spec.Category ?? "")
            .OrderBy(g => CategoryOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();

        if (groups.Count == 0)
        {
            Toolbox.Children.Add(new TextBlock
            {
                Text = "没有匹配的动作单元",
                Style = (Style)FindResource("HintLabel"),
                Margin = new Thickness(2, 8, 0, 0),
            });
        }
        else
        {
            foreach (var group in groups)
            {
                // 搜索时一律展开：搜到了却被折叠挡住，等于没搜到
                bool collapsed = !searching && CollapsedToolboxGroups.Contains(group.Key);
                Button header = BuildToolboxHeader(group.Key, group.Count(), collapsed, searching);
                Toolbox.Children.Add(header);
                _categoryAnchors.Add((group.Key, header));   // 分类导航按分类键找这个标题定位
                if (collapsed) continue;

                foreach (var unit in group.OrderBy(u => u.Spec.Name, StringComparer.CurrentCulture))
                    Toolbox.Children.Add(BuildToolboxButton(unit.Spec));
            }
        }

        // 分类导航：末尾留白 + 重建图标条 + 同步高亮（+ 搜索态清搜索后的定位）
        FinishToolboxBuild(searching);
    }

    /// <summary>收藏 / 最近使用那两段的标题：不可折叠（条目少，折了反而要多点一下）。</summary>
    private TextBlock BuildShortcutHeader(string title, int count) => new()
    {
        Text = $"{title}（{count}）",
        Margin = new Thickness(2, 8, 0, 2),
        FontSize = (double)FindResource("FontSizeHint"),
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindResource("SecondaryTextBrush"),
    };

    /// <summary>
    /// 按一串动作单元键出两段快捷区（收藏 / 最近使用）。键可能已经失效（动作单元改名 / 退役），
    /// 找不到就跳过——**别在这里顺手清理磁盘上的记录**，那样「临时少了一个单元」会把用户收藏吃掉。
    /// </summary>
    private void BuildShortcutSection(string title, IEnumerable<string> keys, IReadOnlyList<IStepUnit> units)
    {
        var byKey = new Dictionary<string, IStepUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (IStepUnit unit in units) byKey[unit.Spec.Key] = unit;

        var picked = new List<UnitSpec>();
        foreach (string key in keys)
            if (byKey.TryGetValue(key, out IStepUnit? unit)) picked.Add(unit.Spec);

        if (picked.Count == 0) return;

        Toolbox.Children.Add(BuildShortcutHeader(title, picked.Count));
        foreach (UnitSpec spec in picked) Toolbox.Children.Add(BuildToolboxButton(spec));
    }

    /// <summary>分类标题：带底色的分组条 + 箭头 + 名称 + 数量，整条可点（点哪都折叠 / 展开）。</summary>
    private Button BuildToolboxHeader(string category, int count, bool collapsed, bool searching)
    {
        string name = category.Length == 0 ? "其它" : category;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = collapsed ? "\uE76C" : "\uE70D",     // ChevronRight / ChevronDown
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("HintTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = (double)FindResource("FontSizeSecondary"),
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = (double)FindResource("FontSizeSecondary"),
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        });

        var header = new Button
        {
            Style = (Style)FindResource("Btn"),            // Btn 的悬停底色走 Tag，这里换成比分组条深一档
            Content = content,
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Tag = (Brush)FindResource("PressedBgBrush"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 26,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 8, 2, 4),
            FontSize = (double)FindResource("FontSizeSecondary"),
            ToolTip = searching
                ? "搜索时分类自动展开"
                : collapsed ? $"展开「{name}」（{count} 个动作单元）" : $"折叠「{name}」",
        };

        if (searching)
        {
            // 搜索时分组恒展开，标题只当标签：不弹手型、不悬停变色、点了也没反应
            header.IsHitTestVisible = false;
            header.Focusable = false;
            header.Cursor = Cursors.Arrow;
        }
        else
        {
            header.Click += (_, _) => ToggleToolboxGroup(category);
        }

        return header;
    }

    private Button BuildToolboxButton(UnitSpec spec)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = IconCatalog.GetGlyph(spec.Icon),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        };
        content.Children.Add(icon);

        var label = new TextBlock
        {
            Text = spec.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        if (IsFavorite(spec.Key))
        {
            var star = new TextBlock
            {
                Text = "★",
                FontSize = 10,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                ToolTip = "已收藏（右键可取消）",
            };
            Grid.SetColumn(star, 2);
            content.Children.Add(star);
        }

        var button = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 28,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 1, 0, 1),
            ToolTip = string.IsNullOrWhiteSpace(spec.Description)
                ? spec.Key
                : $"{spec.Description}\n{spec.Key}",
        };
        button.PreviewMouseLeftButtonDown += (_, e) => BeginUnitPress(button, spec, e);
        button.ContextMenu = BuildUnitMenu(spec);
        button.Click += (_, _) =>
        {
            // 拖拽结束的这一下 mouseup 也会补一个 Click：别再插一步
            if (_suppressUnitClick)
            {
                _suppressUnitClick = false;
                return;
            }

            // Ctrl+点击 = 按默认参数直接插，省一次表单
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                var (list, index, _) = InsertionTarget();
                AddStepAt(spec.Key, list, index, direct: true);
                return;
            }

            AddStep(spec.Key);
        };
        return button;
    }

    private static bool Match(UnitSpec spec, string query)
    {
        if (query.Length == 0) return true;

        // 原样子串（名称 / 键 / 分类 / 说明）或拼音首字母：`jt` → 「截图」。
        return Pinyin.Matches(spec.Name, query)
            || Pinyin.Matches(spec.Key, query)
            || Pinyin.Matches(spec.Category ?? "", query)
            || Pinyin.Matches(spec.Description ?? "", query);
    }

    private static int CategoryOrder(string category) => UnitCategories.Rank(category);
}
