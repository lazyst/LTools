using System.Windows;

namespace LTools.Views;

/// <summary>通用确认/提示对话框（替代原生 MessageBox）。危险动作用红按钮；
/// 速记保存场景支持「保存 / 不保存 / 取消」三选。</summary>
public partial class ConfirmDialog : Window
{
    private bool _ok;
    private bool _discard;

    /// <param name="title">窗口标题。</param>
    /// <param name="message">正文消息。</param>
    /// <param name="danger">危险动作（删除）→ 确认按钮用红色 BtnDanger，文案“确认删除”。</param>
    /// <param name="info">仅提示（单按钮）→ 隐藏取消，确认按钮文案“确定”。</param>
    /// <param name="discard">速记保存三选 → 显示「取消 / 不保存 / 保存」。</param>
    private ConfirmDialog(string title, string message, bool danger, bool info, bool discard)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        if (discard)
        {
            DiscardBtn.Visibility = Visibility.Visible;
            OkBtn.Content = "保存";
        }
        else if (info)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            OkBtn.Content = "确定";
        }
        else if (danger)
        {
            OkBtn.Style = (Style)FindResource("BtnDanger");
            OkBtn.Content = "确认删除";
        }

        Loaded += (_, _) => OkBtn.Focus();
    }

    /// <summary>是/否确认。danger=true 时确认按钮为红色。返回是否确认。</summary>
    public static bool Confirm(Window? owner, string title, string message, bool danger = false)
    {
        var dlg = new ConfirmDialog(title, message, danger, false, false);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        dlg.ShowDialog();
        return dlg._ok;
    }

    /// <summary>单按钮提示。</summary>
    public static void Info(Window? owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message, false, true, false);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        dlg.ShowDialog();
    }

    /// <summary>速记保存三选：保存 / 不保存 / 取消。</summary>
    public static SaveConfirmResult ConfirmDiscard(Window? owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message, false, false, true);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        dlg.ShowDialog();
        if (dlg._ok) return SaveConfirmResult.Save;
        if (dlg._discard) return SaveConfirmResult.Discard;
        return SaveConfirmResult.Cancel;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _ok = true;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        _discard = true;
        DialogResult = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>速记保存确认结果。</summary>
public enum SaveConfirmResult { Save, Discard, Cancel }
