using System.Collections.Generic;
using System.Windows;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>通用单行输入对话框（对应 AHK InputBox）。模态。</summary>
public partial class InputDialog : Window
{
    /// <summary>结果值（确定时填充）。</summary>
    public string Value { get; private set; } = "";

    private bool _ok;

    public InputDialog(string title, string prompt, string defaultValue = "", IReadOnlyList<string>? variables = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        // 传入变量时输入框右侧出现「$」按钮（流程里的文本片段可能需要 ${变量}）。
        VariableSource.SetNames(this, variables);
        ValueBox.Text = defaultValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>弹出模态输入框。owner 可空；<paramref name="variables"/> 非空时提供变量插入按钮。</summary>
    public static (bool ok, string value) Show(Window? owner, string title, string prompt, string defaultValue = "",
        IReadOnlyList<string>? variables = null)
    {
        var dlg = new InputDialog(title, prompt, defaultValue, variables);
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
