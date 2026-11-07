using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 「值」的编辑器：字面量 / 变量 / 内联取值单元（<see cref="StepArg"/> 的三态）。
/// 字面量里的 <c>${变量}</c> 由运行时模板替换，所以普通文本也能引用变量。
/// </summary>
public partial class ValueSourceEditor : UserControl
{
    private bool _loading;
    private Step? _inline;

    public ValueSourceEditor()
    {
        InitializeComponent();
        UnitBox.ItemsSource = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => new UnitChoice(u.Spec.Key, u.Spec.Category, u.Spec.Name))
            .OrderBy(c => c.Category, StringComparer.CurrentCulture)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>值变化（宿主用它刷新可见性 / 摘要）。</summary>
    public event Action? Changed;

    /// <summary>期望类型；<see cref="ValueKind.number"/> 时字面量会转成数字。</summary>
    public ValueKind? Type { get; set; }

    /// <summary>多行字面量（命令 / 正文）。</summary>
    public bool Multiline
    {
        get => LiteralBox.AcceptsReturn;
        set
        {
            LiteralBox.AcceptsReturn = value;
            LiteralBox.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            LiteralBox.MinHeight = value ? 76 : 28;
            LiteralBox.VerticalScrollBarVisibility =
                value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        }
    }

    /// <summary>可选的变量名（用于提示，不强制）。</summary>
    public IReadOnlyList<string>? VariableNames { get; set; }

    /// <summary>内联取值单元编辑时可见的子动作库与变量（由宿主透传）。</summary>
    internal ActionProgram? Program { get; set; }

    internal Func<IEnumerable<string>>? VariableNameSource { get; set; }

