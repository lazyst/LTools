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
/// 步骤行 / 工具箱单元的右键菜单。菜单每次现建（和悬浮窗一致），
/// 项的状态直接读当前行，不维护一份"菜单状态"。
/// </summary>
public partial class ActionEditorWindow
{
    /// <summary>进程内步骤剪贴板：复制即覆盖，可跨动作窗口粘贴。</summary>
    private static Step? _stepClipboard;

    private void StepList_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        StepList.ContextMenu = BuildStepMenu(RowAt(e.OriginalSource as DependencyObject));

    /// <summary>按当前行现建一份菜单（冒烟也直接调它，不用伪造右键事件）。</summary>
    private ContextMenu BuildStepMenu(StepRow? row)
    {
        var menu = new ContextMenu();

        if (row?.Step != null && row.Owner != null)
        {
            // 右键先选中，菜单项和列表高亮指的是同一行
            StepList.SelectedItem = row;

            Item(menu, "编辑…", () => EditSelected(row), "Enter");
            menu.Items.Add(new Separator());
            Item(menu, "复制一份", () => DuplicateRow(row), "Ctrl+D");
            Item(menu, "复制", () => _stepClipboard = row.Step.Clone(), "Ctrl+C");
            Item(menu, "粘贴到下方", () => PasteAt(row.Owner!, row.Index + 1),
                "Ctrl+V", _stepClipboard != null);
            menu.Items.Add(new Separator());
            menu.Items.Add(InsertUnitMenu("在上方插入单元", row.Owner, row.Index));
            menu.Items.Add(InsertUnitMenu("在下方插入单元", row.Owner, row.Index + 1));
            menu.Items.Add(new Separator());
            Item(menu, "从此行试运行", () =>
            {
                StepList.SelectedItem = row;
                StartRun(fromSelected: true);
            }, enabled: ReferenceEquals(row.Owner, _program.Steps));
            Item(menu, row.Step.Disabled ? "启用这一步" : "跳过这一步", () =>
            {
                row.Step.Disabled = !row.Step.Disabled;
                ReloadRows(row.Step);
            });
            menu.Items.Add(new Separator());
            Item(menu, "上移", () => MoveRow(row, -1), enabled: row.Index > 0);
            Item(menu, "下移", () => MoveRow(row, 1), enabled: row.Index + 1 < row.Owner.Count);
            menu.Items.Add(new Separator());
            Item(menu, "删除（含子步骤）", () => DeleteRow(row));
        }
        else if (row?.Owner != null)
        {
            // 否则 / 结束如果 / 结束循环 / 空分支占位：只有「往这个位置加」有意义
            Item(menu, "粘贴到此处", () => PasteAt(row.Owner!, row.Index),
                "Ctrl+V", _stepClipboard != null);
            menu.Items.Add(InsertUnitMenu("插入单元到此处", row.Owner, row.Index));
        }
        else
        {
            Item(menu, "粘贴到末尾", () => PasteAt(_program.Steps, _program.Steps.Count),
                "Ctrl+V", _stepClipboard != null);
            menu.Items.Add(InsertUnitMenu("在末尾插入单元", _program.Steps, _program.Steps.Count));
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

    // ============ 复制 / 粘贴 / 复制一份 ============

    private void DuplicateRow(StepRow row)
    {
        if (row.Step == null || row.Owner == null) return;
        Step copy = row.Step.Clone();
        row.Owner.Insert(Math.Clamp(row.Index + 1, 0, row.Owner.Count), copy);
        ReloadRows(copy);
    }

    private void PasteAt(List<Step> list, int index)
    {
        if (_stepClipboard == null) return;
        Step copy = _stepClipboard.Clone();
        list.Insert(Math.Clamp(index, 0, list.Count), copy);
        ReloadRows(copy);
    }
}
