using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly ActionDto _draft;
    private readonly ActionProgram _program;
    private readonly ObservableCollection<StepRow> _rows = new();
    private readonly List<VariableRowUi> _variables = new();
    private int _number;

    public ActionEditorWindow(string title, ActionDto? existing)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        if (existing != null)
        {
            _draft = existing.Clone();
            _program = ActionPrograms.For(_draft).Clone();
        }
        else
        {
            _draft = new ActionDto { Type = ActionType.composite, Icon = IconCatalog.Default };
            _program = new ActionProgram();
        }

        _draft.Program = _program;

        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        NameBox.Text = _draft.Name;
        IconPicker.SelectedIcon = string.IsNullOrWhiteSpace(_draft.Icon)
            ? IconCatalog.Default : _draft.Icon;

        StepList.ItemsSource = _rows;
        StepList.PreviewKeyDown += StepList_PreviewKeyDown;

        BuildVariableRows();
        BuildToolbox();
        ReloadRows();

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>确定后的动作；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

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

    private StepEditorDialog CreateStepDialog(Step step) => new(step)
    {
        Owner = this,
        Program = _program,
        VariableNameSource = VariableNames,
    };

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
        "数据" => 6,
        "界面" => 7,
        "内部" => 8,
        "流程" => 9,
        "子动作" => 10,
        _ => 11,
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

    private void EditStep_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void StepList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        EditSelected();
    }

    private void EditSelected()
    {
        StepRow? row = SelectedRow();
        if (row?.Step == null || row.Owner == null) return;

        var dlg = CreateStepDialog(row.Step);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        row.Owner[row.Index] = dlg.Result;
        ReloadRows(dlg.Result);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        StepRow? row = SelectedRow();
        if (row?.Step == null || row.Owner == null) return;

        if (row.Step.Body.Count + row.Step.Then.Count + row.Step.Else.Count > 0
            && !ConfirmDialog.Confirm(this, "删除步骤",
                $"「{StepSummary.Title(row.Step)}」带有子步骤，会一起删除。", danger: true))
            return;

        row.Owner.RemoveAt(row.Index);
        ReloadRows();
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

    // ============ 变量 ============

    private static readonly (ValueKind Kind, string Label)[] Kinds =
    {
        (ValueKind.text, "文本"),
        (ValueKind.number, "数字"),
        (ValueKind.@bool, "布尔"),
        (ValueKind.list, "列表"),
        (ValueKind.@object, "对象"),
    };

    private void BuildVariableRows()
    {
        VariableRows.Children.Clear();
        _variables.Clear();

        foreach (var def in _program.Variables)
            AddVariableRow(def.Key, def.Type, def.Default);
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        AddVariableRow("", ValueKind.text, null);
        _variables[^1].Name.Focus();
    }

    private void AddVariableRow(string key, ValueKind kind, Value? defaultValue)
    {
        var row = new VariableRowUi();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

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
            ToolTip = "删除变量声明（步骤里的引用要自己改）",
        };
        remove.Click += (_, _) =>
        {
            _variables.Remove(row);
            VariableRows.Children.Remove(grid);
        };

        grid.Children.Add(row.Name);
        Grid.SetColumn(row.Type, 1);
        grid.Children.Add(row.Type);
        Grid.SetColumn(row.Default, 2);
        grid.Children.Add(row.Default);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        row.Host = grid;
        _variables.Add(row);
        VariableRows.Children.Add(grid);
    }

    /// <summary>步骤输出用到、但还没声明的变量补进右侧变量表。</summary>
    private void EnsureOutputVariables()
    {
        var known = new HashSet<string>(
            _variables.Select(v => v.Name.Text.Trim()), StringComparer.OrdinalIgnoreCase);

        foreach (var step in _program.Steps.SelectMany(s => s.SelfAndDescendants()))
        {
            foreach (string name in step.Out.Values)
            {
                string key = (name ?? "").Trim();
                if (key.Length > 0 && known.Add(key))
                    AddVariableRow(key, ValueKind.text, null);
            }
        }
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

            list.Add(new VariableDef { Key = key, Type = kind, Default = value });
        }

        return list;
    }

    // ============ 保存 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ConfirmDialog.Info(this, "动作", "给动作起一个名字。");
            NameBox.Focus();
            return;
        }

        _program.Id = _draft.Id ?? "";
        _program.Name = name;
        _program.Icon = IconPicker.SelectedIcon ?? "";

        _program.Variables.Clear();
        foreach (var variable in ReadVariables()) _program.Variables.Add(variable);

        // 输出变量永远在变量表里（写配置的人不用再手动补声明）。
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

        _draft.Name = name;
        _draft.Icon = _program.Icon;
        _draft.Type = ActionType.composite;
        _draft.Program = _program;

        Result = _draft;
        DialogResult = true;
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

    private sealed class VariableRowUi
    {
        public TextBox Name = null!;
        public ComboBox Type = null!;
        public TextBox Default = null!;
        public Grid Host = null!;
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
