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
                    return StepArg.FromInline(_inline ?? Step.Of(FirstPureUnit()));

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
        UpdatePanels();
        if (_loading) return;
        if (ModeBox.SelectedIndex == 2)
        {
            _inline ??= Step.Of(FirstPureUnit());
            if (UnitBox.SelectedItem == null && UnitBox.ItemsSource is List<UnitChoice> choices)
                UnitBox.SelectedItem = choices.FirstOrDefault(c => c.Key == _inline.Unit);
        }
        Changed?.Invoke();
    }

    private void UpdatePanels()
    {
        LiteralBox.Visibility = ModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        VarBox.Visibility = ModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        InlinePanel.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
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
            Changed?.Invoke();
        }
    }

    private static string FirstPureUnit() =>
        StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "text.process";

    public sealed record UnitChoice(string Key, string Category, string Name)
    {
        public string Display => Category.Length > 0 ? $"{Category} · {Name}" : Name;

        public override string ToString() => Display;
    }
}
