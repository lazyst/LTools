using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>
/// 组合动作步骤编排（计划 §10.1）：步骤列表（引用动作 Id + DelayMs + OnFail）+ 拖拽排序 + 增删。
/// 独立于 <see cref="ActionEditorDialog"/>。
/// </summary>
public partial class CompositeEditorDialog : Window
{
    /// <summary>要编排的步骤（构造时写入，确定后由调用方读回）。</summary>
    public List<StepDto> Steps { get; set; } = new();

    private readonly ObservableCollection<StepRow> _rows = new();

    public CompositeEditorDialog(Window owner)
    {
        InitializeComponent();
        StepsList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            ReloadRows();
            StepsList.Focus();
        };
    }

    private void ReloadRows()
    {
        _rows.Clear();
        var strategies = new List<StrategyItem>
        {
            new("继续下一步", null),
            new("中止组合", OnFailStrategy.abort),
        };
        int n = 0;
        foreach (var s in Steps)
        {
            _rows.Add(new StepRow(s, n, strategies)
            {
                ActionLabel = ActionRegistry.DisplayName(s.ActionId),
            });
            n++;
        }
    }

    private void StepsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ActionRegistry.All.Count == 0)
        {
            ConfirmDialog.Info(this, "组合动作", "动作池为空，请先到「动作管理」新建动作。");
            return;
        }
        var picker = new ActionPoolPicker(this, "添加步骤 — 选择动作") { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        Steps.Add(new StepDto { ActionId = picker.Result });
        ReloadRows();
        StepsList.SelectedIndex = StepsList.Items.Count - 1;
    }

    /// <summary>点击步骤里的「引用动作」按钮：更换该步骤引用的动作。</summary>
    private void ChangeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not StepRow row) return;
        var picker = new ActionPoolPicker(this, "更换步骤引用的动作", row.Step.ActionId) { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        row.Step.ActionId = picker.Result;
        row.ActionLabel = ActionRegistry.DisplayName(picker.Result);
        ReloadRows();
        StepsList.SelectedIndex = row.Index;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        int idx = StepsList.SelectedIndex;
        if (idx < 0) return;
        Steps.RemoveAt(idx);
        ReloadRows();
        if (StepsList.Items.Count > 0)
            StepsList.SelectedIndex = Math.Min(idx, StepsList.Items.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int idx = StepsList.SelectedIndex;
        if (idx < 0) return;
        int ni = idx + delta;
        if (ni < 0 || ni >= Steps.Count) return;
        var tmp = Steps[idx];
        Steps[idx] = Steps[ni];
        Steps[ni] = tmp;
        ReloadRows();
        StepsList.SelectedIndex = ni;
    }

    // —— 拖拽排序（把手启动 + 落点按命中行命中测试，不依赖 SelectedIndex）——
    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not StepRow row) return;
        // 选中该行，让「上移/删除」等操作指向它
        StepsList.SelectedIndex = row.Index;
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, row.Index.ToString());
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
        }
        catch { /* 拖拽被取消静默 */ }
    }

    private void StepsList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            int.TryParse(e.Data.GetData(DataFormats.UnicodeText) as string, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void StepsList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.UnicodeText)) return;
        if (e.Data.GetData(DataFormats.UnicodeText) is not string text || !int.TryParse(text, out int from)) return;
        if (from < 0 || from >= Steps.Count) return;
        if (e.OriginalSource is not DependencyObject hit) return;

        // 落点：按实际命中的行容器算索引（拖拽期间 SelectedIndex 仍是旧值，不能用）
        var container = ItemsControl.ContainerFromElement(StepsList, hit) as ListBoxItem;
        if (container == null) return;   // 落在列表空白/外部 → 忽略
        int to = StepsList.ItemContainerGenerator.IndexFromContainer(container);
        if (to < 0 || to == from) { e.Handled = true; return; }

        var moved = Steps[from];
        Steps.RemoveAt(from);
        Steps.Insert(to, moved);
        ReloadRows();
        StepsList.SelectedIndex = to;
        e.Handled = true;
    }

    // —— 确定 ——
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in Steps)
        {
            if (string.IsNullOrWhiteSpace(s.ActionId)) { ConfirmDialog.Info(this, "组合动作", "存在未指定动作的步骤。"); return; }
            if (ActionRegistry.FindById(s.ActionId) == null) { ConfirmDialog.Info(this, "组合动作", $"步骤引用的动作不存在: {s.ActionId}"); return; }
        }
        DialogResult = true;
    }

    /// <summary>步骤行展示模型；属性直接读写底层 <see cref="StepDto"/>，绑定编辑即落回数据。</summary>
    private sealed class StepRow
    {
        public StepRow(StepDto step, int index, IReadOnlyList<StrategyItem> strategies)
        {
            Step = step;
            Index = index;
            Number = index + 1;
            StrategyOptions = strategies;
        }
        public int Index { get; }
        public int Number { get; }
        public string ActionLabel { get; set; } = "";
        public IReadOnlyList<StrategyItem> StrategyOptions { get; }
        public StepDto Step { get; }
        public int DelayMs { get => Step.DelayMs; set => Step.DelayMs = value; }
        public OnFailStrategy? OnFail { get => Step.OnFail; set => Step.OnFail = value; }
    }

    private sealed record StrategyItem(string Label, OnFailStrategy? Value)
    {
        public override string ToString() => Label;
    }
}
