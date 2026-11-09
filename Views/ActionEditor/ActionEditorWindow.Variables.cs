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
            var (reads, writes) = name.Length == 0 ? (0, 0) : ReferenceStats(name);
            if (reads + writes > 0)
            {
                // 步骤还写着这个变量（写入或 ${名}），删了会被下次同步自动加回来，还会留下读不到变量的引用。
                string detail = writes > 0 && reads > 0
                    ? $"{writes} 处写入、{reads} 处读取"
                    : writes > 0 ? $"{writes} 处写入" : $"{reads} 处读取";
                ConfirmDialog.Info(this, "删除变量",
                    $"「${{{name}}}」还被 {detail} 引用。\n" +
                    "先改掉那些步骤（或改用别的变量名），再删这里。");
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
        row.LastName = key;
        row.Name.TextChanged += (_, _) =>
        {
            // 改名同步：a → b 时把步骤里的 ${a} / 变量取值 / 输出映射一起改掉。
            // 逐键同步（不是失焦才同步），用户看着步骤摘要一起变，不会漏改。
            string name = row.Name.Text.Trim();
            if (row.LastName.Length > 0 && name.Length > 0
                && !string.Equals(row.LastName, name, StringComparison.Ordinal))
            {
                RenameReferences(row.LastName, name);
                row.LastName = name;
                _renameTouchedSteps = true;
            }
            else if (name.Length > 0)
            {
                row.LastName = name;
            }

            RefreshVariableTips();
        };
        row.Name.LostFocus += (_, _) =>
        {
            // 步骤列表在失焦后重建一次：摘要里的 ${变量} 要跟着新名字变。
            if (!_renameTouchedSteps) return;
            _renameTouchedSteps = false;
            ReloadRows();
        };
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

    /// <summary>变量表每次重建后重算提示：来源 + 被几处步骤引用（写入 / 读取分开算）。</summary>
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

            var (reads, writes) = ReferenceStats(name);
            int refs = reads + writes;
            string origin = row.Origin.Length == 0 ? "手动创建" : row.Origin;
            row.OriginText.Text = refs > 0 ? $"{origin} · 引用 {refs} 处" : origin;
            row.OriginText.ToolTip = refs == 0
                ? "还没有步骤用到它"
                : $"被 {refs} 处步骤引用（{writes} 处写入 · {reads} 处读取）";
            row.Host.ToolTip = "来源：" + origin +
                (refs == 0 ? "\n还没有步骤用到它" : $"\n被 {refs} 处步骤引用（{writes} 处写入 · {reads} 处读取）");
        }
    }

    /// <summary>
    /// 变量名在步骤里的引用：<b>写入</b> = <see cref="Step.Out"/> 映射到这个变量，
    /// <b>读取</b> = 参数文本里的 <c>${名}</c>、变量取值、内联取值里的引用。
    /// </summary>
    private (int Reads, int Writes) ReferenceStats(string name)
    {
        int reads = 0, writes = 0;
        foreach (var (step, _) in AllSteps()) ScanReferences(step, name, ref reads, ref writes);
        return (reads, writes);
    }

    private static void ScanReferences(Step step, string name, ref int reads, ref int writes)
    {
        foreach (string target in step.Out.Values)
            if (NameEquals(target, name)) writes++;

        foreach (StepArg arg in step.In.Values) ScanArgReferences(arg, name, ref reads);
    }

    /// <summary>内联取值步骤自己的输出变量不算「这个动作里的写入」，只继续找它读的变量。</summary>
    private static void ScanArgReferences(StepArg arg, string name, ref int reads)
    {
        switch (arg.Kind)
        {
            case StepArgKind.literal:
                if (ValueUsesVariable(arg.Literal, name)) reads++;
                break;
            case StepArgKind.variable:
                if (NameEquals(PathBase(arg.VariablePath), name)) reads++;
                break;
            case StepArgKind.inline when arg.InlineStep != null:
                foreach (StepArg nested in arg.InlineStep.In.Values) ScanArgReferences(nested, name, ref reads);
                break;
        }
    }

    // ============ 改名同步 ============

    /// <summary>
    /// 把步骤里对 <paramref name="oldName"/> 的引用改成 <paramref name="newName"/>：
    /// 输出映射、<c>${旧名}</c>（含 <c>旧名.成员</c> / <c>旧名[0]</c>）、变量取值。返回改动处数。
    /// </summary>
    private int RenameReferences(string oldName, string newName)
    {
        if (oldName.Length == 0 || newName.Length == 0) return 0;

        int count = 0;
        foreach (var (step, _) in AllSteps()) RenameInStep(step, oldName, newName, ref count);
        if (count > 0) SetStatus($"变量改名：{oldName} → {newName}，同步 {count} 处引用", hint: true);
        return count;
    }

    private static void RenameInStep(Step step, string oldName, string newName, ref int count)
    {
        foreach (string key in step.Out.Keys.ToList())
        {
            if (!NameEquals(step.Out[key], oldName)) continue;
            step.Out[key] = newName;
            count++;
        }

        foreach (string key in step.In.Keys.ToList())
        {
            StepArg arg = step.In[key];
            switch (arg.Kind)
            {
                case StepArgKind.literal:
                {
                    Value renamed = RenameValue(arg.Literal, oldName, newName, ref count);
                    if (!ReferenceEquals(renamed, arg.Literal)) step.In[key] = StepArg.FromValue(renamed);
                    break;
                }
                case StepArgKind.variable:
                {
                    string path = RenamePath(arg.VariablePath, oldName, newName);
                    if (!string.Equals(path, arg.VariablePath, StringComparison.Ordinal))
                    {
                        step.In[key] = StepArg.FromVar(path);
                        count++;
                    }
                    break;
                }
                case StepArgKind.inline when arg.InlineStep != null:
                    RenameInStep(arg.InlineStep, oldName, newName, ref count);
                    break;
            }
        }
    }

    private static Value RenameValue(Value value, string oldName, string newName, ref int count)
    {
        switch (value.Kind)
        {
            case ValueKind.text:
                return RenameTextValue(value, oldName, newName, ref count);

            case ValueKind.list:
            {
                var items = new List<Value>(value.Items.Count);
                foreach (Value item in value.Items)
                    items.Add(RenameValue(item, oldName, newName, ref count));
                return Value.FromList(items);
            }

            case ValueKind.@object:
            {
                var members = new List<KeyValuePair<string, Value>>(value.Members.Count);
                foreach (var pair in value.Members)
                    members.Add(new KeyValuePair<string, Value>(
                        pair.Key, RenameValue(pair.Value, oldName, newName, ref count)));
                return Value.FromObject(members);
            }

            default:
                return value;
        }
    }

    private static Value RenameTextValue(Value value, string oldName, string newName, ref int count)
    {
        string renamed = RenameInText(value.AsText(), oldName, newName, out int hits);
        count += hits;
        return hits == 0 ? value : Value.FromText(renamed);
    }

    /// <summary>把文本里的 <c>${旧名…}</c> 换成 <c>${新名…}</c>；<c>$$</c> 转义的写法不动。</summary>
    private static string RenameInText(string text, string oldName, string newName, out int hits)
    {
        hits = 0;
        if (text.IndexOf("${", StringComparison.Ordinal) < 0) return text;

        var sb = new System.Text.StringBuilder(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            int end = text.IndexOf('}', start + 2);
            if (end < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            bool escaped = start > 0 && text[start - 1] == '$';
            string inner = text[(start + 2)..end];
            bool hit = !escaped && NameEquals(PathBase(inner), oldName);
            if (hit) hits++;

            sb.Append(text, i, start - i);
            sb.Append("${");
            sb.Append(hit ? RenamePath(inner, oldName, newName) : inner);
            sb.Append('}');
            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>只换路径的第一段（变量名），<c>a.b[0]</c> → <c>new.b[0]</c>；保留前后的空白。</summary>
    private static string RenamePath(string path, string oldName, string newName)
    {
        string trimmed = path.Trim();
        if (trimmed.Length == 0) return path;

        string baseName = PathBase(trimmed);
        if (!NameEquals(baseName, oldName)) return path;

        int lead = path.Length - path.TrimStart().Length;
        int tail = path.Length - path.TrimEnd().Length;
        return path[..lead] + newName + trimmed[baseName.Length..]
            + (tail == 0 ? "" : path[(path.Length - tail)..]);
    }

    // ============ 变量引用的小工具 ============

    private static bool NameEquals(string? a, string b) =>
        string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>a.b[0]</c> 的第一段（变量名本身）。</summary>
    private static string PathBase(string? path)
    {
        string trimmed = (path ?? "").Trim();
        int cut = trimmed.IndexOfAny(new[] { '.', '[' });
        return (cut < 0 ? trimmed : trimmed[..cut]).Trim();
    }

    private static bool ValueUsesVariable(Value value, string name) => value.Kind switch
    {
        ValueKind.text => TemplatePaths(value.AsText()).Any(path => NameEquals(PathBase(path), name)),
        ValueKind.list => value.Items.Any(item => ValueUsesVariable(item, name)),
        ValueKind.@object => value.Members.Any(kv => ValueUsesVariable(kv.Value, name)),
        _ => false,
    };

    /// <summary>文本里出现的 <c>${…}</c> 路径（<c>$$</c> 转义的不算）。</summary>
    private static IEnumerable<string> TemplatePaths(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0) yield break;

            int end = text.IndexOf('}', start + 2);
            if (end < 0) yield break;

            bool escaped = start > 0 && text[start - 1] == '$';
            if (!escaped) yield return text[(start + 2)..end];
            i = end + 1;
        }
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

        foreach (var (step, _) in AllSteps())
        {
            StepRow? stepRow = _rows.FirstOrDefault(r => ReferenceEquals(r.Step, step));

            foreach (var pair in step.Out)
            {
                string key = (pair.Value ?? "").Trim();
                if (key.Length == 0) continue;

                ValueKind? declared = OutputType(step, pair.Key);
                string origin = stepRow == null
                    ? $"{StepSummary.Title(step)}（输出 {pair.Key}）"
                    : $"第 {stepRow.Number} 步 · {stepRow.Title}（输出 {pair.Key}）";

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
