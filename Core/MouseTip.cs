using System.Windows;
using System.Windows.Threading;
using CapsLockPro.Views;

namespace CapsLockPro.Core;

/// <summary>
/// 跟随鼠标的提示文本框（替代系统托盘气球，对应原版 AHK <c>ToolTip</c> + <c>SetTimer(() =&gt; ToolTip(), -N)</c>）。
/// 在光标右下角显示一段文字，约 1.8s 后自动隐藏；可从任意线程调用（自动 marshal 到 UI 线程）。
/// </summary>
internal static class MouseTip
{
    private const int DefaultDurationMs = 1800;
    private static MouseTipWindow? _window;
    private static DispatcherTimer? _timer;

    /// <summary>复用的隐藏计时器：只在首次创建时挂 Tick，避免每次提示都新建 DispatcherTimer + 委托。</summary>
    private static DispatcherTimer Timer => _timer ??= CreateTimer();

    private static DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer();
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { _window?.Hide(); } catch { /* 静默 */ }
        };
        return t;
    }

    /// <summary>显示提示文本（支持 \n 换行）。</summary>
    public static void Show(string text, int durationMs = DefaultDurationMs)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        if (!disp.CheckAccess())
            disp.BeginInvoke(() => ShowCore(text, durationMs));
        else
            ShowCore(text, durationMs);
    }

    private static void ShowCore(string text, int durationMs)
    {
        Timer.Stop();

        _window ??= new MouseTipWindow();
        _window.SetText(text);

        // 先离屏 Show 让 SizeToContent 拿到真实尺寸，再定位到光标右下角，避免闪现错位。
        _window.Left = -10000;
        _window.Top = -10000;
        _window.Show();
        _window.PlaceNearCursor();

        Timer.Interval = TimeSpan.FromMilliseconds(durationMs);
        Timer.Start();
    }
}
