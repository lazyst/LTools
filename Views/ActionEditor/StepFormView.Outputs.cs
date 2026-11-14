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
/// 输出区：按动作单元声明（或子动作变量表）建行、勾选与「已勾 N / M」标题。
/// </summary>
public partial class StepFormView
{
    // ============ 输出 ============

    private void BuildOutputs()
    {
        Outputs.Children.Clear();
        _outputs.Clear();
        _dynamicOutputs.Clear();

        if (_step.Unit == "sub.run")
        {
            BuildDynamicOutputs();
        }
        else
        {
            // 「完整结果」（对象）几乎没人用：有别的输出时收进末尾的折叠区，别占视线。
            bool foldValue = _spec.Outputs.Count > 1;
            foreach (var output in _spec.Outputs)
                if (!foldValue || !IsWholeResult(output))
                    AddBuiltInOutput(output);

            var folded = _spec.Outputs.Where(IsWholeResult).ToList();
            if (foldValue && folded.Count > 0)
            {
                var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                var expander = new Expander
                {
                    Header = "其它字段（" + string.Join("、", folded.Select(o => o.Label)) + "）",
                    // 已经勾着的（老动作）默认展开，不然用户找不到自己开过的那一项。
                    IsExpanded = folded.Any(o => _step.Out.ContainsKey(o.Key)),
                    Content = panel,
                };
                foreach (var output in folded) AddBuiltInOutput(output, panel);
                Outputs.Children.Add(expander);
            }
        }

        // 没有输出就别留一个空白的「输出变量」区（内置功能 / 没选子动作的调用子动作）。
        OutputSection.Visibility = _inline || _outputs.Count + _dynamicOutputs.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateOutputHeader();
    }

    private static bool IsWholeResult(OutputSpec output) =>
        string.Equals(output.Key, "value", StringComparison.OrdinalIgnoreCase);

    private void AddBuiltInOutput(OutputSpec output, Panel? host = null)
    {
        bool on = _step.Out.TryGetValue(output.Key, out string? target);
        var row = AddOutputRow(output.Key, output.Label, output.Type, output.Description,
            on ? target ?? "" : OutputNaming.Suggest(_spec.Key, output), host);
        WireRow(row, on);
        _outputs.Add(row);
    }

    private void BuildDynamicOutputs()
    {
        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);
        if (def == null) return;

        foreach (var variable in def.Variables.Where(v => v.Direction == VariableDirection.output))
        {
            bool on = _step.Out.TryGetValue(variable.Key, out string? target);
            var row = AddOutputRow(variable.Key, variable.Key, variable.Type,
                $"子动作「{Label(def)}」声明的输出参数",
                on ? target ?? "" : variable.Key);
            WireRow(row, on);
            _dynamicOutputs.Add(row);
        }
    }

    /// <summary>勾选状态决定变量名是否可编辑，并刷新下面的说明行。</summary>
    private void WireRow(OutputRow row, bool on)
    {
        row.Toggle.IsChecked = on;
        row.Box.IsEnabled = on;
        row.Pick.IsEnabled = on;
        row.Toggle.Checked += (_, _) =>
        {
            row.Box.IsEnabled = true;
            row.Pick.IsEnabled = true;
            row.Refresh();
            UpdateOutputHeader();
        };
        row.Toggle.Unchecked += (_, _) =>
        {
            row.Box.IsEnabled = false;
            row.Pick.IsEnabled = false;
            row.Refresh();
            UpdateOutputHeader();
        };
        row.Box.TextChanged += (_, _) => row.Refresh();
        row.Refresh();
    }

    /// <summary>标题上的「已勾 N / M」：一眼看出这一步到底会往变量表里写几项。</summary>
    private void UpdateOutputHeader()
    {
        int total = _outputs.Count + _dynamicOutputs.Count;
        int on = _outputs.Concat(_dynamicOutputs).Count(r => r.Toggle.IsChecked == true);
        OutputHeader.Text = total == 0 ? "输出变量" : $"输出变量 · 已勾 {on} / {total}";
    }

}
