using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 步骤搜索：在现有列表里**定位**（不筛选，筛选会打乱嵌套与块结构）。
/// </summary>
/// <remarks>
/// 搜的是全部步骤（含已折叠块里的）：命中后自动展开祖先块，再选中并滚动到那一行。
/// `Enter` 下一处、`Shift+Enter` 上一处、`Esc` 清空；多个关键词用空格分开（全部命中才算命中）。
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>当前停在第几个命中（-1 = 还没有）。</summary>
    private int _stepMatchCursor = -1;

    private void StepSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // XAML 解析期 TextChanged 可能比 StepSearchInfo 先到
        if (StepSearchInfo == null) return;
        JumpToStepMatch(delta: 1, restart: true);
    }

    private void StepSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            JumpToStepMatch(delta: back ? -1 : 1, restart: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            StepSearchBox.Clear();
            e.Handled = true;
        }
    }

    /// <summary>跳到上一个 / 下一个命中；<paramref name="restart"/> = 从第一个重新开始（输入变化时）。</summary>
    private void JumpToStepMatch(int delta, bool restart)
    {
        string query = (StepSearchBox.Text ?? "").Trim();
        if (query.Length == 0)
        {
            _stepMatchCursor = -1;
            StepSearchInfo.Visibility = Visibility.Collapsed;
            return;
        }

        List<(Step Step, List<Step> Ancestors)> matches = FindStepMatches(query);
        if (matches.Count == 0)
        {
            _stepMatchCursor = -1;
            StepSearchInfo.Text = "无匹配";
            StepSearchInfo.Visibility = Visibility.Visible;
            return;
        }

        _stepMatchCursor = restart || _stepMatchCursor < 0
            ? 0
            : ((_stepMatchCursor + delta) % matches.Count + matches.Count) % matches.Count;

        var (step, ancestors) = matches[_stepMatchCursor];

        // 命中在折叠块里就先展开，否则选中了也看不见。
        bool expanded = false;
        foreach (Step ancestor in ancestors) expanded |= _collapsed.Remove(ancestor);
        if (expanded) ReloadRows();

        StepRow? row = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, step));
        if (row != null)
        {
            StepList.SelectedItems.Clear();
            StepList.SelectedItem = row;
            StepList.ScrollIntoView(row);
        }

        StepSearchInfo.Text = $"{_stepMatchCursor + 1}/{matches.Count}";
        StepSearchInfo.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按执行顺序找命中（含折叠块）。多个关键词用空格分开，全部命中才算命中；
    /// 匹配范围：单元名 / 单元键 / 摘要 / 备注 / 参数文本 / 输出变量名。
    /// </summary>
    private List<(Step Step, List<Step> Ancestors)> FindStepMatches(string query)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new List<(Step, List<Step>)>();

        return AllSteps()
            .Where(x => Matches(x.Step, words))
            .ToList();
    }

    private static bool Matches(Step step, string[] words)
    {
        string haystack = SearchText(step);
        foreach (string word in words)
            if (!haystack.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string SearchText(Step step)
    {
        var sb = new StringBuilder(96);
        sb.Append(StepSummary.Title(step)).Append(' ')
          .Append(step.Unit).Append(' ')
          .Append(StepSummary.Describe(step)).Append(' ');

        if (!string.IsNullOrWhiteSpace(step.Note)) sb.Append(step.Note).Append(' ');

        foreach (string target in step.Out.Values) sb.Append(target).Append(' ');

        foreach (StepArg arg in step.In.Values)
        {
            switch (arg.Kind)
            {
                case StepArgKind.literal:
                    sb.Append(arg.Literal.AsText()).Append(' ');
                    break;
                case StepArgKind.variable:
                    sb.Append(arg.VariablePath).Append(' ');
                    break;
            }
        }

        return sb.ToString();
    }
}
