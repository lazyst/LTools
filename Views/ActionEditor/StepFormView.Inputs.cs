using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Script;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 输入区：按 UnitSpec.Inputs 建行、高级参数折叠、行模板（标签 + 编辑器 + 说明），
/// 以及「读回步骤 / 收起不可见参数」的那一份同步逻辑。
/// </summary>
public partial class StepFormView
{
    // ============ 输入 ============

    private void BuildInputs()
    {
        var advanced = new List<InputSpec>();

        foreach (var input in _spec.Inputs)
        {
            if (input.Advanced)
            {
                advanced.Add(input);
                continue;
            }

            IArgEditor editor = BuildEditor(input);
            _rows.Add(new Row(input, AddRow(Form, input, editor.Element), editor));
        }

        Form.Children.Add(_dynamicPanel);
        if (advanced.Count > 0) BuildAdvancedSection(advanced);

        SyncStepArgs();
        if (_spec.DynamicInputs) RebuildDynamic();
        UpdateVisibility();
    }

    /// <summary>
    /// 调优参数（超时 / 轮询间隔 / 相似度…）收进「高级参数」：默认折叠，
    /// **已经设过非默认值的自动展开**，不会把用户填过的参数藏起来。
    /// </summary>
    private void BuildAdvancedSection(List<InputSpec> advanced)
    {
        var host = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var header = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 4, 0, 2),
            ToolTip = "超时、轮询间隔、相似度这类调优参数；平时不用改",
        };

        _advancedHeader = header;
        _advancedCount = advanced.Count;
        SetAdvancedExpanded(advanced.Any(input => _step.In.ContainsKey(input.Key)));

        header.Click += (_, _) => SetAdvancedExpanded(!_advancedExpanded);

        host.Children.Add(header);
        host.Children.Add(_advancedPanel);
        Form.Children.Add(host);

        foreach (var input in advanced)
        {
            IArgEditor editor = BuildEditor(input);
            _rows.Add(new Row(input, AddRow(_advancedPanel, input, editor.Element), editor));
        }
    }

    /// <summary>折叠 / 展开高级参数（标题箭头与面板可见性一起改）。</summary>
    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        if (_advancedHeader != null)
            _advancedHeader.Content = (expanded ? "▾ " : "▸ ") + $"高级参数（{_advancedCount}）";
        _advancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement AddRow(Panel host, InputSpec input, FrameworkElement editor)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = input.Required ? input.Label + " *" : input.Label,
            Foreground = (System.Windows.Media.Brush)FindResource(
                input.Required ? "TextBrush" : "SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        // 第一行放输入控件，第二行（有说明时）放「该填什么」——说明不占标签列，
        // 长文本可以贴着输入框的宽度换行。
        // ValueField 自己就带一行状态说明（还会显示「来自变量 …」），别在外面再挂一份。
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string hint = editor is ValueField ? "" : InputHint.Of(input);
        if (hint.Length > 0)
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        row.Children.Add(label);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);

        if (hint.Length > 0)
        {
            var note = new TextBlock
            {
                Text = hint,
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            Grid.SetRow(note, 1);
            Grid.SetColumn(note, 1);
            row.Children.Add(note);
        }

        host.Children.Add(row);
        return row;
    }

    private void UpdateVisibility()
    {
        foreach (var row in _rows)
            row.Host.Visibility = StepSummary.IsVisible(row.Spec, _step, _spec)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把编辑器里的值写回 <see cref="_step"/>（含可见性所依赖的枚举值）。</summary>
    private void SyncStepArgs()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var all = new Dictionary<string, StepArg>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                var arg = row.Editor.Read();
                if (arg != null && ShouldKeep(row.Spec, arg)) all[row.Spec.Key] = arg;
            }

            foreach (var (variable, editor) in _dynamicInputs)
            {
                StepArg value = editor.Value;
                if (value.Kind == StepArgKind.literal
                    && value.Literal.Kind == ValueKind.text
                    && value.Literal.AsText().Length == 0) continue;
                all[variable.Key] = value;
            }

            // 声明之外的参数只有动态参数动作单元（sub.run）才保留；
            // 其它动作单元的未知键会在运行时被判为拼写错误，顺手清掉。
            var extra = _spec.DynamicInputs
                ? _step.In.Where(p => !_spec.Inputs.Any(i =>
                    string.Equals(i.Key, p.Key, StringComparison.OrdinalIgnoreCase))).ToList()
                : new List<KeyValuePair<string, StepArg>>();

            _step.In.Clear();
            foreach (var pair in extra) _step.In[pair.Key] = pair.Value;
            foreach (var pair in all) _step.In[pair.Key] = pair.Value;

            // 隐藏的参数（当前枚举选项之外的、布尔附加条件不满足的）保存时丢掉，
            // 避免切过操作之后留下 x/y / 轮询间隔这类无关字段。
            foreach (var row in _rows)
            {
                if (!StepSummary.IsVisible(row.Spec, _step, _spec))
                    _step.In.Remove(row.Spec.Key);
            }
        }
        finally { _syncing = false; }
    }

    private void OnEdited()
    {
        if (_syncing) return;
        HasEdits = true;
        SyncStepArgs();

        // 「调用子动作」换了子动作：动态输入 / 输出跟着换（以前绑在下拉的 SelectionChanged 上，
        // 现在子动作是文本框 + ▾，按值变没变来判断）。
        if (_step.Unit == "sub.run" && _spec.DynamicInputs)
        {
            string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
            if (!string.Equals(id, _dynamicSubAction, StringComparison.OrdinalIgnoreCase))
            {
                DropUnknownDynamicArgs();
                RebuildDynamic();
                BuildOutputs();
            }
        }

        UpdateVisibility();
    }

    /// <summary>
    /// 空的可选文本、false 的开关不必写进步骤（运行时按默认值处理），
    /// 这样保存出来的 JSON 只留用户真正填过的参数。
    /// </summary>
    private static bool ShouldKeep(InputSpec spec, StepArg arg)
    {
        if (arg.Kind != StepArgKind.literal) return true;

        if (arg.Literal.Kind == ValueKind.@bool)
            return arg.Literal.AsBool() != (spec.Default?.AsBool() ?? false);

        if (arg.Literal.Kind == ValueKind.number)
            return spec.Default == null || arg.Literal.AsNumber() != spec.Default.AsNumber();

        if (arg.Literal.Kind != ValueKind.text) return true;

        string text = arg.Literal.AsText();
        if (text.Length > 0) return true;
        if (spec.Required) return true;
        return spec.Default != null && spec.Default.AsText().Length > 0;
    }

}
