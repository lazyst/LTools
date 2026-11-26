using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「动作管理」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 动作管理 ============

    /// <summary>
    /// 数据真变时重建全量行 + 「引用位置」下拉，再投影一次。
    /// 建行很贵（<see cref="ActionReferenceIndex.DescribeAll"/> 扫全部场景分组 + 宏摘要 + 步骤数），
    /// 所以搜索 / 下拉切换只走 <see cref="ApplyActionFilter"/>，不重新建行、不读配置。
    /// </summary>
    private void PopulateActions()
    {
        _buildingActionRows = true;
        try
        {
            // 建行就是「数据变了」的信号：引用位置索引的缓存跟着失效，列文案与下拉选项取最新。
            ActionReferenceIndex.Invalidate();
            var references = ActionReferenceIndex.DescribeAll();

            _allActionRows.Clear();
            foreach (var a in ActionRegistry.All.OrderBy(x => x.Name, StringComparer.CurrentCulture))
                _allActionRows.Add(new ActionRow(a,
                    references.TryGetValue(a.Id, out var text) ? text : "未使用"));

            // 暂存区入口带上草稿数：AI 写完的东西在这里先看一眼，再决定是否采纳。
            try { OpenStagingButton.Content = $"暂存区（{DraftStore.List().Count}）"; }
            catch { OpenStagingButton.Content = "暂存区"; }

            RefreshActionRefFilter();
        }
        finally { _buildingActionRows = false; }

        ApplyActionFilter();
    }

    /// <summary>重建「引用位置」下拉；当前选中的场景 / 分组被删掉时回落到「全部」。</summary>
    private void RefreshActionRefFilter()
    {
        var options = ActionReferenceIndex.Options();
        if (!options.Any(o => o.Key == _actionRefFilterKey))
            _actionRefFilterKey = ActionReferenceIndex.All;
        ActionRefFilterCombo.ItemsSource = options;
        ActionRefFilterCombo.SelectedValue = _actionRefFilterKey;
    }

    /// <summary>搜索文本 + 引用位置下拉 → 投影到表格（不重新建行、不读配置）。</summary>
    private void ApplyActionFilter()
    {
        if (_buildingActionRows) return;
        int sel = ActionList.SelectedIndex;
        string filter = ActionFilterBox?.Text?.Trim() ?? "";
        string key = _actionRefFilterKey;
        IReadOnlyDictionary<string, HashSet<string>>? map = null;
        int total = _allActionRows.Count;

        _loadingList = true;
        _actionRows.Clear();
        foreach (var row in _allActionRows)
        {
            if (key != ActionReferenceIndex.All)
            {
                // 只有真的按引用位置筛选才建 / 取索引：默认「全部」时切页不该白扫一遍引用。
                map ??= ActionReferenceIndex.Map();
                if (!(map.TryGetValue(row.Id, out var keys) && keys.Contains(key)))
                    continue;
            }
            if (filter.Length > 0
                && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.TypeLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _actionRows.Add(row);
        }
        _loadingList = false;
        if (sel >= 0 && sel < _actionRows.Count) ActionList.SelectedIndex = sel;

        bool filtering = filter.Length > 0 || key != ActionReferenceIndex.All;
        ActionCountText.Text = filtering
            ? $"显示 {_actionRows.Count} / {total} 个动作"
            : $"共 {total} 个动作";
    }

    private void ActionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingList) return;
        _actionFilterDebounce.Signal();
    }

    /// <summary>引用位置下拉：切换立即生效，不等文本搜索的防抖。</summary>
    private void ActionRefFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化填选项 / 选中值由 _buildingActionRows 挡掉；_initializing 只护「通用」页的全局开关，
        // 不该让打开窗口瞬间（自启查询那百毫秒）的下拉点击被吞。
        if (_buildingActionRows) return;
        _actionRefFilterKey = ActionRefFilterCombo.SelectedValue as string ?? ActionReferenceIndex.All;
        _actionFilterDebounce.Now();
    }

    /// <summary>动作管理页顶部「暂存区（N）」：打开面板的暂存区页（草稿不在设置里）。</summary>
    private void OpenStagingPage_Click(object sender, RoutedEventArgs e)
        => Features.SuperPanel.ShowStash();

    private void ActionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DataGridCell 在「自己还没有键盘焦点」时会把这一下 MouseDown 标记成已处理，
        // 单元格里 Focusable="False" 的行内图标就收不到点击（HotkeysView 踩过）。
        var cell = VisualTree.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();
    }

    /// <summary>
    /// 动作行右侧图标的分发器：按 <c>DataContext</c> 认行（点按钮不会自动选中那一行），
    /// 把它设为选中项后复用既有的编辑 / 复制 / 删除逻辑，别另写一套。
    /// </summary>
    private void ActionRowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ActionRow row) return;
        ActionList.SelectedItem = row;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "edit": EditAction_Click(sender, e); break;
            case "copy": DuplicateAction_Click(sender, e); break;
            case "history": OpenActionHistory_Click(sender, e); break;
            case "delete": DeleteAction_Click(sender, e); break;
        }
    }

    /// <summary>
    /// 历史版本：选一份「保存前的样子」，右侧看它和当前的差异，确认后写回。
    /// 快照由 <c>ActionStore</c> 自动存（每次内容真变了才存一份），恢复本身也会留一份，
    /// 所以「恢复错了」还能再退回来。
    /// </summary>
    private void OpenActionHistory_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur == null) return;

        var dlg = new ActionHistoryDialog(this, cur);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        if (!ActionWrites.Update(dlg.Result, out string? restoreError))
        {
            ConfirmDialog.Info(this, "动作历史", restoreError);
            return;
        }

        PopulateActions();
        ActionListChanged();
        ToastService.Show($"已恢复「{cur.Name}」的历史版本（恢复前的版本也进了历史）",
            "动作历史", 3200, ToastKind.success);
    }

    private void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTree.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点行内图标不触发编辑
        if (ActionList.SelectedIndex >= 0) EditAction_Click(sender, e);
    }

    private ActionDto? SelectedAction() =>
        ActionList.SelectedIndex >= 0 && ActionList.SelectedIndex < _actionRows.Count
            ? ActionRegistry.FindById(_actionRows[ActionList.SelectedIndex].Id)
            : null;

    private void NewAction_Click(object sender, RoutedEventArgs e)
    {
        var dto = ActionEditor.Show(this, "新建组合动作", null);
        if (dto == null) return;
        if (!ActionWrites.Add(dto, out string? addError))
        {
            ConfirmDialog.Info(this, "新建组合动作", addError);
            return;
        }
        PopulateActions();
        // 选中新动作
        for (int i = 0; i < _actionRows.Count; i++)
            if (_actionRows[i].Id == dto.Id) { ActionList.SelectedIndex = i; break; }
        ActionListChanged();
    }

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur == null) return;

        // 简单动作开极简表单，组合动作开编辑器——分支只在 PanelActionOps.Edit 一处。
        if (!PanelActionOps.Edit(this, cur.Id)) return;

        PopulateActions();
        for (int i = 0; i < _actionRows.Count; i++)
            if (_actionRows[i].Id == cur.Id) { ActionList.SelectedIndex = i; break; }
        ActionListChanged();
    }

    private void DuplicateAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur == null) return;
        var copy = ActionWrites.Duplicate(cur.Id, out string? copyError);
        if (copy == null)
        {
            ConfirmDialog.Info(this, "复制动作", copyError);
            return;
        }
        PopulateActions();
        for (int i = 0; i < _actionRows.Count; i++)
            if (_actionRows[i].Id == copy.Id) { ActionList.SelectedIndex = i; break; }
        ActionListChanged();
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur != null) DeleteAction(cur.Id);
    }

    /// <summary>删除动作：确认后落盘，并清理快捷键 / 面板 / 自动触发 / 悬浮中的引用。</summary>
    private void DeleteAction(string id)
    {
        if (!ConfirmDialog.Confirm(this, "删除动作", ActionRemoval.ConfirmMessage(id), danger: true)) return;

        if (!ActionWrites.Delete(id, out string? deleteError))
        {
            ConfirmDialog.Info(this, "删除动作", deleteError);
            return;
        }
        RefreshCreatedPages();          // 只刷已经建出来的页（含动作管理），别为没打开过的页强建视图
    }

    /// <summary>visual tree 上溯找最近的 <typeparamref name="T"/> 祖先。</summary>

    /// <summary>visual tree 下溯找第一个 <typeparamref name="T"/> 后代。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }

}