    /// <summary>当前值；直接构造，不修改传入对象。</summary>
    public StepArg Value
    {
        get
        {
            switch (ModeBox.SelectedIndex)
            {
                case 1:
                    return StepArg.FromVar((VarBox.Text ?? "").Trim());

                case 2:
                    return StepArg.FromInline(_inline
                        ?? Step.Of((UnitBox.SelectedItem as UnitChoice)?.Key ?? FirstPureUnit()));

                default:
                    string text = LiteralBox.Text ?? "";
                    if (Type == ValueKind.number
                        && double.TryParse(text.Trim(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double number))
                        return StepArg.FromNumber(number);
                    if (Type == ValueKind.@bool && bool.TryParse(text.Trim(), out bool flag))
                        return StepArg.FromBool(flag);
                    return StepArg.FromText(text);
            }
        }
        set => Load(value);
    }

    /// <summary>字面量文本（文件选择按钮回填路径用）。</summary>
    public string LiteralText
    {
        get => LiteralBox.Text ?? "";
        set
        {
            ModeBox.SelectedIndex = 0;
            LiteralBox.Text = value ?? "";
        }
    }

    public bool IsLiteralMode => ModeBox.SelectedIndex == 0;

    /// <summary>显示名（供步骤摘要与提示）。</summary>
    public string DisplayText => Value.Kind switch
    {
        StepArgKind.variable => "${" + Value.VariablePath + "}",
        StepArgKind.inline => "[" + StepSummary.Title(Value.InlineStep?.Unit ?? "") + "]",
        _ => StepSummary.Quote(Value.Literal.AsText()),
    };

    private void Load(StepArg? value)
    {
        _loading = true;
        try
        {
            value ??= StepArg.FromText("");

            switch (value.Kind)
            {
                case StepArgKind.variable:
                    ModeBox.SelectedIndex = 1;
                    VarBox.Text = value.VariablePath;
                    break;

                case StepArgKind.inline:
                    ModeBox.SelectedIndex = 2;
                    _inline = value.InlineStep ?? Step.Of(FirstPureUnit());
                    var choices = UnitBox.ItemsSource as List<UnitChoice>;
                    UnitBox.SelectedItem = choices?.FirstOrDefault(c => c.Key == _inline.Unit);
                    break;

                default:
                    ModeBox.SelectedIndex = 0;
                    LiteralBox.Text = value.Literal.AsText();
                    break;
            }

            UpdatePanels();
        }
        finally { _loading = false; }
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;

        // 先备好内联步骤与单元选中项，说明行才能按当前单元算出「还缺什么」。
        if (!_loading && ModeBox.SelectedIndex == 2)
        {
            _inline ??= Step.Of(FirstPureUnit());
            if (UnitBox.SelectedItem == null && UnitBox.ItemsSource is List<UnitChoice> choices)
                UnitBox.SelectedItem = choices.FirstOrDefault(c => c.Key == _inline.Unit);
        }

        UpdatePanels();
        if (_loading) return;
        Changed?.Invoke();
    }

    private void UpdatePanels()
    {
        LiteralBox.Visibility = ModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VarBox.Visibility = ModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        InlinePanel.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        RefreshInlineHint();
    }

    /// <summary>
    /// 「取值单元」选中时的一行说明：这个单元做什么、还缺哪些必填参数、下一步点哪里。
    /// 新用户看这一行就知道要去点「填参数…」补齐必填项，而不是以为选完单元就完事了。
    /// </summary>
    private void RefreshInlineHint()
    {
        if (ModeBox.SelectedIndex != 2)
        {
            InlineHint.Visibility = Visibility.Collapsed;
            return;
        }

        InlineHint.Visibility = Visibility.Visible;
        Step inline = _inline ??= Step.Of(FirstPureUnit());

        UnitSpec? spec = StepRegistry.Default.TryGet(inline.Unit, out var unit) ? unit.Spec : null;
        string name = spec?.Name ?? inline.Unit;
        string desc = (spec?.Description ?? "").Trim().TrimEnd('。', '.', '；', ';', '，', ',');

        // 没有参数的单元（读剪贴板 / 光标位置…）不用打开表单，按钮直接收起。
        bool hasInputs = spec is { Inputs.Count: > 0 };
        InlineSetupBtn.Visibility = hasInputs ? Visibility.Visible : Visibility.Collapsed;

        string head = desc.Length == 0
            ? $"「{name}」会在运行到这里时现场取一个值"
            : $"「{name}」会在运行到这里时现场取一个值：{desc}";

        var missing = MissingRequired(inline, spec);
        InlineHint.Inlines.Clear();
        var normal = (System.Windows.Media.Brush)FindResource("HintTextBrush");
        var warn = (System.Windows.Media.Brush)FindResource("DangerBrush");

        InlineHint.Inlines.Add(new System.Windows.Documents.Run(head + "。") { Foreground = normal });
        if (missing.Count > 0)
        {
            InlineSetupBtn.Content = "填参数…";
            InlineHint.Inlines.Add(new System.Windows.Documents.Run(
                $"还缺必填参数：{string.Join("、", missing)} —— 点「填参数…」补上（带 * 的项必填）。")
            { Foreground = warn });
        }
        else
        {
            InlineSetupBtn.Content = "改参数…";
            InlineHint.Inlines.Add(new System.Windows.Documents.Run(
                hasInputs ? "点「改参数…」可调整参数。" : "（这个单元不需要参数）。")
            { Foreground = normal });
        }
    }

    /// <summary>当前内联单元还缺哪些必填参数（隐藏行不算）。</summary>
    private static List<string> MissingRequired(Step step, UnitSpec? spec)
    {
        var missing = new List<string>();
        if (spec == null) return missing;

        foreach (var input in spec.Inputs)
        {
            if (!input.Required || input.Default != null) continue;
            if (!StepSummary.IsVisible(input, step, spec)) continue;

            if (!step.In.TryGetValue(input.Key, out var arg)
                || (arg.Kind == StepArgKind.literal
                    && arg.Literal.Kind == ValueKind.text
                    && arg.Literal.AsText().Trim().Length == 0))
                missing.Add(input.Label);
        }

        return missing;
    }

    private void Value_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Changed?.Invoke();
    }

    private void Unit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UnitBox.SelectedItem is not UnitChoice choice) return;

        // 换了单元就丢掉旧参数（不同单元的输入没有交集）
        if (_inline == null || _inline.Unit != choice.Key)
            _inline = Step.Of(choice.Key);

        RefreshInlineHint();
        Changed?.Invoke();
    }

    private void InlineSetup_Click(object sender, RoutedEventArgs e)
    {
        if (UnitBox.SelectedItem is not UnitChoice choice) return;
        _inline ??= Step.Of(choice.Key);

        var dlg = new StepEditorDialog(_inline, inline: true)
        {
            Owner = Window.GetWindow(this),
            VariableNameSource = VariableNameSource,
            Program = Program,
        };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _inline = dlg.Result;
            RefreshInlineHint();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 新切到「取值单元」时默认选中的单元：挑最常用、参数最少的一个，
    /// 别让新用户一进来就撞上「进程」这种要填进程名、说明又长的单元。
    /// </summary>
    private static readonly string[] PreferredDefaults =
    {
        "text.selection", "data.time", "input.cursor", "screen.findImage",
    };

    private static string FirstPureUnit()
    {
        var keys = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec.Key)
            .ToList();

        foreach (string preferred in PreferredDefaults)
            if (keys.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return preferred;

        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? "text.process";
    }

    public sealed record UnitChoice(string Key, string Category, string Name)
    {
        public string Display => Category.Length > 0 ? $"{Category} · {Name}" : Name;

        public override string ToString() => Display;
    }
}
