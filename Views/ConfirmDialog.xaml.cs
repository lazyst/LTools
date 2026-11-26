using System.Windows;

namespace LTools.Views;

/// <summary>通用确认/提示对话框（替代原生 MessageBox）。危险动作用红按钮；
/// 速记保存场景支持「保存 / 不保存 / 取消」三选。</summary>
/// <remarks>
/// 无 Owner（托盘 / MCP 回调 / 面板已关闭）时自动置顶，免得确认框被别的窗口盖住，见 <see cref="ShouldTopmost"/>。
/// 全项目除启动期单实例提示（WPF 还没起来）外一律走这里，别再调 <c>MessageBox</c>。
/// </remarks>
public partial class ConfirmDialog : Window
{
    private bool _ok;
    private bool _discard;

    /// <param name="title">窗口标题。</param>
    /// <param name="message">正文消息。</param>
    /// <param name="danger">危险动作（如删除）→ 确认按钮用红色 BtnDanger。</param>
    /// <param name="okText">确认按钮文案（默认：danger 时「确认删除」，否则「确认」）。导出/导入等非删除动作应显式传入。</param>
    /// <param name="info">仅提示（单按钮）→ 隐藏取消，确认按钮文案「确定」。</param>
    /// <param name="discard">三选（保存 / 不保存 / 取消）→ 显示「取消 / 不保存 / 保存」。</param>
    private ConfirmDialog(string title, string message, bool danger, bool info, bool discard, string? okText)
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
        else
        {
            if (danger)
            {
                OkBtn.Style = (Style)FindResource("BtnDanger");
                OkBtn.Content = string.IsNullOrEmpty(okText) ? "确认删除" : okText;
            }
            else
            {
                OkBtn.Content = string.IsNullOrEmpty(okText) ? "确认" : okText;
            }
        }

        Loaded += (_, _) => OkBtn.Focus();
    }

    /// <summary>是/否确认。danger=true 时确认按钮为红色。返回是否确认。</summary>
    public static bool Confirm(Window? owner, string title, string message, bool danger = false, string? okText = null)
    {
        var dlg = new ConfirmDialog(title, message, danger, false, false, okText);
        ApplyOwner(dlg, owner);
        dlg.ShowDialog();
        return dlg._ok;
    }

    /// <summary>
    /// 可撤销的确认：<paramref name="cancel"/> 触发时自己关掉并返回「否」。
    /// </summary>
    /// <remarks>
    /// 给「等对方 / 等超时」的确认用（局域网共享的接收确认）：调用方在后台线程等答复，
    /// 超时或对端放弃时要把这扇模态框收掉，否则它会一直压着界面。
    /// **关窗一律从对话框自己的 Dispatcher 投递**，别的线程直接 <c>Close()</c> 会抛。
    /// </remarks>
    internal static bool Confirm(Window? owner, string title, string message, string okText, CancellationToken cancel)
    {
        var dlg = new ConfirmDialog(title, message, false, false, false, okText);
        ApplyOwner(dlg, owner);

        // 取消可能正好发生在「框还没显示出来」的那一下（注册时 IsVisible 还是 false），
        // 所以显示出来后再补一次——否则这一扇会一直挂着。
        using var registration = cancel.Register(() => CloseFromAnyThread(dlg));
        dlg.Loaded += (_, _) => { if (cancel.IsCancellationRequested) CloseFromAnyThread(dlg); };
        dlg.ShowDialog();
        return dlg._ok;
    }

    private static void CloseFromAnyThread(Window dlg)
    {
        try
        {
            if (!dlg.IsVisible) return;
            dlg.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { dlg.Close(); } catch { /* 已经关掉了 */ }
            }));
        }
        catch
        {
            // 窗口已释放 / Dispatcher 已关：没人可收，ShowDialog 那边自己会结束。
        }
    }

    /// <summary>单按钮提示。</summary>
    public static void Info(Window? owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message, false, true, false, null);
        ApplyOwner(dlg, owner);
        dlg.ShowDialog();
    }

    /// <summary>三选：保存 / 不保存 / 取消（速记、动作编辑器关闭时共用）。</summary>
    public static SaveConfirmResult ConfirmDiscard(Window? owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message, false, false, true, null);
        ApplyOwner(dlg, owner);
        dlg.ShowDialog();
        if (dlg._ok) return SaveConfirmResult.Save;
        if (dlg._discard) return SaveConfirmResult.Discard;
        return SaveConfirmResult.Cancel;
    }

    /// <summary>
    /// 确认框该不该置顶：有 Owner 就跟随它（面板是 Topmost 时别掉到它后面）；
    /// <b>没有 Owner</b>（托盘 / MCP 回调 / 面板已关闭）就自己置顶——那时没有任何 LTools 窗口能把它带上来。
    /// </summary>
    internal static bool ShouldTopmost(Window? owner) => owner?.Topmost ?? true;

    private static void ApplyOwner(ConfirmDialog dlg, Window? owner)
    {
        if (owner != null) dlg.Owner = owner;
        dlg.Topmost = ShouldTopmost(owner);
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

/// <summary>保存 / 不保存 / 取消的三选结果。</summary>
public enum SaveConfirmResult { Save, Discard, Cancel }
