using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 屏幕顶部居中的录制 / 倒计时指示器：置顶、点击穿透、不抢焦点。
/// 录制时显示红点 + 时长 + 事件数；回放倒计时时显示剩余秒数。
/// </summary>
/// <remarks>
/// 不提供可点击按钮：停止录制用编辑器内的限定热键（托盘发起的录制用托盘菜单停止），
/// 避免按钮点击本身被录进宏。窗口通过 <c>WS_EX_TRANSPARENT</c> 让鼠标事件穿透到下层应用。
/// </remarks>
public partial class RecordingIndicatorWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;

    private static RecordingIndicatorWindow? _window;
    private static DispatcherTimer? _timer;

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>显示录制中指示器（由 <see cref="MacroRecorder"/> 调用）。</summary>
    public static void ShowIndicator()
    {
        var w = EnsureWindow();
        w.Dot.Visibility = Visibility.Visible;
        w.UpdateRecordingText();
        w.ShowPill();

        var t = _timer ??= CreateTimer();
        t.Start();
    }

    /// <summary>显示回放倒计时（如「3 秒后回放…」）。</summary>
    public static void ShowCountdown(string text)
    {
        var w = EnsureWindow();
        w.Dot.Visibility = Visibility.Collapsed;
        w.IndicatorText.Text = $"{text} 秒后回放…";
        w.ShowPill();
        _timer?.Stop();
    }

    /// <summary>隐藏指示器。</summary>
    public static void HideIndicator()
    {
        _timer?.Stop();
        try { _window?.Hide(); } catch { /* 静默 */ }
    }

    private static RecordingIndicatorWindow EnsureWindow()
    {
        if (_window == null)
        {
            _window = new RecordingIndicatorWindow();
            _window.Closed += (_, _) => _window = null;
        }
        return _window;
    }

    private static DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        t.Tick += (_, _) => _window?.UpdateRecordingText();
        return t;
    }

    private void UpdateRecordingText()
    {
        int totalSeconds = MacroRecorder.ElapsedMs / 1000;
        // 录制中热键一定是可用的（停止不受编辑器会话限制），直接提示热键。
        IndicatorText.Text =
            $"录制中 {totalSeconds / 60:00}:{totalSeconds % 60:00} · {MacroRecorder.EventCount} 事件 · {MacroSettings.RecordHotkeyDisplay} 停止";
    }

    private void ShowPill()
    {
        // 先离屏显示让 SizeToContent 量出真实尺寸，再定位到屏幕顶部居中，避免闪现错位。
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 12;
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long ex = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExstyle,
            (IntPtr)(ex | WsExTransparent | WsExNoactivate | WsExToolwindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
