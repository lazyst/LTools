using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏控制条：计时 + 暂停 / 继续 + 完成 / 取消。
/// </summary>
/// <remarks>
/// <para>
/// **不抢焦点**（与贴图 / 长截图面板同一套）：录制时用户还在别的程序里操作，
/// 控制条一抢前台就会把之后的键鼠都打在自己身上。代价是收不到 <c>Esc</c>，
/// 所以三个出口都做成鼠标按钮。
/// </para>
/// <para>
/// 自己**不入镜**：<c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>
/// （Win10 2004+；老系统调用失败时窗口会照常被录进去，但录的内容不受影响）。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow : Window
{
    private readonly Int32Rect _area;
    private readonly RecordController _controller;
    private readonly DispatcherTimer _timer;

    private DoubleAnimation? _pulse;
    private IntPtr _hwnd;
    private bool _closing;

    internal CaptureRecordWindow(Int32Rect area, RecordPlan plan, RecordController controller)
    {
        InitializeComponent();

        _area = area;
        _controller = controller;

        MetaText.Text =
            $"{RecordPlan.FormatLabel(plan.Extension)} · {plan.EffectiveFps} fps · {area.Width} × {area.Height}";

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Sync();

        DpiChanged += (_, _) => PlaceBar();
    }

    internal RecordController Controller => _controller;

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceBar();
        UpdateLayout();
        PlaceBar();   // 量过高度之后再落一次（SizeToContent 会把它撑高）

        StartPulse();
        Sync();
        _timer.Start();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();

        // 任何别的关闭方式（系统关窗 / 任务管理器）= 取消，别留下一直在录的后台线程
        if (!_closing) _controller.Cancel();
    }

    /// <summary>收尾之后由会话调用：安静地关掉，不算用户取消。</summary>
    internal void CloseQuietly()
    {
        _closing = true;
        try { Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关控制条失败：" + ex.Message); }
    }

    // ============ 状态 ============

    private void Sync()
    {
        ClockLabel.Text = _controller.ClockText;

        bool paused = _controller.IsPaused;
        PauseBtn.Content = paused ? "继续" : "暂停";
        Dot.Fill = BrushOf(paused ? "#A1A1AA" : "#EF4444");

        if (_controller.Finished)
        {
            StatusText.Text = "正在收尾…";
            PauseBtn.IsEnabled = false;
            DoneBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            return;
        }

        StatusText.Text = paused
            ? "已暂停 · 时间不再走"
            : "录制中 · 点「完成」保存到采集历史";
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Finished) return;

        if (_controller.TogglePause()) StopPulse();
        else StartPulse();

        Sync();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        Sync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _controller.Cancel();
        Sync();
    }

    // ============ 界面 ============

    private void StartPulse()
    {
        if (_pulse != null) return;

        _pulse = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Dot.BeginAnimation(OpacityProperty, _pulse);
    }

    private void StopPulse()
    {
        Dot.BeginAnimation(OpacityProperty, null);
        Dot.Opacity = 1;
    }

    /// <summary>落在录制区域那块显示器的底部居中（不盖住选区中间的内容）。</summary>
    private void PlaceBar()
    {
        var screen = ScreenOf(_area);
        if (screen == null) return;

        double scale = screen.Scale > 0 ? screen.Scale : 1;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 110;

        double left = screen.WorkLeft / scale + (Math.Max(1, screen.WorkWidth) / scale - width) / 2;
        double top = (screen.WorkTop + screen.WorkHeight) / scale - height - 28;

        Left = Math.Max(screen.WorkLeft / scale + 8, left);
        Top = Math.Max(screen.WorkTop / scale + 8, top);
    }

    /// <summary>录制区域中心所在显示器；找不到退回第一块。</summary>
    internal static ScreenGeometry? ScreenOf(Int32Rect area)
    {
        int cx = area.X + area.Width / 2;
        int cy = area.Y + area.Height / 2;

        var all = Screens.All();
        foreach (var screen in all)
        {
            if (cx < screen.Left || cx >= screen.Left + screen.Width) continue;
            if (cy < screen.Top || cy >= screen.Top + screen.Height) continue;
            return screen;
        }

        return all.Count > 0 ? all[0] : null;
    }

    private static Brush BrushOf(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
    }
}
