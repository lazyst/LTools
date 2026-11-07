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
/// 动作编辑器（W4）：左「工具箱」· 中「步骤」· 右「变量 + 属性」。
/// </summary>
/// <remarks>
/// <para>
/// 步骤是**扁平行**：嵌套用缩进 +「否则 / 结束如果 / 结束循环」标记行表达；
/// 空分支有一行可选的占位行，选中后从左侧添加即可插进该分支。
/// </para>
/// <para>
/// 工具箱完全由 <see cref="StepRegistry"/> 的 <see cref="UnitSpec"/> 生成：
/// 新增一个单元不需要改这个窗口（W4 验收）。
/// </para>
/// <para>保存契约不变：<see cref="Result"/> 由调用方 <c>ConfigStore.AddAction</c> / <c>UpdateAction</c> 落盘。</para>
/// </remarks>
public partial class ActionEditorWindow : Window
{
    private readonly ActionDto? _draft;
    private readonly SubActionDef? _subDef;
    private readonly bool _isSubAction;
    private readonly ActionProgram _program;
    private readonly ObservableCollection<StepRow> _rows = new();
    private readonly List<VariableRowUi> _variables = new();
    private readonly DragVisuals _dragGhost;
    private int _number;

    // —— 试运行 ——
    private readonly ObservableCollection<TraceRow> _traceRows = new();
    private readonly ObservableCollection<SnapshotRow> _snapshotRows = new();
    private readonly List<StepTrace> _pendingTraces = new();
    private readonly Dictionary<string, TraceRow> _traceRowsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepRow> _traceTargets = new(StringComparer.Ordinal);
    private DispatcherTimer? _traceFlush;
    private bool _running;

    /// <summary>程序化改变量类型时置位，避免被当成「用户改过」。</summary>
    private bool _syncingKinds;

    // 拖动排序（只移动步骤本身，子步骤跟着走；标记行 / 空分支占位行不是落点）
    private StepRow? _dragRow;
    private int _dragRowIndex;
    private Point _dragOrigin;
    private bool _dragPending;
    private bool _dragging;
    private Border? _insertLine;
    private (List<Step> List, int Index)? _dropTarget;

    public ActionEditorWindow(string title, ActionDto? existing)
        : this(title, existing, null) { }

    /// <summary>
    /// 编辑全局子动作（变量多一列「方向」，用来标记输入 / 输出）。
    /// id 为空时自动生成——id 是对外调用名，生成后不再让用户随手改。
    /// </summary>
    public static ActionEditorWindow ForSubAction(string title, SubActionDef? existing)
    {
        var def = existing?.Clone() ?? new SubActionDef();
        if (string.IsNullOrWhiteSpace(def.Id)) def.Id = NewSubActionId();
        return new ActionEditorWindow(title, null, def);
    }

    private ActionEditorWindow(string title, ActionDto? existing, SubActionDef? subAction)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        _subDef = subAction;
        _isSubAction = subAction != null;

