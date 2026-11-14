using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 输入框下方那一行状态说明：该填什么 / 来自变量 X（类型 · 第 2 步写）/
/// 取值还缺哪些必填参数 / 「这里写的是文本，不是变量」。
/// </summary>
public partial class ValueField
{
    private void UpdateStatus()
    {
        // 颜色也在这里定：必填红字 / 「写成文本了」红字都要能回到主题的提示色。
        StatusText.ClearValue(TextBlock.ForegroundProperty);

        if (_missing)
        {
            StatusText.Text = "必填：这一项还没填";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        string hint = _spec == null ? "" : InputHint.Of(_spec);
        StatusText.ToolTip = hint.Length > 0 ? hint : null;

        if (_inline != null)
        {
            var spec = StepRegistry.Default.TryGet(_inline.Unit, out var unit) ? unit.Spec : null;
            string name = spec?.Name ?? _inline.Unit;
            var missing = MissingRequired(_inline, spec);
            StatusText.Text = missing.Count > 0
                ? $"取值：{name} · 还缺必填参数：{string.Join("、", missing)}"
                : spec is { Inputs.Count: > 0 }
                    ? $"取值：{name} · 点 ▾ 里的「改参数…」调整它"
                    : $"取值：{name}（不需要参数）";
            return;
        }

        if (_boundVariable != null)
        {
            var info = VariableInfos().FirstOrDefault(v =>
                string.Equals(v.Name, _boundVariable, StringComparison.OrdinalIgnoreCase));
            string type = info == null ? "" : "（" + ValueKindText.Of(info.Kind) + "）";
            string from = string.IsNullOrWhiteSpace(info?.Origin) ? "" : " · " + info!.Origin;
            StatusText.Text = $"来自变量 {_boundVariable}{type}{from}";
            return;
        }

        // 文本里正好写了某个变量名（没加 ${}）：运行时会被当成字面量，「如果」就永远成立。
        // 变量名输入框（flow.set 的变量名）里本来就该是裸名字，跳过。
        string literal = (Box.Text ?? "").Trim();
        if (!VariableNameMode && literal.Length > 0
            && !literal.Contains("${", StringComparison.Ordinal)
            && VariableInfos().Any(v =>
                string.Equals(v.Name, literal, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"这里写的是文本「{literal}」；要取值请写成 ${{{literal}}}";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        string selected = ChoiceLabelOf(literal);
        StatusText.Text = selected.Length == 0
            ? hint
            : hint.Length == 0 ? selected : selected + " · " + hint;
    }

    /// <summary>
    /// 当前值正好是某个候选项（宏 / 子动作存的是 Id）：状态行补一句「已选 …」，
    /// 输入框里仍然是真实存进步骤的值，不玩「显示名 ≠ 存的值」。
    /// </summary>
    private string ChoiceLabelOf(string literal)
    {
        if (literal.Length == 0 || ChoiceSource == null || !Pickers.HasFlag(PickerKind.choices))
            return "";

        try
        {
            var match = ChoiceSource().FirstOrDefault(c =>
                string.Equals(c.Value, literal, StringComparison.OrdinalIgnoreCase));
            return match == null ? "" : "已选「" + match.Label + "」";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 取值动作单元还缺哪些必填参数：新用户从状态行就知道要先去点「改参数…」，
    /// 而不是运行到一半才发现没填。
    /// </summary>
    private static List<string> MissingRequired(Step step, UnitSpec? spec)
    {
        var missing = new List<string>();
        if (spec == null) return missing;

        foreach (var input in spec.Inputs)
        {
            if (!input.Required) continue;
            // 有默认值的参数没填也能跑（引擎回落到 Default），不算「还缺」。
            if (input.Default != null) continue;
            if (!StepSummary.IsVisible(input, step, spec)) continue;
            if (!step.In.TryGetValue(input.Key, out var arg) || arg.IsEmpty) missing.Add(input.Label);
        }

        return missing;
    }

    private IReadOnlyList<VariableInfo> VariableInfos()
    {
        try
        {
            if (VariableInfoSource != null) return VariableInfoSource();
            return (VariableNameSource?.Invoke() ?? Enumerable.Empty<string>())
                .Select(n => (n ?? "").Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .Select(n => new VariableInfo(n, ValueKind.text, ""))
                .ToList();
        }
        catch
        {
            return Array.Empty<VariableInfo>();
        }
    }

    private static IReadOnlyList<string> NoteCategories()
    {
        try
        {
            return Directory.Exists(AppPaths.NotesDir)
                ? Directory.GetDirectories(AppPaths.NotesDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.CurrentCulture)
                    .ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

}
