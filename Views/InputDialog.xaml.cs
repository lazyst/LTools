using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 通用输入对话框（对应 AHK InputBox）。模态；单行或多行。
/// </summary>
/// <remarks>
/// 多行模式用于「自动化 · 输入文本」要一段文字（多行内容、长文本）的场景：
/// 回车换行、Ctrl+Enter 确定。单行模式保持原来的回车即确定。
/// </remarks>
public partial class InputDialog : Window
{
    /// <summary>结果值（确定时填充）。</summary>
    public string Value { get; private set; } = "";

    private bool _ok;

    public InputDialog(string title, string prompt, string defaultValue = "",
        IReadOnlyList<string>? variables = null, bool multiline = false)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        // 传入变量时输入框右侧出现「$」按钮（流程里的文本片段可能需要 ${变量}）。
        VariableSource.SetNames(this, variables);
        ValueBox.Text = defaultValue;

        if (multiline)
        {
            Width = 520;
            ValueBox.AcceptsReturn = true;
            ValueBox.TextWrapping = TextWrapping.Wrap;
            ValueBox.VerticalContentAlignment = VerticalAlignment.Top;
            ValueBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            ValueBox.MinHeight = 120;
            ValueBox.MaxHeight = 280;
            MultilineHint.Visibility = Visibility.Visible;
            // 多行时 TextBox 自己吃掉 Enter，默认按钮收不到，用 Ctrl+Enter 确定。
            ValueBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Ok_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            };
        }

        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>弹出模态输入框。owner 可空；<paramref name="variables"/> 非空时提供变量插入按钮。</summary>
    public static (bool ok, string value) Show(Window? owner, string title, string prompt, string defaultValue = "",
        IReadOnlyList<string>? variables = null, bool multiline = false)
    {
        var dlg = new InputDialog(title, prompt, defaultValue, variables, multiline);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        dlg.ShowDialog();
        return (dlg._ok, dlg.Value);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text;
        _ok = true;
        DialogResult = true;
    }
}
