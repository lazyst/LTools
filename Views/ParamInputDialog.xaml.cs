using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 运行前填「输入」方向的变量：一行一个输入框，留空就用变量表里的默认值。
/// </summary>
/// <remarks>
/// <para>
/// 两处共用：动作参数（面板 / 快捷键 / 悬浮运行前问一次，见 <c>Features.ActionParams</c>）与
/// 子动作试运行（<c>SubActionInputDialog</c> 已并入这里）。参数名 = 变量名，
/// 语义与 <see cref="ActionEngine.RunSubAction"/> 完全一致，这里只负责把文本解析成 <see cref="Value"/>。
/// </para>
/// <para>
/// **记忆值只作预填**：值解析不了（变量改过类型）就退回默认值、不弹错——
/// 打开一个参数框先被拦住一次，比填错更让人烦躁；真填错了在「运行」时照样会提示。
/// </para>
/// </remarks>
public partial class ParamInputDialog : Window
{
    private readonly List<(VariableDef Def, TextBox Box)> _rows = new();

    public IReadOnlyDictionary<string, Value> Values { get; private set; } =
        new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

    private ParamInputDialog(string title, IEnumerable<VariableDef> variables,
        IReadOnlyDictionary<string, Value>? initial, string? hint)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        HeadText.Text = title;
        if (!string.IsNullOrWhiteSpace(hint)) HintText.Text = hint;

        Build(variables, initial);
        Loaded += (_, _) => _rows.FirstOrDefault().Box?.Focus();
    }

    /// <summary>
    /// 让用户填一次输入值。没有输入变量时不弹窗，直接返回空表；用户取消返回 null。
    /// </summary>
    /// <param name="owner">宿主窗口；null 时自动置顶（面板 / 悬浮这类无宿主入口）。</param>
    /// <param name="title">标题右侧那段（如「截图 → 文字 · 参数」）。</param>
    /// <param name="variables">变量表；只有「输入」方向的会出现在框里。</param>
    /// <param name="initial">预填值（上次填过的），解析不了就退回默认值。</param>
    /// <param name="hint">说明那一行；不给就用通用说明。</param>
    public static IReadOnlyDictionary<string, Value>? Ask(Window? owner, string title,
        IEnumerable<VariableDef> variables, IReadOnlyDictionary<string, Value>? initial = null,
        string? hint = null)
    {
        var list = variables.ToList();
        bool hasInput = list.Any(v =>
            v.Direction == VariableDirection.input && !string.IsNullOrWhiteSpace(v.Key));
        if (!hasInput) return new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        var dialog = new ParamInputDialog(title, list, initial, hint);
        if (owner != null)
        {
            dialog.Owner = owner;
        }
        else
        {
            // 面板 / 快捷键 / 悬浮触发时没有宿主窗口：居中到屏幕（CenterOwner 没有 owner 不可靠），
            // 并且置顶——否则参数框可能停在别的窗口下面，用户以为「按了没反应」。
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.Topmost = ConfirmDialog.ShouldTopmost(owner);
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }

    private void Build(IEnumerable<VariableDef> variables, IReadOnlyDictionary<string, Value>? initial)
    {
        foreach (var variable in variables)
        {
            if (variable.Direction != VariableDirection.input) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0) continue;

            bool multiLine = variable.Type is ValueKind.list or ValueKind.@object;

            // 有显示名就「显示名（变量名）」，没有就只写变量名 —— 变量名才是真参数名。
            string caption = string.IsNullOrWhiteSpace(variable.Label)
                ? key
                : $"{variable.Label}（{key}）";

            Inputs.Children.Add(new TextBlock
            {
                Text = $"{caption} · {KindLabel(variable.Type)}",
                Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(0, _rows.Count == 0 ? 0 : 10, 0, 4),
            });

            var box = new TextBox
            {
                Text = Prefill(variable, initial),
                MinHeight = multiLine ? 64 : 30,
                AcceptsReturn = multiLine,
                TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
            Inputs.Children.Add(box);

            _rows.Add((variable, box));
        }
    }

    /// <summary>预填文本：上次填的（能按当前类型解析才用）→ 变量默认值。</summary>
    private static string Prefill(VariableDef variable, IReadOnlyDictionary<string, Value>? initial)
    {
        string key = (variable.Key ?? "").Trim();
        if (initial != null && initial.TryGetValue(key, out Value? remembered) && remembered != null)
        {
            string text = ListText.Display(remembered);
            if (ListText.TryParseDefault(variable.Type, text, out _, out _)) return text;
        }

        return ListText.Display(variable.Seed());
    }

    /// <summary>每个框填回变量表里的默认值（用户改乱了想重来）。</summary>
    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (def, box) in _rows) box.Text = ListText.Display(def.Seed());
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        foreach (var (variable, box) in _rows)
        {
            string key = (variable.Key ?? "").Trim();
            if (!ListText.TryParseDefault(variable.Type, box.Text, out Value? value, out string error))
            {
                ConfirmDialog.Info(this, "参数", $"「{key}」填得不对：\n{error}");
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
