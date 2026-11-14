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
/// 「调用子动作」的按名参数：子动作换了就重建，没声明的键顺手清掉。
/// </summary>
public partial class StepFormView
{
    // ============ 子动作动态参数 ============

    private void DropUnknownDynamicArgs()
    {
        var allowed = new HashSet<string>(
            _spec.Inputs.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);
        if (def != null)
            foreach (var variable in def.Variables) allowed.Add(variable.Key);

        foreach (string key in _step.In.Keys.ToList())
            if (!allowed.Contains(key)) _step.In.Remove(key);
    }

    private void RebuildDynamic()
    {
        _dynamicPanel.Children.Clear();
        _dynamicInputs.Clear();

        if (_step.Unit != "sub.run" || !_spec.DynamicInputs) return;

        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);
        _dynamicSubAction = id;

        _dynamicPanel.Children.Add(new TextBlock
        {
            Text = def == null
                ? "（选一个子动作后，这里会出现它的输入参数）"
                : "子动作参数",
            Style = (Style)FindResource(def == null ? "HintLabel" : "SectionLabel"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        });

        if (def == null) return;

        foreach (var variable in def.Variables.Where(v => v.Direction == VariableDirection.input))
        {
            var spec = new InputSpec(variable.Key,
                $"{variable.Key}（{ValueKindText.Of(variable.Type)}）", variable.Type);
            var editor = new ValueField
            {
                Spec = spec,
                Type = variable.Type,
                VariableNameSource = VariableNameSource,
                VariableInfoSource = VariableInfoSource,
                Program = Program,
                ResourceRoot = ResourceRoot,
                Value = _step.In.TryGetValue(variable.Key, out var existing)
                    ? existing : StepArg.FromText(""),
            };
            editor.Changed += OnEdited;
            _fieldSetters[variable.Key] = text => editor.LiteralText = text;

            _dynamicPanel.Children.Add(BuildRowFor(spec, editor));
            _dynamicInputs.Add((variable, editor));
        }

        if (_dynamicInputs.Count == 0)
        {
            _dynamicPanel.Children.Add(new TextBlock
            {
                Text = "这个子动作没有输入参数。",
                Style = (Style)FindResource("HintLabel"),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        // 动态行是后建的：把宿主的内联取值钩子补上（为空时保持 null，退回弹窗口）。
        RewireInlineEditors();
    }

    private FrameworkElement BuildRowFor(InputSpec spec, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = spec.Label,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private List<SubChoice> SubActionChoices()
    {
        var list = new List<SubChoice>();
        foreach (var def in SubActionStore.LoadAll())
            list.Add(new SubChoice(def.Id, Label(def)));

        return list;
    }

    private static SubActionDef? ResolveSubAction(string id) => SubActionStore.Load(id);

    private static string Label(SubActionDef def) =>
        string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;

}
