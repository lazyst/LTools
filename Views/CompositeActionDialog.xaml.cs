using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>
/// 组合动作编辑器（计划 §10.1）：左「动作来源」（内部动作 / 动作池，双击或拖入）·
/// 中「步骤编排」（拖拽排序 / 双击就地编辑 / 延迟 / 失败策略）· 右「组合属性」（名称 / 图标 / 失败策略）。
/// </summary>
/// <remarks>
/// 步骤**内嵌动作快照**（与动作池解耦，见 §12）：加入步骤即深拷贝，改池动作不影响已加步骤；
/// **禁止嵌套组合**（左栏动作池已排除组合动作，＋新建步骤类型也不含组合）。
/// 用户点「改为普通动作…」置 <see cref="JumpToNormal"/>，由 <see cref="ActionEditor"/> 改开普通编辑器（不带数据）。
/// </remarks>
public partial class CompositeActionDialog : Window
{
    private readonly ActionDto _draft;
    private readonly List<StepDto> _steps;
    private readonly ObservableCollection<StepRow> _rows = new();

    // 左栏拖拽（阈值触发，避免与双击冲突）
    private LeftItem? _leftDragCandidate;
    private Point _leftDragOrigin;
    private static LeftItem? _dragLeftItem;   // DoDragDrop 期间供中间区 Drop 读取

    public CompositeActionDialog(string title, ActionDto? existing)
    {
        InitializeComponent();
        _draft = existing != null
            ? existing.Clone()
            : new ActionDto { Type = ActionType.composite, Icon = IconCatalog.Default };
        if (!string.IsNullOrEmpty(title)) Title = title;
        _steps = _draft.Steps != null ? new List<StepDto>(_draft.Steps) : new List<StepDto>();

        NameBox.Text = _draft.Name;
        IconPicker.SelectedIcon = _draft.Icon;
        AbortBox.IsChecked = _draft.CompositeOnFail == OnFailStrategy.abort;
        StepsList.ItemsSource = _rows;
        ReloadRows();
        BuildSourceList();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>返回值；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>用户点了「改为普通动作…」：由 <see cref="ActionEditor"/> 改开普通编辑器（不带数据跳转）。</summary>
    public bool JumpToNormal { get; private set; }

    // ============ 左栏：动作来源 ============

    private void BuildSourceList()
    {
        string q = (SearchBox.Text ?? "").Trim();
        SourceList.Children.Clear();

        // 内部动作（6 个内置命令）→ 内联 internal 快照
        var internals = InternalActionRegistry.Commands
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => new LeftItem { Label = ActionEditorDialog.FormatCommand(c), Glyph = "\uE756", InternalCommand = c })
            .Where(i => q.Length == 0 || i.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        AddGroup("内部动作", internals);

        // 动作池（排除组合动作，避免嵌套）→ 深拷贝快照
        var pool = ActionRegistry.All
            .Where(a => a.Type != ActionType.composite)
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .Select(a => new LeftItem { Label = a.Name, Glyph = IconCatalog.GetGlyph(a.Icon), PoolActionId = a.Id })
            .Where(i => q.Length == 0 || i.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        AddGroup("动作池", pool);

        if (internals.Count == 0 && pool.Count == 0)
            SourceList.Children.Add(new TextBlock
            {
                Text = "（无匹配）",
                Margin = new Thickness(12, 4, 0, 0),
                Foreground = (Brush)FindResource("HintTextBrush"),
            });
    }

    private void AddGroup(string title, List<LeftItem> items)
    {
        if (items.Count == 0) return;
        SourceList.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionLabel"),
            Margin = new Thickness(12, 10, 0, 4),
        });
        foreach (var it in items) SourceList.Children.Add(BuildLeftButton(it));
    }