        if (_isSubAction)
        {
            // 子动作与动作同构：步骤 + 变量，差别只在变量的「方向」标记。
            _program = new ActionProgram
            {
                Id = subAction!.Id,
                Name = subAction.Name,
                Icon = subAction.Icon,
                Description = subAction.Description,
            };
            foreach (var variable in subAction.Variables) _program.Variables.Add(variable.Clone());
            foreach (var step in subAction.Steps) _program.Steps.Add(step.Clone());
            foreach (var child in subAction.SubActions) _program.SubActions.Add(child.Clone());
            _draft = null;

            IconSection.Visibility = Visibility.Collapsed;
            CodeHint.Text = "调用名：" + _program.Id;
            CodeHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "（空子动作）从左侧点一个单元开始，例如：设置变量 → 提示弹窗";
            // 多一列「方向」，右栏放宽一点，否则默认值、图标会被挤没
            RightColumn.Width = new GridLength(360);
        }
        else if (existing != null)
        {
            _draft = existing.Clone();
            _program = ActionPrograms.For(_draft).Clone();
            _draft.Program = _program;
            DirectionColumn.Width = new GridLength(0);
            DirectionHeader.Visibility = Visibility.Collapsed;
        }
        else
        {
            _draft = new ActionDto { Type = ActionType.composite, Icon = IconCatalog.Default };
            _program = new ActionProgram();
            _draft.Program = _program;
            DirectionColumn.Width = new GridLength(0);
            DirectionHeader.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        NameBox.Text = _isSubAction ? _program.Name : _draft!.Name;
        string? icon = _isSubAction ? _program.Icon : _draft!.Icon;
        IconPicker.SelectedIcon = string.IsNullOrWhiteSpace(icon) ? IconCatalog.Default : icon;

        StepList.ItemsSource = _rows;
        StepList.PreviewKeyDown += StepList_PreviewKeyDown;

        _dragGhost = new DragVisuals(this, GhostLayer);
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragUp;
        PreviewKeyDown += OnDragKeyDown;

        BuildVariableRows();
        BuildToolbox();
        ReloadRows();

        TraceList.ItemsSource = _traceRows;
        SnapshotList.ItemsSource = _snapshotRows;

        // 关窗时别把正在跑的试运行留在后台（它会继续操作键鼠）。
        Closed += (_, _) =>
        {
            _traceFlush?.Stop();
            if (_running) AutomationRunner.Stop();
        };

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>确定后的动作；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>子动作模式下的结果；取消时为 null。</summary>
    public SubActionDef? SubResult { get; private set; }

    private static string NewSubActionId() =>
        "sub-" + Guid.NewGuid().ToString("N")[..8];

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
                _rows.Add(MarkerRow("否则", depth));
                AddBranch(step.Else, depth + 1, "假分支为空 · 选中这一行后从左侧添加");
                _rows.Add(MarkerRow("结束如果", depth));
            }
            else if (body == BodyKind.body)
            {
                AddBranch(step.Body, depth + 1, "循环体为空 · 选中这一行后从左侧添加");
                _rows.Add(MarkerRow("结束循环", depth));
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

    private static StepRow MarkerRow(string text, int depth) => new()
    {
        Depth = depth,
        IsMarker = true,
        MarkerText = text,
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
        new(step, program: _program, variableNames: VariableNames) { Owner = this };

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

    // ============ 拖动排序 ============

    /// <summary>
    /// 按住某一步就开始「可能的拖动」；超过 4px 才真正进入拖动（照全项目统一的拖动手感）。
    /// 只移动被拖的那一步（子步骤跟着走），落在别的分支里就是「移入该分支」。
    /// </summary>
    private void StepList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<Button>((DependencyObject)e.OriginalSource) != null) return;

        StepRow? row = RowAt(e.OriginalSource as DependencyObject);
        if (row?.Step == null || row.Owner == null) return;   // 标记行 / 空分支占位行不拖

        _dragRow = row;
        _dragRowIndex = _rows.IndexOf(row);
        _dragOrigin = e.GetPosition(this);
        _dragPending = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending && !_dragging) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        Point p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragOrigin.X) < 4 && Math.Abs(p.Y - _dragOrigin.Y) < 4) return;
            if (!StartDrag(p)) return;
        }

        _dragGhost.MoveGhost(p);
        UpdateDropTarget(p);
        e.Handled = true;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (!_dragging)
        {
            _dragPending = false;
            _dragRow = null;
            return;
        }

        StepRow? row = _dragRow;
        var target = _dropTarget;
        EndDragVisuals();

        if (row?.Step != null && row.Owner != null && target is { } t)
            MoveStepTo(row, t.List, t.Index);

        _dragRow = null;
        e.Handled = true;
    }

    private void OnDragKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!_dragPending && !_dragging)) return;
        CancelDrag();
        e.Handled = true;
    }

    private bool StartDrag(Point p)
    {
        if (_dragRow == null) return false;
        if (StepList.ItemContainerGenerator.ContainerFromIndex(_dragRowIndex) is not ListBoxItem row)
        {
            _dragPending = false;
            return false;
        }

        _dragGhost.ShowGhost(row, p);
        row.Opacity = 0.35;
        StepList.SelectedItem = _dragRow;
        _dragPending = false;
        _dragging = true;
        return true;
    }

    private void UpdateDropTarget(Point windowPoint)
    {
        Point listPos = TranslatePoint(windowPoint, StepList);
        if (listPos.X < 0 || listPos.Y < 0
            || listPos.X > StepList.ActualWidth || listPos.Y > StepList.ActualHeight) return;

        if (StepList.InputHitTest(listPos) is not DependencyObject hit) return;
        if (ItemsControl.ContainerFromElement(StepList, hit) is not ListBoxItem container) return;

        int hover = StepList.ItemContainerGenerator.IndexFromContainer(container);
        if (hover < 0 || hover >= _rows.Count) return;

        StepRow target = _rows[hover];
        List<Step>? list;
        int index;
        bool after;

        if (target.Step != null && target.Owner != null)
        {
            after = listPos.Y
                    >= container.TranslatePoint(new Point(0, 0), StepList).Y + container.ActualHeight / 2;
            list = target.Owner;
            index = target.Index + (after ? 1 : 0);
        }
        else if (target.IsPlaceholder && target.Owner != null)
        {
            after = false;   // 空分支：放到分支里
            list = target.Owner;
            index = 0;
        }
        else
        {
            return;          // 否则 / 结束如果 / 结束循环 这类标记行不是落点
        }

        // 不能把一步拖进它自己的分支里
        if (_dragRow?.Step != null && ContainsList(_dragRow.Step, list)) return;

        _dropTarget = (list, index);
        ShowInsertLine(container, target.Depth, after);
    }

    private void ShowInsertLine(ListBoxItem container, int depth, bool after)
    {
        _insertLine ??= new Border
        {
            Height = 2,
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(1),
        };
        if (!GhostLayer.Children.Contains(_insertLine)) GhostLayer.Children.Add(_insertLine);

        double indent = depth * 18;
        Point origin = container.TranslatePoint(new Point(0, 0), GhostLayer);
        _insertLine.Width = Math.Max(12, container.ActualWidth - indent);
        Canvas.SetLeft(_insertLine, origin.X + indent);
        Canvas.SetTop(_insertLine, origin.Y + (after ? container.ActualHeight : 0) - 1);
    }

    private void EndDragVisuals()
    {
        _dragGhost.End();
        if (_insertLine != null) GhostLayer.Children.Remove(_insertLine);
        _insertLine = null;
        _dropTarget = null;
        _dragging = false;
        _dragPending = false;

        // 容器会被回收复用，逐行复位不透明度
        for (int i = 0; i < _rows.Count; i++)
            if (StepList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row)
                row.Opacity = 1;
    }

    private void CancelDrag()
    {
        EndDragVisuals();
        _dragRow = null;
    }

    private void MoveStepTo(StepRow row, List<Step> list, int index)
    {
        Step step = row.Step!;
        List<Step> from = row.Owner!;
        int fromIndex = row.Index;

        if (ContainsList(step, list)) return;   // 不能把一步挪进它自己的分支

        int insert = index;
        if (ReferenceEquals(from, list) && insert > fromIndex) insert--;
        if (ReferenceEquals(from, list) && insert == fromIndex) return;

        from.RemoveAt(fromIndex);
        list.Insert(Math.Clamp(insert, 0, list.Count), step);
        ReloadRows(step);
    }

    /// <summary>该步（或它的任意后代）的分支列表是不是 <paramref name="list"/>。</summary>
    private static bool ContainsList(Step step, List<Step> list)
    {
        if (ReferenceEquals(step.Then, list) || ReferenceEquals(step.Else, list)
            || ReferenceEquals(step.Body, list)) return true;

        foreach (var child in step.Then) if (ContainsList(child, list)) return true;
        foreach (var child in step.Else) if (ContainsList(child, list)) return true;
        foreach (var child in step.Body) if (ContainsList(child, list)) return true;

        return false;
    }

    private StepRow? RowAt(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem)
        {
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        if (source is not ListBoxItem item) return null;
        int index = StepList.ItemContainerGenerator.IndexFromContainer(item);
        return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    // ============ 变量 ============

    private static readonly (ValueKind Kind, string Label)[] Kinds =
    {
        (ValueKind.text, "文本"),
        (ValueKind.number, "数字"),
        (ValueKind.@bool, "布尔"),
        (ValueKind.list, "列表"),
        (ValueKind.@object, "对象"),
    };

    private static readonly (VariableDirection Direction, string Label)[] Directions =
    {
        (VariableDirection.input, "输入"),
        (VariableDirection.local, "本地"),
        (VariableDirection.output, "输出"),
    };

    private void BuildVariableRows()
    {
        VariableRows.Children.Clear();
        _variables.Clear();

        foreach (var def in _program.Variables)
            AddVariableRow(def.Key, def.Type, def.Default, def.Direction);
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        AddVariableRow("", ValueKind.text, null, VariableDirection.local);
        _variables[^1].Name.Focus();
    }

    private VariableRowUi AddVariableRow(string key, ValueKind kind, Value? defaultValue,
        VariableDirection direction = VariableDirection.local,
        string origin = "", bool autoType = false)
    {
        var row = new VariableRowUi();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = _isSubAction ? new GridLength(76) : new GridLength(0),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        row.Name = new TextBox
        {
            MinHeight = 26,
            Text = key,
            Margin = new Thickness(0, 0, 4, 0),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = (double)FindResource("FontSizeHint"),
        };

        row.Type = new ComboBox
        {
            MinHeight = 26,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = (double)FindResource("FontSizeHint"),
            ItemsSource = Kinds.Select(k => new KindChoice(k.Kind, k.Label)).ToList(),
        };
        row.Type.SelectedItem = ((List<KindChoice>)row.Type.ItemsSource)
            .FirstOrDefault(k => k.Kind == kind);

        // 用户自己改过类型 = 表态，之后不再被步骤声明覆盖。
        row.Type.SelectionChanged += (_, _) =>
        {
            if (_syncingKinds) return;
            row.AutoType = false;
        };

        row.Default = new TextBox
        {
            MinHeight = 26,
            Text = defaultValue?.AsText() ?? "",
            FontSize = (double)FindResource("FontSizeHint"),
        };

        var remove = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = (double)FindResource("FontSizeHint"),
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = "删除变量声明；仍被步骤使用的会先提示",
        };
        remove.Click += (_, _) =>
        {
            string name = row.Name.Text.Trim();
            int refs = name.Length == 0 ? 0 : ReferenceCount(name);
            if (refs > 0)
            {
                // 步骤还写着这个输出变量，删了会被下次同步自动加回来，还会让步骤引用空变量。
                ConfirmDialog.Info(this, "删除变量",
                    $"「${{{name}}}」还被 {refs} 处步骤用作输出变量。\n" +
                    "先改掉那些步骤的「输出变量」（或删掉步骤），再删这里。");
                return;
            }

            _variables.Remove(row);
            VariableRows.Children.Remove(grid);
        };

        grid.Children.Add(row.Name);
        Grid.SetColumn(row.Type, 1);
        grid.Children.Add(row.Type);
        Grid.SetColumn(row.Default, 3);
        grid.Children.Add(row.Default);

        if (_isSubAction)
        {
            row.Direction = new ComboBox
            {
                MinHeight = 26,
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = (double)FindResource("FontSizeHint"),
                ItemsSource = Directions.Select(d => new DirectionChoice(d.Direction, d.Label)).ToList(),
            };
            row.Direction.SelectedItem = ((List<DirectionChoice>)row.Direction.ItemsSource)
                .FirstOrDefault(d => d.Direction == direction);
            Grid.SetColumn(row.Direction, 2);
            grid.Children.Add(row.Direction);
        }

        Grid.SetColumn(remove, 4);
        grid.Children.Add(remove);

        // 第二行小字：这个变量是谁写进来的。窄面板放不下独立列，就放行下。
        row.OriginText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("HintTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 3),
        };
        Grid.SetRow(row.OriginText, 1);
        Grid.SetColumn(row.OriginText, 0);
        Grid.SetColumnSpan(row.OriginText, 4);
        grid.Children.Add(row.OriginText);

        row.Host = grid;
        row.Origin = origin;
        row.AutoType = autoType;
        row.Name.TextChanged += (_, _) => RefreshVariableTips();
        _variables.Add(row);
        VariableRows.Children.Add(grid);
        RefreshVariableTips();
        return row;
    }

    /// <summary>变量表每次重建后重算提示：来源 + 被几处步骤写入。</summary>
    private void RefreshVariableTips()
    {
        foreach (var row in _variables)
        {
            string name = row.Name.Text.Trim();
            if (name.Length == 0)
            {
                row.OriginText.Text = row.Origin;
                row.Host.ToolTip = "名字为空，保存时会忽略这一行";
                continue;
            }

            int refs = ReferenceCount(name);
            string origin = row.Origin.Length == 0 ? "手动创建" : row.Origin;
            row.OriginText.Text = origin;
            row.OriginText.ToolTip = refs == 0
                ? "还没有步骤写入它"
                : $"被 {refs} 处步骤写入";
            row.Host.ToolTip = "来源：" + origin +
                (refs == 0 ? "\n还没有步骤写入它" : $"\n被 {refs} 处步骤写入");
        }
    }

    /// <summary>有多少步骤把这个名字当作输出变量（只数 <see cref="Step.Out"/> 的写入点）。</summary>
    private int ReferenceCount(string name)
    {
        int count = 0;
        foreach (var row in _rows)
        {
            if (row.Step == null) continue;
            foreach (string target in row.Step.Out.Values)
                if (string.Equals((target ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                    count++;
        }

        return count;
    }

    /// <summary>
    /// 步骤输出用到、还没声明的变量补进变量表；类型取单元声明（旧版本一律登记成文本，那是错的）。
    /// 已在表里的只校正「类型没被用户改过」的行；来源写进行提示，方便看出这个变量是谁写的。
    /// </summary>
    private void EnsureOutputVariables()
    {
        var known = new Dictionary<string, VariableRowUi>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _variables)
        {
            string key = row.Name.Text.Trim();
            if (key.Length > 0) known.TryAdd(key, row);
        }

        foreach (var stepRow in _rows)
        {
            Step? step = stepRow.Step;
            if (step == null) continue;

            foreach (var pair in step.Out)
            {
                string key = (pair.Value ?? "").Trim();
                if (key.Length == 0) continue;

                ValueKind? declared = OutputType(step, pair.Key);
                string origin = $"第 {stepRow.Number} 步 · {stepRow.Title}（输出 {pair.Key}）";

                if (known.TryGetValue(key, out var existing))
                {
                    if (existing.AutoType && declared != null) SelectKind(existing.Type, declared.Value);
                    if (existing.Origin.Length == 0) existing.Origin = origin;
                    continue;
                }

                known[key] = AddVariableRow(key, declared ?? ValueKind.text, null,
                    VariableDirection.local, origin, autoType: true);
            }
        }

        RefreshVariableTips();
    }

    /// <summary>某个步骤的某个输出键声明了什么类型（子动作按它的输出变量声明）。</summary>
    private ValueKind? OutputType(Step step, string key)
    {
        if (step.Unit == "sub.run")
        {
            string id = step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
            var def = SubActionStore.Resolve(_program, id);
            return def?.Variables.FirstOrDefault(v =>
                string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))?.Type;
        }

        if (!StepRegistry.Default.TryGet(step.Unit, out var unit)) return null;
        return unit.Spec.Outputs.FirstOrDefault(o =>
            string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))?.Type;
    }

    /// <summary>程序化改类型（不把它当成「用户改过」）。</summary>
    private void SelectKind(ComboBox combo, ValueKind kind)
    {
        if (combo.ItemsSource is not List<KindChoice> items) return;
        var match = items.FirstOrDefault(k => k.Kind == kind);
        if (match == null || ReferenceEquals(combo.SelectedItem, match)) return;

        _syncingKinds = true;
        try { combo.SelectedItem = match; }
        finally { _syncingKinds = false; }
    }

    private List<VariableDef> ReadVariables()
    {
        var list = new List<VariableDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _variables)
        {
            string key = row.Name.Text.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;

            ValueKind kind = (row.Type.SelectedItem as KindChoice)?.Kind ?? ValueKind.text;
            string text = row.Default.Text ?? "";

            Value? value = kind switch
            {
                ValueKind.number when double.TryParse(text.Trim(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double number) => Value.FromNumber(number),
                ValueKind.@bool when bool.TryParse(text.Trim(), out bool flag) => Value.FromBool(flag),
                ValueKind.text when text.Length > 0 => Value.FromText(text),
                _ => null,
            };

            list.Add(new VariableDef
            {
                Key = key,
                Type = kind,
                Default = value,
                Direction = (row.Direction?.SelectedItem as DirectionChoice)?.Direction
                    ?? VariableDirection.local,
            });
        }

        return list;
    }

    // ============ 保存 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ConfirmDialog.Info(this, _isSubAction ? "子动作" : "动作",
                _isSubAction ? "给子动作起一个名字。" : "给动作起一个名字。");
            NameBox.Focus();
            return;
        }

        _program.Name = name;
        _program.Icon = IconPicker.SelectedIcon ?? "";

        _program.Variables.Clear();
        foreach (var variable in ReadVariables()) _program.Variables.Add(variable);

        // 输出变量永远在变量表里（写配置的人不用再手动补声明）。
        // 子动作不自动补：那里「本地 / 输出」是刻意标记的，补一条反而要用户再改。
        if (!_isSubAction)
        {
            var declared = new HashSet<string>(
                _program.Variables.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var step in _program.Steps.SelectMany(s => s.SelfAndDescendants()))
            {
                foreach (string raw in step.Out.Values)
                {
                    string key = (raw ?? "").Trim();
                    if (key.Length == 0 || !declared.Add(key)) continue;
                    _program.Variables.Add(new VariableDef { Key = key, Type = ValueKind.text });
                }
            }
        }

        if (_isSubAction)
        {
            var def = _subDef!;
            def.Name = name;
            def.Icon = _program.Icon;

            def.Variables.Clear();
            foreach (var variable in _program.Variables) def.Variables.Add(variable);

            def.Steps.Clear();
            foreach (var step in _program.Steps) def.Steps.Add(step);

            SubResult = def;
            DialogResult = true;
            return;
        }

        _program.Id = _draft!.Id ?? "";

        _draft.Name = name;
        _draft.Icon = _program.Icon;
        _draft.Type = ActionType.composite;
        // 用新编辑器保存过 = 已是新格式，不再回写 v1 字段
        _draft.ClearLegacy();
        _draft.Program = _program;

        Result = _draft;
        DialogResult = true;
    }

    // ============ 试运行 ============

    private void RunToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = RunBody.Visibility != Visibility.Visible;
        RunBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RunToggleButton.Content = show ? "收起" : "展开";
    }

    private void Run_Click(object sender, RoutedEventArgs e) => StartRun(fromSelected: false);

    private void RunFrom_Click(object sender, RoutedEventArgs e) => StartRun(fromSelected: true);

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        AutomationRunner.Stop();
        RunStatus.Text = "正在停止…";
    }

    /// <summary>
    /// 试运行：跑的是**编辑器里现在的内容**（克隆一份，不必先保存），轨迹按步骤路径回填。
    /// </summary>
    private void StartRun(bool fromSelected)
    {
        if (_running) return;

        string? startPath = null;
        if (fromSelected)
        {
            StepRow? row = SelectedRow();
            if (row?.Step == null || !ReferenceEquals(row.Owner, _program.Steps))
            {
                ConfirmDialog.Info(this, "试运行",
                    "「从此行」只支持顶层步骤：先选中左侧最外层的那一行为起点。");
                return;
            }
            startPath = $"/steps/{row.Index}";
        }

        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) name = "未命名动作";

        ActionProgram program = BuildRunProgram();

        // 路径 → 步骤行：等宽两条遍历（程序本身 + 编辑器行），运行时靠路径定位到某一行。
        _traceTargets.Clear();
        var paths = new List<string>();
        IndexStepPaths(_program.Steps, "", paths);
        var realRows = _rows.Where(r => r.Step != null).ToList();
        for (int i = 0; i < paths.Count && i < realRows.Count; i++)
            _traceTargets[paths[i]] = realRows[i];

        _traceRowsByPath.Clear();
        _traceRows.Clear();
        _snapshotRows.Clear();
        lock (_pendingTraces) _pendingTraces.Clear();

        RunBody.Visibility = Visibility.Visible;
        RunToggleButton.Content = "收起";
        SetStatus(fromSelected ? "试运行（从此行）" : "试运行中", hint: true);

        if (!AutomationRunner.TryRunProgram(name, program, startPath, OnTrace, OnRunCompleted))
        {
            SetStatus("已有动作正在运行，先等它结束或按停止", hint: false);
            return;
        }

        SetRunning(true);
        StartTraceFlush();
    }

    /// <summary>克隆当前编辑器内容：步骤是活的模型，变量表要现读（保存前 <see cref="_program.Variables"/> 是旧的）。</summary>
    private ActionProgram BuildRunProgram()
    {
        ActionProgram program = _program.Clone();
        program.Variables.Clear();
        foreach (var variable in ReadVariables()) program.Variables.Add(variable.Clone());
        return program;
    }

    /// <summary>按引擎的路径规则遍历：<c>/steps/2/then/0</c>（分支段 + 该层的步骤下标）。</summary>
    private static void IndexStepPaths(List<Step> steps, string prefix, List<string> output)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string path = prefix.Length == 0 ? $"/steps/{i}" : $"{prefix}/{i}";
            output.Add(path);
            IndexStepPaths(step.Then, $"{path}/then", output);
            IndexStepPaths(step.Else, $"{path}/else", output);
            IndexStepPaths(step.Body, $"{path}/body", output);
        }
    }

    /// <summary>轨迹回调（后台线程）：只往队列里塞，由计时器攒批上屏，循环几千步也不会淹掉 UI。</summary>
    private void OnTrace(StepTrace trace)
    {
        lock (_pendingTraces)
        {
            if (_pendingTraces.Count >= 4000) _pendingTraces.RemoveAt(0);
            _pendingTraces.Add(trace);
        }
    }

    private void StartTraceFlush()
    {
        _traceFlush ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _traceFlush.Tick -= TraceFlush_Tick;
        _traceFlush.Tick += TraceFlush_Tick;
        _traceFlush.Start();
    }

    private void TraceFlush_Tick(object? sender, EventArgs e) => FlushTraces();

    private void FlushTraces()
    {
        List<StepTrace> batch;
        lock (_pendingTraces)
        {
            if (_pendingTraces.Count == 0) return;
            batch = new List<StepTrace>(_pendingTraces);
            _pendingTraces.Clear();
        }

        foreach (var trace in batch)
        {
            if (_traceRowsByPath.TryGetValue(trace.Path, out var row))
            {
                row.Merge(trace);
                continue;
            }

            row = new TraceRow(trace);
            _traceRowsByPath[trace.Path] = row;
            _traceRows.Add(row);
        }

        // 只保留最近 400 行（循环里路径去重后一般远小于这个数）。
        while (_traceRows.Count > 400)
        {
            TraceRow first = _traceRows[0];
            _traceRows.RemoveAt(0);
            if (_traceRowsByPath.TryGetValue(first.Path, out var mapped) && ReferenceEquals(mapped, first))
                _traceRowsByPath.Remove(first.Path);
        }
    }

    /// <summary>运行结束（后台线程）：回到 UI 线程收尾、填变量快照、选中失败那一行。</summary>
    private void OnRunCompleted(ActionRunResult result)
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _traceFlush?.Stop();
            FlushTraces();
            SetRunning(false);
            FillSnapshot(result);

            if (result.Stopped)
            {
                SetStatus($"已停止 · 共 {result.Steps} 步", hint: true);
                return;
            }

            if (result.Completed)
            {
                SetStatus($"完成 · {result.Steps} 步 · {result.Elapsed.TotalMilliseconds:0} ms", hint: true);
                return;
            }

            SetStatus(result.Error ?? "执行失败", hint: false, danger: true);
            SelectTraceRow(result.FailedStepLabel);
        }));
    }

    private void FillSnapshot(ActionRunResult result)
    {
        _snapshotRows.Clear();
        foreach (var pair in result.Variables.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            string text;
            try { text = pair.Value.AsText(); }
            catch { text = "(无法显示)"; }

            if (text.Length > 300) text = text[..300] + "…";
            _snapshotRows.Add(new SnapshotRow(pair.Key, text));
        }
    }

    /// <summary>失败定位：按<strong>步骤路径</strong>找回编辑器里的那一行（嵌套步骤也能对上）。</summary>
    private void SelectTraceRow(string? label)
    {
        TraceRow? target = label == null
            ? _traceRows.FirstOrDefault(r => r.IsFailed)
            : _traceRows.FirstOrDefault(r => r.Label == label);

        if (target == null) return;
        TraceList.SelectedItem = target;
        TraceList.ScrollIntoView(target);
    }

    /// <summary>点轨迹行 = 定位到对应的步骤行（左侧列表跟着滚动、选中）。</summary>
    private void TraceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_running) return;   // 跑的时候别抢用户正在看的行

        if (TraceList.SelectedItem is not TraceRow trace) return;
        if (!_traceTargets.TryGetValue(trace.Path, out var row)) return;
        if (!_rows.Contains(row)) return;

        StepList.SelectedItem = row;
        StepList.ScrollIntoView(row);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        RunButton.IsEnabled = !running;
        RunFromButton.IsEnabled = !running;
        StopRunButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) SetStatus("试运行中…（真的会操作鼠标键盘）", hint: false);
    }

    private void SetStatus(string text, bool hint, bool danger = false)
    {
        RunStatus.Text = text;
        RunStatus.Foreground = (Brush)FindResource(
            danger ? "DangerBrush" : hint ? "HintTextBrush" : "TextBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    public sealed record KindChoice(ValueKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record DirectionChoice(VariableDirection Direction, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class VariableRowUi
    {
        public TextBox Name = null!;
        public ComboBox Type = null!;
        public ComboBox? Direction;
        public TextBox Default = null!;
        public Grid Host = null!;
        public TextBlock OriginText = null!;

        /// <summary>这个变量是谁写进来的（「第 2 步 · 截图（输出 file）」/「手动创建」）。</summary>
        public string Origin = "";

        /// <summary>类型由单元声明决定（用户改过类型就为 false，不再被步骤覆盖）。</summary>
        public bool AutoType;
    }
}

/// <summary>步骤列表的一行：真实步骤 / 标记行 / 空分支占位行。</summary>
public sealed class StepRow
{
    public Step? Step { get; init; }

    /// <summary>该步骤所在的列表（新增 / 删除 / 移动都作用在这里）。</summary>
    public List<Step>? Owner { get; init; }

    public int Index { get; init; }

    public int Depth { get; init; }

    public bool IsMarker { get; init; }

    public bool IsPlaceholder { get; init; }

    public string MarkerText { get; init; } = "";

    public string Number { get; init; } = "";

    public string Glyph { get; init; } = "";

    public string Title { get; init; } = "";

    public string Summary { get; init; } = "";

    public string Output { get; init; } = "";

    public Visibility OutputVisibility =>
        Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ButtonVisibility =>
        Step != null ? Visibility.Visible : Visibility.Collapsed;

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public Thickness MarkerIndent => new(Depth * 18 + 26, 0, 0, 1);
}

/// <summary>
/// 试运行轨迹的一行（按**步骤路径**聚合：循环跑十轮，同一行只留一条，右侧显示 ×N）。
/// </summary>
/// <remarks>
/// 轨迹在后台线程到达，面板攒批后在这里更新——所以属性都带变更通知，
/// 循环里反复命中同一行时只是「改一个属性」，不重建列表。
/// </remarks>
public sealed class TraceRow : INotifyPropertyChanged
{
    private string _glyph = "·";
    private Brush _brush = Brushes.Gray;
    private string _elapsed = "";
    private string _detail = "";
    private bool _isFailed;
    private int _count = 1;

    public TraceRow(StepTrace trace)
    {
        Path = trace.Path;
        Label = trace.Label;
        Unit = trace.UnitName;
        Merge(trace);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Label { get; }

    public string Unit { get; }

    /// <summary>状态图标：✓ 成功、✕ 失败、! 失败但继续、■ 被停止。</summary>
    public string Glyph
    {
        get => _glyph;
        private set => Set(ref _glyph, value, nameof(Glyph));
    }

    public Brush Brush
    {
        get => _brush;
        private set => Set(ref _brush, value, nameof(Brush));
    }

    /// <summary>耗时（末次），重复执行时带 ×N。</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => Set(ref _elapsed, value, nameof(Elapsed));
    }

    /// <summary>错误信息优先，其次是本步写入的变量（<c>text = 你好</c>）。</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value, nameof(Detail));
    }

    /// <summary>是否失败（失败定位用）。</summary>
    public bool IsFailed
    {
        get => _isFailed;
        private set => Set(ref _isFailed, value, nameof(IsFailed));
    }

    /// <summary>同一步骤又执行了一次（循环）：更新耗时 / 状态 / 详情，并累加次数。</summary>
    public void Merge(StepTrace trace)
    {
        if (_count > 1 || _elapsed.Length > 0) _count++;

        (Glyph, IsFailed, Brush) = trace.Status switch
        {
            StepTraceStatus.ok => ("✓", false, Done),
            StepTraceStatus.failed => ("✕", true, Fail),
            StepTraceStatus.continued => ("!", true, Warn),
            _ => ("■", false, Muted),
        };

        Elapsed = _count > 1 ? $"{trace.ElapsedMs} ms ×{_count}" : $"{trace.ElapsedMs} ms";

        Detail = !string.IsNullOrEmpty(trace.Error)
            ? trace.Error!
            : string.Join(" · ", trace.Outputs.Select(p => $"{p.Key} = {Short(p.Value)}"));
    }

    private static string Short(Value value)
    {
        string text;
        try { text = value.AsText(); }
        catch { return "(无法显示)"; }

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static readonly Brush Done = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Fail = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush Warn = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Muted = Freeze(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>变量快照的一行（运行结束时的值）。</summary>
public sealed record SnapshotRow(string Name, string Value);
