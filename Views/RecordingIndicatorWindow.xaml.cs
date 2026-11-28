using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Features;
using LTools.Features.Running;

namespace LTools.Views;

/// <summary>
/// 屏幕顶部居中的指示器：录制 / 倒计时 / **自动化运行栈**。置顶、不抢焦点。
/// </summary>
/// <remarks>
/// <para>
/// 录制 / 倒计时是单行、点击穿透（<c>WS_EX_TRANSPARENT</c>）：按钮点不到，也不该点到。
/// 自动化运行时改成最多 4 行、每行右侧 ✕（单独停这一份），并给一行「还有 N 个 · 全部…」，
/// 所以那一态必须**能点**：只去掉 `WS_EX_TRANSPARENT`，`WS_EX_NOACTIVATE` 保留（点 ✕ 不抢前台）。
/// </para>
/// <para>
/// 行内容来自注册表（<see cref="RunningActions.Snapshot"/>），500ms 刷一次——不是由每个运行各推一份文本，
/// 并发时谁也不会把别人的行冲掉。截图前 <see cref="SuppressForCapture"/> 收整组。
/// </para>
/// </remarks>
public partial class RecordingIndicatorWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;

    /// <summary>最多显示几行（更多折叠成「还有 N 个」）。</summary>
    private const int MaxRows = 4;

    private static RecordingIndicatorWindow? _window;
    private static DispatcherTimer? _timer;
    private static DispatcherTimer? _automationTimer;
    private static bool _automation;
    private static string? _automationNote;   // 底部灰字的额外说明（停止中 / 慢步骤），随重绘保留

    public RecordingIndicatorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            SyncClickThrough();
            StayClickable.Attach(this);
        };
    }

    /// <summary>显示录制中指示器（由 <see cref="MacroRecorder"/> 调用）。</summary>
    public static void ShowIndicator()
    {
        _automation = false;
        var w = EnsureWindow();
        w.RecordingPanel.Visibility = Visibility.Visible;
        w.AutomationPanel.Visibility = Visibility.Collapsed;
        w.Dot.Visibility = Visibility.Visible;
        w.UpdateRecordingText();
        w.SyncClickThrough();
        w.ShowPill();

        var t = _timer ??= CreateTimer();
        t.Start();
    }

    /// <summary>显示回放倒计时（如「3 秒后回放…」）。</summary>
    public static void ShowCountdown(string text)
    {
        _automation = false;
        var w = EnsureWindow();
        w.RecordingPanel.Visibility = Visibility.Visible;
        w.AutomationPanel.Visibility = Visibility.Collapsed;
        w.Dot.Visibility = Visibility.Collapsed;
        w.IndicatorText.Text = $"{text} 秒后回放…";
        w.SyncClickThrough();
        w.ShowPill();
        _timer?.Stop();
    }

    /// <summary>
    /// 显示自动化运行栈（行内容来自注册表，500ms 自动重绘）。
    /// <paramref name="note"/> 是底部灰字的额外说明（「正在停止 · 等待当前步骤返回」/「第 3 行已运行 12 秒」）；
    /// 为空时用默认那句（停止热键 + 并发提示）。**没有任何运行时直接收起**——别留一扇空窗口。
    /// </summary>
    public static void ShowAutomation(string? note = null)
    {
        // 流程在后台线程运行：窗口创建 / 更新必须回 UI 线程。
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowAutomation(note));
            return;
        }

        // 没有运行 = 没什么可显示：截图 / 识别结束后的「恢复」、停一个已经结束的运行都会走到这里。
        if (RunningActions.Count == 0)
        {
            HideAutomation();
            return;
        }

        _automation = true;
        _automationNote = note;
        _timer?.Stop();

        var w = EnsureWindow();
        w.RecordingPanel.Visibility = Visibility.Collapsed;
        w.AutomationPanel.Visibility = Visibility.Visible;
        w.SyncClickThrough();

        if (!w.IsVisible) w.ShowPill();
        w.RenderAutomation();
        w.Recenter();

        var timer = _automationTimer ??= CreateAutomationTimer();
        timer.Start();
    }

    /// <summary>自动化指示器现在是不是显示着（冒烟 / 排查用）。</summary>
    public static bool IsAutomationShowing => _automation && _window?.IsVisible == true;

    /// <summary>隐藏自动化指示器（只隐藏自己这个态，避免误关录制 / 倒计时）。</summary>
    public static void HideAutomation()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => HideAutomation());
            return;
        }

        if (!_automation) return;
        _automation = false;
        _automationNote = null;
        _automationTimer?.Stop();
        HideIndicator();
    }

    /// <summary>
    /// 截图 / OCR 前临时收起自动化指示器（它是置顶窗口，会被截进画面）。
    /// 返回**它本来在不在显示**：没显示就别恢复（以前这里返回「文本」当令牌，空串恢复时
    /// 会开出一扇空窗口留在屏幕顶上——截图 Pro 的识别走的就是这条路，用户报过）。
    /// 同步等待 UI 线程完成隐藏，避免截到半透明淡出。
    /// </summary>
    public static bool HideAutomationForCapture()
    {
        var app = Application.Current;
        if (app == null || !_automation) return false;

        bool wasShowing = false;
        void HideNow()
        {
            if (!_automation) return;
            wasShowing = true;
            _automation = false;
            _automationNote = null;
            _automationTimer?.Stop();
            HideIndicator();
        }

        if (app.Dispatcher.CheckAccess()) HideNow();
        else app.Dispatcher.Invoke(HideNow);
        return wasShowing;
    }

    /// <summary>恢复此前被 <see cref="HideAutomationForCapture"/> 收起的自动化指示器（按注册表重画）。</summary>
    public static void RestoreAutomation(bool wasShowing)
    {
        if (wasShowing) ShowAutomation();
    }

    /// <summary>
    /// 截图 / 识别期间的「收起自动化指示器」作用域：<c>using var scope = SuppressForCapture();</c>
    /// 一次搞定收起与恢复，别在调用点各写一遍 <c>try / finally</c>。
    /// </summary>
    public static CaptureSuppression SuppressForCapture() => new(HideAutomationForCapture());

    /// <summary><see cref="SuppressForCapture"/> 的作用域对象，<c>Dispose</c> 时把指示器恢复回去。</summary>
    public sealed class CaptureSuppression : IDisposable
    {
        private bool _wasShowing;

        internal CaptureSuppression(bool wasShowing) => _wasShowing = wasShowing;

        /// <summary>收起前它到底有没有显示（要等桌面重绘的调用点看这个）。</summary>
        public bool WasShowing => _wasShowing;

        public void Dispose()
        {
            bool restore = _wasShowing;
            _wasShowing = false;
            RestoreAutomation(restore);
        }
    }

    /// <summary>隐藏指示器。</summary>
    public static void HideIndicator()
    {
        _timer?.Stop();
        try { _window?.Hide(); } catch { /* 静默 */ }
    }

    // ============ 自动化栈 ============

    /// <summary>一行：runId + 显示文本（绑定用，属性必须是 public）。</summary>
    public sealed class RunRow
    {
        public required string RunId { get; init; }
        public required string Text { get; init; }
    }

    private void RenderAutomation()
    {
        IReadOnlyList<ActionRunSession> runs = RunningActions.Snapshot();

        // 运行都结束了：别留一扇空窗口（也兜住「恢复时已经没得显示」的情况）。
        if (runs.Count == 0)
        {
            HideAutomation();
            return;
        }

        var rows = new List<RunRow>(Math.Min(runs.Count, MaxRows));
        foreach (ActionRunSession session in runs.Take(MaxRows))
        {
            bool stopping = session.StopRequested || session.Cts.IsCancellationRequested;
            string step = session.Step > 0 ? $" · 第 {session.Step} 步" : "";
            rows.Add(new RunRow
            {
                RunId = session.RunId,
                Text = session.Label + step + (stopping ? " · 正在停止" : ""),
            });
        }

        AutomationRows.ItemsSource = rows;

        int more = runs.Count - rows.Count;
        MorePanel.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreText.Text = more > 0 ? $"还有 {more} 个" : "";

        AutomationFoot.Text = string.IsNullOrWhiteSpace(_automationNote)
            ? $"按 {MacroSettings.PlaybackStopHotkeyDisplay} 停止全部 · 运行时鼠标类步骤可能相互影响"
            : _automationNote!;
    }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RunRow row }) return;
        if (string.IsNullOrEmpty(row.RunId)) return;
        RunningActions.Cancel(row.RunId);
        RenderAutomation();
    }

    private void OpenList_Click(object sender, RoutedEventArgs e)
    {
        // 运行列表窗口与注册表在同一个进程（提权进程）：直接开。
        RunningActionsWindow.Toggle();
    }

    private void Recenter()
    {
        if (!IsVisible) return;
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 12;
    }

    // ============ 基础设施 ============

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

    private static DispatcherTimer CreateAutomationTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            if (!_automation) { t.Stop(); return; }

            // 运行都结束了（哪怕收尾时漏了一次隐藏）：自己收起来，别在屏幕顶上留一扇空窗口。
            if (RunningActions.Count == 0) { HideAutomation(); return; }
            _window?.RenderAutomation();
        };
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

    /// <summary>
    /// 自动化态要能点（✕ / 全部…），录制 / 倒计时保持点击穿透。
    /// 只切换 `WS_EX_TRANSPARENT`，`WS_EX_NOACTIVATE` 一直保留（点按钮不抢焦点）。
    /// </summary>
    private void SyncClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long ex = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        long next = _automation
            ? ex & ~(long)WsExTransparent
            : ex | WsExTransparent;

        SetWindowLongPtr(hwnd, GwlExstyle,
            (IntPtr)(next | WsExNoactivate | WsExToolwindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
