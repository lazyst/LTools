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

    private void ReloadRows(Step? select = null)
    {
        Step? keep = select ?? SelectedRow()?.Step;

        _rows.Clear();
        _number = 0;
        Walk(_program.Steps, 0);

        int total = _program.Steps.Sum(s => s.SelfAndDescendants().Count());
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepCountText.Text = total == 0 ? "" : $"{total} 步";

        if (keep != null)
        {
            var target = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, keep));
            if (target != null)
            {
                StepList.SelectedItem = target;
                StepList.ScrollIntoView(target);
            }
        }

        EnsureOutputVariables();
    }

    private void Walk(List<Step> list, int depth)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Step step = list[i];
            _number++;
            _rows.Add(MakeRow(step, list, i, depth));

            BodyKind body = SpecOf(step)?.Body ?? BodyKind.none;
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

    private StepRow MakeRow(Step step, List<Step> owner, int index, int depth)
    {
        UnitSpec? spec = SpecOf(step);
        string title = spec?.Name ?? step.Unit;
        if (step.Disabled) title += "（跳过）";

        return new StepRow
        {
            Step = step,
            Owner = owner,
            Index = index,
            Depth = depth,
            Number = _number.ToString(CultureInfo.InvariantCulture),
            Glyph = IconCatalog.GetGlyph(spec?.Icon),
            Title = title,
            Summary = StepSummary.Describe(step),
            Output = StepSummary.OutputText(step),
        };
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

    private void BuildToolbox()
    {
        string query = (SearchBox?.Text ?? "").Trim();
        Toolbox.Children.Clear();

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
            Toolbox.Children.Add(new TextBlock
            {
                Text = group.Key.Length == 0 ? "其它" : group.Key,
                Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(2, 10, 0, 4),
            });

            foreach (var unit in group.OrderBy(u => u.Spec.Name, StringComparer.CurrentCulture))
                Toolbox.Children.Add(BuildToolboxButton(unit.Spec));
        }
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
        button.Click += (_, _) => AddStep(spec.Key);
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

    private static int CategoryOrder(string category) => category switch
    {
        "文本" => 0,
        "文件" => 1,
        "屏幕" => 2,
        "输入" => 3,
        "窗口" => 4,
        "程序" => 5,
        "脚本" => 6,
        "数据" => 7,
        "界面" => 8,
        "内部" => 9,
        "流程" => 10,
        "子动作" => 11,
        _ => 12,
    };

    // ============ 增删改 ============

    private void AddStep(string unitKey)
    {
        Step draft = Step.Of(unitKey);
        var dlg = CreateStepDialog(draft);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var (list, index, _) = InsertionTarget();
        list.Insert(Math.Clamp(index, 0, list.Count), dlg.Result);
        ReloadRows(dlg.Result);
    }

    private void EditStep_Click(object sender, RoutedEventArgs e) => EditSelected(RowOf(sender));

    private void StepList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        EditSelected(RowAt(e.OriginalSource as DependencyObject));
    }

    /// <summary>按钮所在的那一行（行内按钮的 DataContext 就是 <see cref="StepRow"/>）。</summary>
    private static StepRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as StepRow;

    private void EditSelected(StepRow? row = null)
    {
        row ??= SelectedRow();
        if (row?.Step == null || row.Owner == null) return;

        var dlg = CreateStepDialog(row.Step);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        row.Owner[row.Index] = dlg.Result;
        ReloadRows(dlg.Result);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        // 行内按钮点下去不会选中那一行（按钮吃掉了鼠标事件），所以优先用按钮自己的 DataContext，
        // 不能只看当前选中项——否则删掉一步后什么都没选中，再点第二个删除就「没反应」。
        StepRow? row = RowOf(sender) ?? SelectedRow();
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

        owner.RemoveAt(index);
        ReloadRows(next);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        StepRow? row = SelectedRow();
        if (row?.Step == null || row.Owner == null) return;

        int target = row.Index + delta;
        if (target < 0 || target >= row.Owner.Count) return;

        (row.Owner[row.Index], row.Owner[target]) = (row.Owner[target], row.Owner[row.Index]);
        ReloadRows(row.Step);
    }

    private void StepList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;

        if (e.Key == Key.Up) { MoveSelected(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { MoveSelected(1); e.Handled = true; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) BuildToolbox();
    }
}
