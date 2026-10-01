using System.Windows;

namespace LTools.Views;

/// <summary>通用单行输入对话框（对应 AHK InputBox）。模态。</summary>
public partial class InputDialog : Window
{
    /// <summary>结果值（确定时填充）。</summary>
    public string Value { get; private set; } = "";

    private bool _ok;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = defaultValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>弹出模态输入框。owner 可空。</summary>
    public static (bool ok, string value) Show(Window? owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new InputDialog(title, prompt, defaultValue);
        if (owner != null) dlg.Owner = owner;
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
