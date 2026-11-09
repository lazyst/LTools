using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using LTools.Features.Migration;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 步骤列表 / 工具箱 / 增删改
/// </summary>
public partial class ActionEditorWindow
{
    // ============ 步骤列表 ============

    /// <summary>被折叠的块（如果 / 循环）。按步骤对象记，重建列表后仍然有效；只在本次编辑期间有效。</summary>
    private readonly HashSet<Step> _collapsed = new();

    private void ReloadRows(Step? select = null)
    {
        // 多选：按步骤引用记住选中项，重建列表后恢复（不传 select 时保留当前多选）
        List<Step> keep = select != null
            ? new List<Step> { select }
            : SelectedRows().Select(r => r.Step!).ToList();

        _rows.Clear();
        _number = 0;
        Walk(_program.Steps, 0);

        int total = _program.Steps.Sum(s => s.SelfAndDescendants().Count());
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepCountText.Text = total == 0 ? "" : $"{total} 步";

        StepList.SelectedItems.Clear();
        StepRow? first = null;
        foreach (Step step in keep)
        {
            var target = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, step));
            if (target == null) continue;
            StepList.SelectedItems.Add(target);
            first ??= target;
        }
        if (first != null) StepList.ScrollIntoView(first);

        EnsureOutputVariables();
    }

    private void Walk(List<Step> list, int depth)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Step step = list[i];
            _number++;

            BodyKind body = SpecOf(step)?.Body ?? BodyKind.none;
            bool hasChildren = body != BodyKind.none
                && step.Body.Count + step.Then.Count + step.Else.Count > 0;
            bool collapsed = hasChildren && _collapsed.Contains(step);

            _rows.Add(MakeRow(step, list, i, depth, hasChildren, collapsed));

            if (body == BodyKind.none) continue;

            if (collapsed)
            {
                // 折叠的块不占行，但步号要照旧连续（后面的「第 N 步」不能因为折叠就变）。
                _number += DescendantCount(step);
                continue;
            }

            if (body == BodyKind.thenElse)
            {
                AddBranch(step.Then, depth + 1, "真分支为空 · 选中这一行后从左侧添加");
                // 「否则」这一行就是假分支的开头：选中它再加步骤要落进 else 里。
                // （标记行以前没有 Owner，加进去的步骤会跑到动作末尾，看起来像「否则里什么都没执行」。）
                _rows.Add(MarkerRow("否则", depth, step.Else, 0));
                AddBranch(step.Else, depth + 1, "假分支为空 · 选中这一行后从左侧添加");
                _rows.Add(MarkerRow("结束如果", depth, list, i + 1));
            }
            else if (body == BodyKind.body)
            {
                AddBranch(step.Body, depth + 1, "循环体为空 · 选中这一行后从左侧添加");
                _rows.Add(MarkerRow("结束循环", depth, list, i + 1));
            }
        }
    }

    /// <summary>一个块里一共有多少个子步骤（用来给折叠块跳过步号）。</summary>
    private static int DescendantCount(Step step) =>
        step.Then.Sum(s => 1 + DescendantCount(s))
        + step.Else.Sum(s => 1 + DescendantCount(s))
        + step.Body.Sum(s => 1 + DescendantCount(s));

    private void AddBranch(List<Step> branch, int depth, string emptyHint)
    {
        if (branch.Count == 0)
            _rows.Add(new StepRow
            {
                Owner = branch,
                Index = 0,
                Depth = depth,
                IsMarker = true,
                IsPlaceholder = true,
                MarkerText = emptyHint,
            });
        else
            Walk(branch, depth);
    }

    private StepRow MakeRow(Step step, List<Step> owner, int index, int depth,
        bool hasChildren = false, bool collapsed = false)
    {
        UnitSpec? spec = SpecOf(step);
        string title = spec?.Name ?? step.Unit;
        if (step.Disabled) title += "（跳过）";

        string summary = StepSummary.Describe(step);
        if (collapsed)
        {
            string folded = $"已折叠 {DescendantCount(step)} 步";
            summary = summary.Length == 0 ? folded : $"{summary} · {folded}";
        }

        return new StepRow
        {
            Step = step,
            Owner = owner,
            Index = index,
            Depth = depth,
            Number = _number.ToString(CultureInfo.InvariantCulture),
            Glyph = IconCatalog.GetGlyph(spec?.Icon),
            Title = title,
            Summary = summary,
            Output = StepSummary.OutputText(step),
            HasChildren = hasChildren,
            IsCollapsed = collapsed,
        };
    }

    /// <summary>折叠箭头：收起 / 展开这一块（子步骤不占行，选中项与滚动位置尽量保留）。</summary>
    private void FoldToggle_Click(object sender, RoutedEventArgs e)
    {
        StepRow? row = RowOf(sender);
        if (row?.Step != null) ToggleFold(row.Step);
    }

    private void ToggleFold(Step step)
    {
        if (!_collapsed.Remove(step)) _collapsed.Add(step);
        ReloadRows();
    }

    private void CollapseAll()
    {
        foreach (var (step, _) in AllSteps())
            if (step.Body.Count + step.Then.Count + step.Else.Count > 0) _collapsed.Add(step);
        ReloadRows();
    }

    private void ExpandAll()
    {
        _collapsed.Clear();
        ReloadRows();
    }

    /// <summary>
    /// 按执行顺序列出全部步骤（**含已折叠块里的**）与它的祖先链。
    /// 搜索定位要用祖先链自动展开，变量引用统计不能漏掉折叠块。
    /// </summary>
    private List<(Step Step, List<Step> Ancestors)> AllSteps()
    {
        var list = new List<(Step, List<Step>)>();
        CollectAll(_program.Steps, new List<Step>(), list);
        return list;
    }

    private static void CollectAll(List<Step> steps, List<Step> ancestors,
        List<(Step Step, List<Step> Ancestors)> into)
    {
        foreach (Step step in steps)
        {
            into.Add((step, new List<Step>(ancestors)));
            ancestors.Add(step);
            CollectAll(step.Then, ancestors, into);
            CollectAll(step.Else, ancestors, into);
            CollectAll(step.Body, ancestors, into);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    /// <summary>
    /// 标记行（否则 / 结束如果 / 结束循环）：<c>Owner</c> / <c>Index</c> 决定「选中这一行再加步骤」落到哪里——
    /// 「否则」= 假分支开头，「结束如果」「结束循环」= 这一块之后的下一行。
    /// </summary>
    private static StepRow MarkerRow(string text, int depth, List<Step>? owner = null, int index = 0) => new()
    {
        Depth = depth,
        IsMarker = true,
        MarkerText = text,
        Owner = owner,
        Index = index,
    };

    private StepRow? SelectedRow() => StepList.SelectedItem as StepRow;

    /// <summary>当前选中的所有步骤行（按列表顺序）；标记行 / 空分支占位行不算。</summary>
    private List<StepRow> SelectedRows() =>
        StepList.SelectedItems.OfType<StepRow>()
            .Where(r => r.Step != null && r.Owner != null)
            .OrderBy(r => _rows.IndexOf(r))
            .ToList();

    private static UnitSpec? SpecOf(Step step) =>
        StepRegistry.Default.TryGet(step.Unit, out var unit) ? unit.Spec : null;

    /// <summary>新步骤插到哪里：选中行之后 / 空分支占位处 / 末尾。</summary>
    private (List<Step> List, int Index, int Depth) InsertionTarget()
    {
        StepRow? row = SelectedRow();
        if (row?.Owner != null)
        {
            int index = row.Step != null ? row.Index + 1 : row.Index;
            return (row.Owner, Math.Clamp(index, 0, row.Owner.Count), row.Depth);
        }

        return (_program.Steps, _program.Steps.Count, 0);
    }

    private StepEditorDialog CreateStepDialog(Step step) =>
        new(step, program: _program, variableNames: VariableNames, resourceRoot: ResourceRoot) { Owner = this };

    private IEnumerable<string> VariableNames() =>
        _variables.Select(v => v.Name.Text.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ============ 工具箱 ============

    /// <summary>
    /// 左栏「添加单元」里已折叠的分类。**进程内共享 + 落盘 <c>ui.json</c>**：
    /// 在 A 动作里折好的分组，换编辑 B 动作、重开编辑器都还是那个样子（全局记忆，不跟着动作走）。
    /// </summary>
    private static HashSet<string>? _toolboxCollapsed;

    private static HashSet<string> CollapsedToolboxGroups
    {
        get
        {
            if (_toolboxCollapsed != null) return _toolboxCollapsed;

            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (string key in ConfigStore.LoadConfig().ToolboxCollapsedGroups)
                    if (!string.IsNullOrWhiteSpace(key)) set.Add(key);
            }
            catch (Exception ex)
            {
                // 读不到就按「全部展开」走，不影响编辑动作
                AppLog.Warn("Editor", "读取左栏折叠状态失败：" + ex.Message);
            }

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

    /// <summary>写盘（只动 ui.json 的这一个字段）+ 重建左栏，并把滚动位置放回原处。</summary>
    private void SaveToolboxCollapsed()
    {
        try
        {
            List<string> snapshot = CollapsedToolboxGroups
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            // 显式给锚点：ConfigStore 的路径就是本进程实际用的配置（不靠 ConfigLocator 再猜一次）
            if (string.IsNullOrEmpty(ConfigStore.ConfigPath))
                UiStore.Modify(ui => ui.ToolboxCollapsedGroups = snapshot);
            else
                UiStore.Modify(ConfigStore.ConfigPath, ui => ui.ToolboxCollapsedGroups = snapshot);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Editor", "保存左栏折叠状态失败：" + ex.Message);
        }

        // 重建后把滚动位置放回原处：折一个靠上的分组不该把视图弹回顶部
        ScrollViewer? scroller = FindAncestor<ScrollViewer>(Toolbox);
        double offset = scroller?.VerticalOffset ?? 0;
        BuildToolbox();
        if (scroller != null)
        {
            Toolbox.UpdateLayout();
            scroller.ScrollToVerticalOffset(offset);
        }
    }

    private void BuildToolbox()
    {
        string query = (SearchBox?.Text ?? "").Trim();
        bool searching = query.Length > 0;
        Toolbox.Children.Clear();

        // 「全部展开 / 全部收起」：搜索时分类一定全展开，按钮没有意义
        List<string> categories = ToolboxCategories();
        bool anyCollapsed = categories.Any(CollapsedToolboxGroups.Contains);
        bool allCollapsed = categories.Count > 0 && categories.All(CollapsedToolboxGroups.Contains);
        if (ExpandAllButton != null) ExpandAllButton.IsEnabled = !searching && anyCollapsed;
        if (CollapseAllButton != null) CollapseAllButton.IsEnabled = !searching && !allCollapsed;

        var groups = StepRegistry.Default.Units
            .Where(u => Match(u.Spec, query))
            .GroupBy(u => u.Spec.Category ?? "")
            .OrderBy(g => CategoryOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();

        if (groups.Count == 0)
        {
            Toolbox.Children.Add(new TextBlock
            {
                Text = "没有匹配的单元",
                Style = (Style)FindResource("HintLabel"),
                Margin = new Thickness(2, 8, 0, 0),
            });
            return;
        }

        foreach (var group in groups)
        {
            // 搜索时一律展开：搜到了却被折叠挡住，等于没搜到
            bool collapsed = !searching && CollapsedToolboxGroups.Contains(group.Key);
            Toolbox.Children.Add(BuildToolboxHeader(group.Key, group.Count(), collapsed, searching));
            if (collapsed) continue;

            foreach (var unit in group.OrderBy(u => u.Spec.Name, StringComparer.CurrentCulture))
                Toolbox.Children.Add(BuildToolboxButton(unit.Spec));
        }
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
                : collapsed ? $"展开「{name}」（{count} 个单元）" : $"折叠「{name}」",
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
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = IconCatalog.GetGlyph(spec.Icon),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = spec.Name,
            VerticalAlignment = VerticalAlignment.Center,
        });

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

        return spec.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || spec.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (spec.Category ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
            || (spec.Description ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int CategoryOrder(string category) => UnitCategories.Rank(category);

    // ============ 增删改 ============

    private void AddStep(string unitKey)
    {
        var (list, index, _) = InsertionTarget();
        AddStepAt(unitKey, list, index, direct: false);
    }

    /// <summary>
    /// 把单元插到指定位置。<paramref name="direct"/> = 按默认参数直接插（拖拽 / Ctrl+点击 / 右键「直接添加」）；
    /// 有必填项没默认值时退回参数表单，别插出一个跑不了的步骤。
    /// </summary>
    private void AddStepAt(string unitKey, List<Step> list, int index, bool direct)
    {
        Step? step = direct ? BuildDefaultStep(unitKey) : null;
        if (step == null)
        {
            // 「保存并继续添加」：保存这一步后原地再开一个同单元的空表单，适合连着加一串。
            int at = Math.Clamp(index, 0, list.Count);
            while (true)
            {
                var dlg = CreateStepDialog(Step.Of(unitKey));
                dlg.EnableContinueAdding();

                if (dlg.ShowDialog() != true || dlg.Result == null) return;

                PushUndo($"添加「{StepSummary.Title(unitKey)}」");
                list.Insert(Math.Clamp(at, 0, list.Count), dlg.Result);
                ReloadRows(dlg.Result);
                at++;

                if (!dlg.ContinueAdding) return;
            }
        }

        PushUndo($"添加「{StepSummary.Title(unitKey)}」");
        list.Insert(Math.Clamp(index, 0, list.Count), step);
        ReloadRows(step);
    }

    /// <summary>
    /// 只套 <see cref="InputSpec.Default"/> 建一步；有可见的必填参数没默认值时返回 null。
    /// 与参数表单共用同一份声明，新增单元不用改这里。
    /// </summary>
    private static Step? BuildDefaultStep(string unitKey)
    {
        if (!StepRegistry.Default.TryGet(unitKey, out var unit)) return null;

        Step step = Step.Of(unitKey);
        foreach (var input in unit.Spec.Inputs)
        {
            if (input.Default == null) continue;
            if (!VisibleWhen.IsVisible(input, step.In, unit.Spec)) continue;
            step.In[input.Key] = StepArg.FromValue(input.Default);
        }

        foreach (var input in unit.Spec.Inputs)
        {
            if (!input.Required) continue;
            if (!VisibleWhen.IsVisible(input, step.In, unit.Spec)) continue;
            if (!step.In.ContainsKey(input.Key)) return null;
        }

        return step;
    }

    private void EditStep_Click(object sender, RoutedEventArgs e) => EditSelected(RowOf(sender));

    private void StepList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

        // 只有真的双击在某一行上才编辑：点在列表空白处时不要拿「当前选中行」凑数
        StepRow? row = RowAt(e.OriginalSource as DependencyObject);
        if (row?.Step == null || row.Owner == null) return;
        EditSelected(row);
    }

    /// <summary>按钮所在的那一行（行内按钮的 DataContext 就是 <see cref="StepRow"/>）。</summary>
    private static StepRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as StepRow;

    /// <summary>
    /// 编辑一行步骤（工具栏按钮 / 右键「编辑…」/ Enter / 双击）。<paramref name="row"/> 必须是真正的那一行，
    /// 不要在这里兜底「没有就编辑当前选中行」——双击空白处会因此误开选中步骤。
    /// </summary>
    private void EditSelected(StepRow? row)
    {
        if (row?.Step == null || row.Owner == null) return;

        // 对话框里的「上一步 / 下一步」会先保存这一步，再接着编辑相邻步骤——
        // 一次打开就能顺着改完一串，不用反复双击。
        StepRow current = row;
        while (true)
        {
            Step step = current.Step!;
            List<Step> owner = current.Owner!;

            var dlg = CreateStepDialog(step);
            dlg.EnableNavigation(
                NeighborStepRow(current, -1) != null,
                NeighborStepRow(current, 1) != null);

            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            PushUndo($"编辑「{StepSummary.Title(step)}」");
            owner[current.Index] = dlg.Result;
            ReloadRows(dlg.Result);

            if (dlg.NavigateDelta == 0) return;

            StepRow? saved = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, dlg.Result));
            StepRow? next = saved == null ? null : NeighborStepRow(saved, dlg.NavigateDelta);
            if (next?.Step == null || next.Owner == null) return;
            current = next;
        }
    }

    /// <summary>相邻的可编辑步骤行（跳过标记行 / 空分支占位行）；没有就返回 null。</summary>
    private StepRow? NeighborStepRow(StepRow row, int delta)
    {
        int index = _rows.IndexOf(row);
        if (index < 0) return null;

        for (int i = index + delta; i >= 0 && i < _rows.Count; i += delta)
        {
            StepRow candidate = _rows[i];
            if (candidate.Step != null && candidate.Owner != null) return candidate;
        }

        return null;
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        // 行内按钮点下去不会选中那一行（按钮吃掉了鼠标事件），所以优先用按钮自己的 DataContext，
        // 不能只看当前选中项——否则删掉一步后什么都没选中，再点第二个删除就「没反应」。
        StepRow? row = RowOf(sender) ?? SelectedRow();
        if (row != null) DeleteRow(row);
    }

    /// <summary>删除一行（含子步骤），删完把选中项落到相邻一行。</summary>
    private void DeleteRow(StepRow row)
    {
        if (row?.Step == null || row.Owner == null) return;

        if (row.Step.Body.Count + row.Step.Then.Count + row.Step.Else.Count > 0
            && !ConfirmDialog.Confirm(this, "删除步骤",
                $"「{StepSummary.Title(row.Step)}」带有子步骤，会一起删除。", danger: true))
            return;

        // 删完把选中项落到下一行（没有下一行就上一行），连点删除才能一直生效。
        int index = row.Index;
        List<Step> owner = row.Owner;
        Step? next = index + 1 < owner.Count ? owner[index + 1]
            : index > 0 ? owner[index - 1] : null;

        PushUndo($"删除「{StepSummary.Title(row.Step)}」");
        owner.RemoveAt(index);
        ReloadRows(next);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        StepRow? row = SelectedRow();
        if (row != null) MoveRow(row, delta);
    }

    private void MoveRow(StepRow row, int delta)
    {
        if (row.Step == null || row.Owner == null) return;

        int target = row.Index + delta;
        if (target < 0 || target >= row.Owner.Count) return;

        PushUndo("移动步骤");
        (row.Owner[row.Index], row.Owner[target]) = (row.Owner[target], row.Owner[row.Index]);
        ReloadRows(row.Step);
    }

    private void StepList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        StepRow? row = SelectedRow();
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C:
                    CopySelected();
                    e.Handled = true;
                    return;
                case Key.V:
                    PasteToSelection();
                    e.Handled = true;
                    return;
                case Key.D:
                    DuplicateSelected();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            ToggleDisabledSelected();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && row?.Step != null)
        {
            EditSelected(row);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;

        if (e.Key == Key.Up) { MoveSelected(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { MoveSelected(1); e.Handled = true; }
    }

    /// <summary>Ctrl+V：选中步骤 → 粘到它下面；标记行 / 空分支 → 粘进那个位置；没选中 → 粘到末尾。</summary>
    private void PasteToSelection()
    {
        StepRow? row = SelectedRow();
        if (row?.Owner == null)
        {
            PasteAt(_program.Steps, _program.Steps.Count);
            return;
        }

        int index = row.Step != null ? row.Index + 1 : row.Index;
        PasteAt(row.Owner, index);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) BuildToolbox();
    }
}
