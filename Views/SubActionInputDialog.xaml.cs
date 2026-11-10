using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 子动作试运行前填输入值：按「输入」方向的变量生成一行一个输入框，留空就用变量的默认值。
/// </summary>
/// <remarks>
/// 动作编辑器与设置页「子动作库」共用。输入 / 输出的语义与 <see cref="ActionEngine.RunSubAction"/>
/// 完全一致（参数名 = 变量名），这里只负责把文本解析成 <see cref="Value"/>。
/// </remarks>
public partial class SubActionInputDialog : Window
{
    private readonly List<(VariableDef Def, TextBox Box)> _rows = new();

    public IReadOnlyDictionary<string, Value> Values { get; private set; } =
        new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

    public SubActionInputDialog(string name, IEnumerable<VariableDef> variables)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        HeadText.Text = $"{name} · 输入";
        Build(variables);
    }

    /// <summary>
    /// 让用户填一次输入值。没有输入变量时不弹窗，直接返回空表；用户取消返回 null。
    /// </summary>
    /// <remarks>
    /// 变量表由调用方给（编辑器传「现在界面上的变量表」，子动作库传文件里的定义），
    /// 这样刚在编辑器里改过默认值、还没保存时，试运行用的也是新值。
    /// </remarks>
    public static IReadOnlyDictionary<string, Value>? Ask(Window? owner, string name,
        IEnumerable<VariableDef> variables)
    {
        var list = variables.ToList();
        bool hasInput = list.Any(v =>
            v.Direction == VariableDirection.input && !string.IsNullOrWhiteSpace(v.Key));
        if (!hasInput) return new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        var dialog = new SubActionInputDialog(name, list);
        if (owner != null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }

    private void Build(IEnumerable<VariableDef> variables)
    {
        foreach (var variable in variables)
        {
            if (variable.Direction != VariableDirection.input) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0) continue;

            bool multiLine = variable.Type is ValueKind.list or ValueKind.@object;

            Inputs.Children.Add(new TextBlock
            {
                Text = $"{key}（{KindLabel(variable.Type)}）",
                Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(0, _rows.Count == 0 ? 0 : 10, 0, 4),
            });

            var box = new TextBox
            {
                Text = ListText.Display(variable.Seed()),
                MinHeight = multiLine ? 64 : 30,
                AcceptsReturn = multiLine,
                TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
            Inputs.Children.Add(box);

            if (!string.IsNullOrWhiteSpace(variable.Label))
            {
                Inputs.Children.Add(new TextBlock
                {
                    Text = variable.Label,
                    FontSize = (double)FindResource("FontSizeHint"),
                    Foreground = (Brush)FindResource("HintTextBrush"),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            _rows.Add((variable, box));
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        foreach (var (variable, box) in _rows)
        {
            string key = (variable.Key ?? "").Trim();
            if (!ListText.TryParseDefault(variable.Type, box.Text, out Value? value, out string error))
            {
                ConfirmDialog.Info(this, "输入值", $"「{key}」填得不对：\n{error}");
                box.Focus();
                box.SelectAll();
                return;
            }

            values[key] = value ?? VariableDef.DefaultFor(variable.Type);
        }

        Values = values;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string KindLabel(ValueKind kind) => kind switch
    {
        ValueKind.number => "数字",
        ValueKind.@bool => "布尔",
        ValueKind.list => "列表",
        ValueKind.@object => "对象",
        _ => "文本",
    };
}
