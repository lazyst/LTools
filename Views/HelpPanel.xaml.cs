using System.Windows;
using System.Windows.Input;

namespace LTools.Views;

/// <summary>帮助面板窗口（无边框置顶 + 只读滚动文本）。Esc / 失焦 / 关闭按钮 关闭。</summary>
public partial class HelpPanelWindow : Window
{
    private bool _isClosing;

    public HelpPanelWindow(string text)
    {
        InitializeComponent();
        ContentText.Text = text;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // 失焦自动关闭；但显式 Close 期间会再触发 Deactivated —— 守卫防重入崩溃。
        if (_isClosing) return;
        _isClosing = true;
        try { Close(); } catch { /* 静默 */ }
    }
}
