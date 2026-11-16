using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏倒计时：盖住要录的那块区域，数到 0 自己关掉。
/// </summary>
/// <remarks>
/// <para>
/// 不抢焦点（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>），
/// 也不入镜（<c>WDA_EXCLUDEFROMCAPTURE</c>）——它正好压在要录的内容上。
/// </para>
/// <para>
/// 非模态：会话在后台线程轮询 <see cref="Finished"/>；右键 / 「取消」= <see cref="Cancelled"/>。
/// </para>
/// </remarks>
public partial class RecordCountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remaining;
    private IntPtr _hwnd;

    internal RecordCountdownWindow(Int32Rect area, int seconds)
    {
        InitializeComponent();

        _remaining = Math.Max(1, seconds);

        // 按目标显示器的 DPI 换算成 DIP（与录屏控制条同一套落点规则）
        var screen = CaptureRecordWindow.ScreenOf(area);
        double scale = screen is { Scale: > 0 } ? screen.Scale : 1;

        Left = area.X / scale;
        Top = area.Y / scale;
        Width = Math.Max(1, area.Width / scale);
        Height = Math.Max(1, area.Height / scale);

        CountLabel.Text = _remaining.ToString();
        HintText.Text = $"{_remaining} 秒后开始录屏 · 右键取消";

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>倒计时走完（或被取消）之后不再变。</summary>
    internal volatile bool Finished;

    /// <summary>用户主动取消。</summary>
    internal volatile bool Cancelled;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 刻意不 Activate / Focus：抢了前台，要录的窗口（比如展开的菜单）就变了样。
        // 取消是鼠标右键 / 按钮，不需要键盘焦点。
        _timer.Start();
    }

    private void Tick()
    {
        _remaining--;

        if (_remaining > 0)
        {
            CountLabel.Text = _remaining.ToString();
            HintText.Text = $"{_remaining} 秒后开始录屏 · 右键取消";
            return;
        }

        Finish(false);
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e) => Cancel_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(true);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // 别的关闭方式：当取消处理，别让会话一直等一个永远不结束的倒计时
        if (Finished) return;
        Cancelled = true;
        Finished = true;
        _timer.Stop();
    }

    private void Finish(bool cancelled)
    {
        if (Finished) return;

        Cancelled = cancelled;
        Finished = true;
        _timer.Stop();

        try { Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关倒计时失败：" + ex.Message); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));

        Win32.SetWindowDisplayAffinity(_hwnd, Win32.WdaExcludeFromCapture);

        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }
}
