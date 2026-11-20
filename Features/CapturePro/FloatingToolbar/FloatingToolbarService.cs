using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Core;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 悬浮工具条的进程内单例：按设置建 / 关窗口，并在「截图 / 录屏进行中」与
/// 「前台窗口全屏」时自动收起。
/// </summary>
/// <remarks>
/// 启动时调一次 <see cref="Start"/>（订阅设置变更）；设置页改任何一项都经
/// <see cref="FloatingToolbarStore.Changed"/> 回到 <see cref="Apply"/> 重建。
/// 全屏判定每秒轮询一次（<c>GetForegroundWindow</c> + DWM 可见范围对显示器矩形），
/// 不做 WinEvent 钩子——这条 1 秒的定时器只在工具条开着时跑。
/// </remarks>
internal static class FloatingToolbarService
{
    private static CaptureFloatingBarWindow? _window;
    private static DispatcherTimer? _timer;
    private static bool _captureActive;
    private static bool _started;

    /// <summary>启动（幂等）：订阅设置变更并立刻按当前设置显示。</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        FloatingToolbarStore.Changed += Apply;
        Apply();
    }

    /// <summary>按当前设置重建（开关关掉就关窗口）。</summary>
    public static void Apply() => Post(() =>
    {
        CloseWindow();

        FloatingToolbarSettings settings = FloatingToolbarStore.Current;
        if (!settings.Enabled)
        {
            StopTimer();
            return;
        }

        _window = new CaptureFloatingBarWindow(settings, FloatingToolbarStore.Layout);
        _window.Show();
        StartTimer();
        UpdateVisibility();
    });

    /// <summary>
    /// 投到 UI 线程。优先用 <see cref="Ui"/>（生产路径）；没初始化（冒烟 / 单元环境）
    /// 就退回 <see cref="Application.Current"/> 的 Dispatcher，别再静默丢掉。
    /// </summary>
    private static void Post(Action action)
    {
        if (Ui.IsUiThread)
        {
            action();
            return;
        }

        if (Application.Current is { } app)
        {
            if (app.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(action);
            return;
        }

        Ui.Post(action);
    }

    /// <summary>截图 / 录屏开始与结束时调（照上游 <c>hide_during_capture</c>）。</summary>
    public static void SetCaptureActive(bool active)
    {
        if (_captureActive == active) return;
        _captureActive = active;
        Post(UpdateVisibility);
    }

    /// <summary>当前工具条窗口（冒烟 / 设置页摘要用；没开时 <c>null</c>）。</summary>
    internal static Window? Window => _window;

    private static void UpdateVisibility()
    {
        if (_window == null) return;

        FloatingToolbarSettings settings = FloatingToolbarStore.Current;
        bool hide = (settings.HideDuringCapture && _captureActive)
                    || (settings.HideInFullscreen && IsFullscreenForeground());

        if (hide)
        {
            if (_window.IsVisible) _window.Hide();
        }
        else if (!_window.IsVisible)
        {
            _window.Show();
        }
    }

    /// <summary>前台窗口是否正好盖满某一块显示器（照上游 <c>hide_in_fullscreen</c>）。</summary>
    private static bool IsFullscreenForeground()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (_window != null && hwnd == new WindowInteropHelper(_window).Handle) return false;

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        if (w <= 0 || h <= 0) return false;

        foreach (var screen in Screens.All())
        {
            if (Math.Abs(x - screen.Left) <= 2 && Math.Abs(y - screen.Top) <= 2
                && Math.Abs(x + w - (screen.Left + screen.Width)) <= 2
                && Math.Abs(y + h - (screen.Top + screen.Height)) <= 2)
                return true;
        }

        return false;
    }

    private static void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        if (_timer.IsEnabled) return;

        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e) => UpdateVisibility();

    private static void StopTimer() => _timer?.Stop();

    private static void CloseWindow()
    {
        if (_window == null) return;

        try { _window.Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.FloatingBar", "关悬浮工具条失败：" + ex.Message); }
        finally { _window = null; }
    }
}
