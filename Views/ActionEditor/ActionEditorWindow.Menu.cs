using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 步骤行 / 工具箱单元的右键菜单与多选批量操作。
/// </summary>
/// <remarks>
/// 菜单每次现建（和悬浮窗一致），项的状态直接读当前行与选中项，不维护一份「菜单状态」。
/// 右键点在某一行上时：这一行已在多选里就保留整组，否则先把它单独选中——
/// 菜单里说的「选中的 N 步」与列表高亮永远一致。
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>进程内步骤剪贴板：复制即覆盖，可跨动作窗口粘贴（按行顺序存多步）。</summary>
    private static readonly List<Step> _stepClipboard = new();

    /// <summary>
    /// 右键菜单必须在**按下时**就装好。WPF 的 ContextMenu 服务是在
    /// <c>ContextMenuOpening</c> 触发之后才取菜单，在事件里现赋会赶不上——第一次右键只把菜单存下来、
    /// 不弹出（用户报过「打开编辑器后第一次右键没反应」）。键盘（Shift+F10）仍走 Opening 兜底。
    /// </summary>
    private void StepList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        StepList.ContextMenu = BuildStepMenu(RowAt(e.OriginalSource as DependencyObject));

    private void StepList_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        StepList.ContextMenu = BuildStepMenu(RowAt(e.OriginalSource as DependencyObject));

    /// <summary>按当前行现建一份菜单（冒烟也直接调它，不用伪造右键事件）。</summary>
    private ContextMenu BuildStepMenu(StepRow? row)
    {
        if (row?.Step != null && !StepList.SelectedItems.Contains(row))
        {
            StepList.SelectedItems.Clear();
            StepList.SelectedItem = row;
        }

        var menu = new ContextMenu();
        List<StepRow> selected = SelectedRows();
        bool multi = row?.Step != null && selected.Count > 1 && selected.Contains(row);

        if (multi)
        {
            int count = selected.Count;
            Item(menu, $"复制选中的 {count} 步", CopySelected, "Ctrl+C");
            Item(menu, $"复制一份选中的 {count} 步", DuplicateSelected, "Ctrl+D");
            Item(menu, AllDisabled(selected) ? $"启用选中的 {count} 步" : $"跳过选中的 {count} 步",
                ToggleDisabledSelected, "Space");
            menu.Items.Add(new Separator());
            menu.Items.Add(InsertUnitMenu("在上方插入单元", row!.Owner!, row.Index));
            menu.Items.Add(InsertUnitMenu("在下方插入单元", row.Owner!, row.Index + 1));
            menu.Items.Add(new Separator());
            Item(menu, $"删除选中的 {count} 步", DeleteSelected);
        }
        else if (row?.Step != null && row.Owner != null)
        {
            Item(menu, "编辑…", () => EditSelected(row), "Enter");
            menu.Items.Add(new Separator());
            Item(menu, "复制一份", DuplicateSelected, "Ctrl+D");
            Item(menu, "复制", CopySelected, "Ctrl+C");
            Item(menu, "粘贴到下方", () => PasteAt(row.Owner!, row.Index + 1),
                "Ctrl+V", _stepClipboard.Count > 0);
            menu.Items.Add(new Separator());
            menu.Items.Add(InsertUnitMenu("在上方插入单元", row.Owner, row.Index));
            menu.Items.Add(InsertUnitMenu("在下方插入单元", row.Owner, row.Index + 1));
            menu.Items.Add(new Separator());
            Item(menu, "从此行试运行", () =>
            {
                StepList.SelectedItems.Clear();
                StepList.SelectedItem = row;
                StartRun(fromSelected: true);
            }, enabled: ReferenceEquals(row.Owner, _program.Steps));
            Item(menu, AllDisabled(selected) ? "启用这一步" : "跳过这一步",
                ToggleDisabledSelected, "Space");
            menu.Items.Add(new Separator());

            int children = row.Step.Body.Count + row.Step.Then.Count + row.Step.Else.Count;
            if (children > 0)
            {
                Item(menu, _collapsed.Contains(row.Step)
                        ? $"展开子步骤（{children} 步）"
                        : $"折叠子步骤（{children} 步）",
                    () => ToggleFold(row.Step));
                menu.Items.Add(new Separator());
            }

            Item(menu, "上移", () => MoveRow(row, -1), enabled: row.Index > 0);
            Item(menu, "下移", () => MoveRow(row, 1), enabled: row.Index + 1 < row.Owner.Count);
            menu.Items.Add(new Separator());
            Item(menu, "删除（含子步骤）", DeleteSelected, "Delete");
        }
        else if (row?.Owner != null)
        {
            // 否则 / 结束如果 / 结束循环 / 空分支占位：只有「往这个位置加」有意义
            Item(menu, "粘贴到此处", () => PasteAt(row.Owner!, row.Index),
                "Ctrl+V", _stepClipboard.Count > 0);
            menu.Items.Add(InsertUnitMenu("插入单元到此处", row.Owner, row.Index));
        }
        else
        {
            Item(menu, "粘贴到末尾", () => PasteAt(_program.Steps, _program.Steps.Count),
                "Ctrl+V", _stepClipboard.Count > 0);
            menu.Items.Add(InsertUnitMenu("在末尾插入单元", _program.Steps, _program.Steps.Count));
            menu.Items.Add(new Separator());
            Item(menu, "全部展开", ExpandAll, enabled: _collapsed.Count > 0);
            Item(menu, "折叠所有块", CollapseAll, enabled: _program.Steps.Count > 0);
        }

        return menu;
    }

    /// <summary>工具箱里某个单元的小菜单：填参数添加 / 直接按默认添加。</summary>
    private ContextMenu BuildUnitMenu(UnitSpec spec)
    {
        var menu = new ContextMenu();
        Item(menu, "添加…（填参数）", () => AddStep(spec.Key));
        Item(menu, "直接添加（默认参数）", () =>
        {
            var (list, index, _) = InsertionTarget();
            AddStepAt(spec.Key, list, index, direct: true);
        });
        menu.Items.Add(new Separator());
        Item(menu, "添加到末尾…（填参数）",
            () => AddStepAt(spec.Key, _program.Steps, _program.Steps.Count, direct: false));
        return menu;
    }

    /// <summary>按分类列全部单元，点了就插到 <paramref name="list"/> 的第 <paramref name="index"/> 项之前。</summary>
    private static MenuItem InsertUnitMenu(string header, List<Step> list, int index)
    {
        var root = new MenuItem { Header = header };

        foreach (var group in StepRegistry.Default.Units
                     .GroupBy(u => u.Spec.Category ?? "")
                     .OrderBy(g => UnitCategories.Rank(g.Key))
                     .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var category = new MenuItem { Header = group.Key.Length == 0 ? "其它" : group.Key };
            foreach (var unit in group.OrderBy(u => u.Spec.Name, StringComparer.CurrentCulture))
                category.Items.Add(UnitItem(unit.Spec, list, index));

            root.Items.Add(category);
        }

        return root;
    }

    private static MenuItem UnitItem(UnitSpec spec, List<Step> list, int index)
    {
        var item = new MenuItem { Header = spec.Name, ToolTip = spec.Key };
        item.Click += (_, _) => ((ActionEditorWindow)Window.GetWindow(item)!)
            .AddStepAt(spec.Key, list, index, direct: false);
        return item;
    }

    private static void Item(ContextMenu menu, string header, Action click,
        string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
        };
        if (!string.IsNullOrEmpty(gesture)) item.InputGestureText = gesture;
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    // ============ 复制 / 粘贴 / 复制一份 / 批量删除与跳过 ============

    private static bool AllDisabled(IReadOnlyList<StepRow> rows) =>
        rows.Count > 0 && rows.All(r => r.Step!.Disabled);

    private void CopySelected()
    {
        List<StepRow> rows = SelectedRows();
        if (rows.Count == 0) return;

        _stepClipboard.Clear();
        foreach (StepRow row in rows) _stepClipboard.Add(row.Step!.Clone());
        SetStatus($"已复制 {rows.Count} 步", hint: true);
    }

    private void DuplicateSelected()
    {
        List<StepRow> rows = SelectedRows();
        if (rows.Count == 0) return;

        PushUndo(rows.Count == 1 ? "复制一份" : $"复制一份（{rows.Count} 步）");

        var copies = new List<Step>();
        // 从后往前插，前面的行下标不会被打乱
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            StepRow row = rows[i];
            Step copy = row.Step!.Clone();
            row.Owner!.Insert(Math.Min(row.Index + 1, row.Owner.Count), copy);
            copies.Insert(0, copy);
        }

        SelectSteps(copies);
    }

    private void PasteAt(List<Step> list, int index)
    {
        if (_stepClipboard.Count == 0) return;

        PushUndo(_stepClipboard.Count == 1 ? "粘贴步骤" : $"粘贴 {_stepClipboard.Count} 步");

        int at = Math.Clamp(index, 0, list.Count);
        var inserted = new List<Step>();
        foreach (Step step in _stepClipboard)
        {
            Step copy = step.Clone();
            list.Insert(at++, copy);
            inserted.Add(copy);
        }

        SelectSteps(inserted);
    }

    private void ToggleDisabledSelected()
    {
        List<StepRow> rows = SelectedRows();
        if (rows.Count == 0) return;

        bool disable = !AllDisabled(rows);
        PushUndo(disable ? "跳过步骤" : "启用步骤");
        foreach (StepRow row in rows) row.Step!.Disabled = disable;
        ReloadRows();
    }

    /// <summary>删除选中的步骤（含子步骤）。被另一个选中步骤包含的后代只删一次。</summary>
    private void DeleteSelected()
    {
        List<StepRow> rows = SelectedRows();
        if (rows.Count == 0)
        {
            StepRow? single = SelectedRow();
            if (single?.Step != null && single.Owner != null) rows.Add(single);
        }
        if (rows.Count == 0) return;

        List<StepRow> roots = rows
            .Where(r => !rows.Any(o => !ReferenceEquals(o, r) && ContainsStep(o.Step!, r.Step!)))
            .ToList();

        int withChildren = roots.Count(r =>
            r.Step!.Body.Count + r.Step.Then.Count + r.Step.Else.Count > 0);

        string message = roots.Count == 1
            ? $"「{StepSummary.Title(roots[0].Step!)}」带有子步骤，会一起删除。"
            : $"选中的 {roots.Count} 步会被删除"
              + (withChildren > 0 ? $"，其中 {withChildren} 步带有子步骤。" : "。");

        // 单步且没子步骤时直接删（和以前一样），只有批量或带子步骤才确认
        if ((roots.Count > 1 || withChildren > 0)
            && !ConfirmDialog.Confirm(this, "删除步骤", message, danger: true)) return;

        PushUndo(roots.Count == 1 ? "删除步骤" : $"删除 {roots.Count} 步");

        foreach (var group in roots.GroupBy(r => r.Owner!))
            foreach (StepRow row in group.OrderByDescending(r => r.Index))
                group.Key.Remove(row.Step!);

        ReloadRows();
    }

    /// <summary>重建列表后把这几步一起选中（粘贴 / 复制一份后）。</summary>
    private void SelectSteps(IReadOnlyList<Step> steps)
    {
        ReloadRows(steps.Count > 0 ? steps[0] : null);
        for (int i = 1; i < steps.Count; i++)
        {
            var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, steps[i]));
            if (row != null) StepList.SelectedItems.Add(row);
        }
    }

    /// <summary><paramref name="ancestor"/> 的分支里（任意深度）有没有 <paramref name="candidate"/>。</summary>
    private static bool ContainsStep(Step ancestor, Step candidate)
    {
        foreach (Step child in ancestor.Then)
        {
            if (ReferenceEquals(child, candidate) || ContainsStep(child, candidate)) return true;
        }
        foreach (Step child in ancestor.Else)
        {
            if (ReferenceEquals(child, candidate) || ContainsStep(child, candidate)) return true;
        }
        foreach (Step child in ancestor.Body)
        {
            if (ReferenceEquals(child, candidate) || ContainsStep(child, candidate)) return true;
        }
        return false;
    }
}