    private Button BuildLeftButton(LeftItem item)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = item.Glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var btn = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 1, 8, 1),
            MinHeight = 30,
            Cursor = Cursors.Hand,
        };
        btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { AppendStep(item); _leftDragCandidate = null; return; }
            _leftDragCandidate = item;
            _leftDragOrigin = e.GetPosition(this);
        };
        return btn;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_leftDragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _leftDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _leftDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _leftDragCandidate;
        _leftDragCandidate = null;
        _dragLeftItem = item;
        try { DragDrop.DoDragDrop(this, new DataObject("clp.left", "1"), DragDropEffects.Copy); }
        catch { /* 拖拽取消静默 */ }
        finally { _dragLeftItem = null; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildSourceList();

    private void NewStep_Click(object sender, RoutedEventArgs e)
    {
        var dto = ActionEditorDialog.Show(this, "新建步骤", null, allowComposite: false);
        if (dto == null) return;
        var a = dto.Clone();
        a.Id = "";                                              // 内嵌快照不占动作池 Id
        if (a.Type == ActionType.composite) a.Type = ActionType.runCommand;   // 防御：禁止嵌套
        _steps.Add(new StepDto { Action = a });
        ReloadRows();
        StepsList.SelectedIndex = _steps.Count - 1;
    }

    private void AppendStep(LeftItem item)
    {
        var step = MakeStep(item);
        if (step == null) return;
        _steps.Add(step);
        ReloadRows();
        StepsList.SelectedIndex = _steps.Count - 1;
    }

    /// <summary>左栏项 → 步骤（深拷贝池动作 / 构造内联内部动作）。</summary>
    private static StepDto? MakeStep(LeftItem item)
    {
        if (item.InternalCommand != null)
            return new StepDto
            {
                Action = new ActionDto
                {
                    Type = ActionType.@internal,
                    Command = item.InternalCommand,
                    Name = item.Label,
                    Icon = IconCatalog.Default,
                },
            };
        if (item.PoolActionId != null)
        {
            var a = ActionRegistry.FindById(item.PoolActionId);
            if (a == null || a.Type == ActionType.composite) return null;
            var copy = a.Clone();
            copy.Id = "";
            return new StepDto { Action = copy };
        }
        return null;
    }

    // ============ 中间：步骤编排 ============

    private void ReloadRows()
    {
        _rows.Clear();
        var strategies = new List<StrategyItem>
        {
            new("继续下一步", null),
            new("中止组合", OnFailStrategy.abort),
        };
        for (int i = 0; i < _steps.Count; i++)
            _rows.Add(new StepRow(_steps[i], i, strategies));
        EmptyHint.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StepsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource)) return;   // 别把延迟框/下拉的双击当成编辑行
        if (StepsList.SelectedItem is StepRow row) EditStep(row);
    }

    private void EditStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is StepRow row) EditStep(row);
    }

    private void EditStep(StepRow row)
    {
        var dto = ActionEditorDialog.Show(this, "编辑步骤", row.Step.Action, allowComposite: false);
        if (dto == null || dto.Type == ActionType.composite) return;
        var a = dto.Clone();
        a.Id = "";
        row.Step.Action = a;
        int idx = _steps.IndexOf(row.Step);
        ReloadRows();
        if (idx >= 0 && idx < _rows.Count) StepsList.SelectedIndex = idx;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not StepRow row) return;
        int idx = _steps.IndexOf(row.Step);
        if (idx < 0) return;
        _steps.RemoveAt(idx);
        ReloadRows();
        if (_steps.Count > 0) StepsList.SelectedIndex = Math.Min(idx, _steps.Count - 1);
    }

    private void Delay_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    // —— 拖拽（左栏项拖入 = 插入；把手拖动 = 排序）——

    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not StepRow row) return;
        int idx = _steps.IndexOf(row.Step);
        if (idx < 0) return;
        StepsList.SelectedIndex = idx;
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, idx.ToString());
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
        }
        catch { /* 拖拽取消静默 */ }
    }

    private void StepsList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragLeftItem != null) { e.Effects = DragDropEffects.Copy; e.Handled = true; return; }
        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            int.TryParse(e.Data.GetData(DataFormats.UnicodeText) as string, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void StepsList_Drop(object sender, DragEventArgs e)
    {
        // 左栏项 → 插入步骤
        if (_dragLeftItem != null)
        {
            var step = MakeStep(_dragLeftItem);
            if (step != null)
            {
                int to = Math.Clamp(DropIndex(e), 0, _steps.Count);
                _steps.Insert(to, step);
                ReloadRows();
                StepsList.SelectedIndex = to;
            }
            e.Handled = true;
            return;
        }

        // 把手排序
        if (e.Data.GetDataPresent(DataFormats.UnicodeText) &&
            e.Data.GetData(DataFormats.UnicodeText) is string text && int.TryParse(text, out int from))
        {
            if (from < 0 || from >= _steps.Count) return;
            int to = DropIndex(e);
            if (to < 0 || to == from) { e.Handled = true; return; }
            var moved = _steps[from];
            _steps.RemoveAt(from);
            if (to > from) to--;   // 移除后索引前移
            _steps.Insert(Math.Clamp(to, 0, _steps.Count), moved);
            ReloadRows();
            StepsList.SelectedIndex = to;
            e.Handled = true;
        }
    }

    /// <summary>落点索引：命中行 → 该行索引；空白/末尾 → 追加到末尾。</summary>
    private int DropIndex(DragEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit)
        {
            var container = ItemsControl.ContainerFromElement(StepsList, hit) as ListBoxItem;
            if (container != null)
            {
                int i = StepsList.ItemContainerGenerator.IndexFromContainer(container);
                if (i >= 0) return i;
            }
        }
        return _steps.Count;
    }

    // ============ 右栏 / 底部 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ConfirmDialog.Info(this, "组合动作", "名称不能为空。"); NameBox.Focus(); return; }
        if (_steps.Count == 0) { ConfirmDialog.Info(this, "组合动作", "组合动作至少需要一个步骤。"); return; }

        _draft.Name = name;
        _draft.Type = ActionType.composite;
        _draft.Icon = IconPicker.SelectedIcon ?? IconCatalog.Default;
        _draft.Steps = _steps;
        _draft.CompositeOnFail = AbortBox.IsChecked == true ? OnFailStrategy.abort : null;
        Result = _draft;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ToNormal_Click(object sender, RoutedEventArgs e)
    {
        JumpToNormal = true;
        DialogResult = false;
    }

    // ============ 辅助 ============

    /// <summary>原始命中的元素是否落在交互控件（TextBox/ComboBox/Button）内——用于避免误触行双击编辑。</summary>
    private static bool IsInteractive(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is TextBox || d is ComboBox || d is Button) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private sealed class LeftItem
    {
        public string Label { get; set; } = "";
        public string Glyph { get; set; } = "";
        public string? InternalCommand { get; set; }
        public string? PoolActionId { get; set; }
    }

    /// <summary>步骤行展示模型；属性直接读写底层 <see cref="StepDto"/>，绑定编辑即落回数据。</summary>
    private sealed class StepRow
    {
        public StepRow(StepDto step, int index, IReadOnlyList<StrategyItem> strategies)
        {
            Step = step;
            Number = index + 1;
            StrategyOptions = strategies;
            TypeBadge = ActionTypeLabel.Of(step.Action.Type);
            ActionName = string.IsNullOrEmpty(step.Action.Name) ? "（未命名）" : step.Action.Name;
        }
        public int Number { get; }
        public string TypeBadge { get; }
        public string ActionName { get; }
        public IReadOnlyList<StrategyItem> StrategyOptions { get; }
        public StepDto Step { get; }
        public int DelayMs { get => Step.DelayMs; set => Step.DelayMs = value; }
        public int OnFailIndex
        {
            get => Step.OnFail == OnFailStrategy.abort ? 1 : 0;
            set => Step.OnFail = value == 1 ? OnFailStrategy.abort : null;
        }
    }

    private sealed record StrategyItem(string Label, OnFailStrategy? Value)
    {
        public override string ToString() => Label;
    }
}
