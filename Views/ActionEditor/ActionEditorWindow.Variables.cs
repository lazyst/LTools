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
/// 变量
/// </summary>
public partial class ActionEditorWindow
{
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
            SyncDefaultEditor(row);
        };

        row.Default = new TextBox
        {
            MinHeight = 26,
            Text = ListText.Display(defaultValue),
            FontSize = (double)FindResource("FontSizeHint"),
        };
        SyncDefaultEditor(row);

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

    /// <summary>
    /// 默认值输入框按类型换形态：列表 / 对象是多行的（每行一项 / JSON），
    /// 单行框会把它们挤成一条，用户根本看不出自己填了什么。
    /// </summary>
    private static void SyncDefaultEditor(VariableRowUi row)
    {
        ValueKind kind = (row.Type.SelectedItem as KindChoice)?.Kind ?? ValueKind.text;
        bool multiline = kind is ValueKind.list or ValueKind.@object;

        row.Default.AcceptsReturn = multiline;
        row.Default.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        row.Default.MinHeight = multiline ? 48 : 26;
        row.Default.MaxHeight = multiline ? 120 : double.PositiveInfinity;
        row.Default.VerticalScrollBarVisibility = multiline
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;

        row.Default.ToolTip = kind switch
        {
            ValueKind.list => "每行一项，也可以用逗号分隔；项里含逗号或要嵌套时写 JSON 数组，如 [\"a,b\", 1]",
            ValueKind.@object => "写 JSON 对象，如 {\"a\": 1}",
            ValueKind.number => "留空 = 0",
            ValueKind.@bool => "true / false；留空 = false",
            _ => "留空 = 空文本",
        };
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

    private List<VariableDef>? ReadVariables()
    {
        var list = new List<VariableDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _variables)
        {
            string key = row.Name.Text.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;

            ValueKind kind = (row.Type.SelectedItem as KindChoice)?.Kind ?? ValueKind.text;

            // 列表 / 对象的默认值以前在这里被直接丢掉（列表只能永远是空的）。
            // 现在按类型解析；填错就提示并拦住保存，不静默丢值。
            if (!ListText.TryParseDefault(kind, row.Default.Text, out Value? value, out string error))
            {
                ConfirmDialog.Info(this, "变量默认值",
                    $"「{key}」的默认值填得不对：\n{error}");
                row.Default.Focus();
                row.Default.SelectAll();
                return null;
            }

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
}
