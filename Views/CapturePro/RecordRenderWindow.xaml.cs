using System;
using System.Windows;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 渲染进度对话框（照 Snow Shot 的 <c>RecordingRenderDialog</c>）：录完之后的收尾 / 动图转码 /
/// 裁剪导出都走它，状态行 + 4px 进度条 + 失败后「重试 / 保留源文件 / 丢弃」。
/// </summary>
/// <remarks>
/// <para>
/// 渲染跑在后台线程（<see cref="RecordRenderRunner"/>），这里只负责画：状态由
/// <see cref="Apply"/> 灌进来，用户点了哪个按钮由 <see cref="Choice"/> 读出去。
/// **不要在这里做逻辑判断**——文案 / 显隐全在 <see cref="RecordRender"/> 的纯函数里。
/// </para>
/// <para>
/// 关窗策略照上游 Manual：渲染中关窗 = 按「取消」，已经停下来的态关窗 = 「保留源文件」。
/// 不设 Owner（渲染时录屏控制条已经关了），放在录制区域那块屏的工作区正中。
/// </para>
/// </remarks>
public partial class RecordRenderWindow : Window
{
    private RecordRenderState _state = new();
    private RecordRenderChoice _choice = RecordRenderChoice.None;
    private bool _cancelRequested;
    private bool _closed;

    internal RecordRenderWindow(string sourcePath, Int32Rect anchor)
    {
        InitializeComponent();

        _state.Path = sourcePath;
        Refresh();
        PlaceOnScreen(anchor);
    }

    /// <summary>用户选了哪个（<see cref="RecordRenderChoice.None"/> = 还没选）。</summary>
    internal RecordRenderChoice Choice => _choice;

    /// <summary>用户按过「取消」（渲染线程每 60ms 问一次）。</summary>
    internal bool CancelRequested => _cancelRequested;

    /// <summary>当前状态的一份拷贝（冒烟断言用）。</summary>
    internal RecordRenderState State => _state.Clone();

    /// <summary>把新状态画上去（**必须在 UI 线程**）。</summary>
    /// <remarks>
    /// 窗口自己的「取消」不能被进度的推送抹掉：渲染线程 60ms 推一次状态，而它手里的
    /// 副本里 <c>CancelRequested</c> 永远是 false——不并上这一位的话，用户点了取消，
    /// 下一拍就被覆盖回去，ffmpeg 一直跑到底（冒烟里表现为卡死）。
    /// </remarks>
    internal void Apply(RecordRenderState state)
    {
        _state = state.Clone();
        if (_cancelRequested) _state.CancelRequested = true;
        Refresh();
    }

    /// <summary>重试：把上一次的取消与选择一起清掉（**UI 线程**，渲染线程每轮开工时调）。</summary>
    internal void ResetAttempt()
    {
        _cancelRequested = false;
        _choice = RecordRenderChoice.None;
        Refresh();
    }

    /// <summary>
    /// 进入「等用户选」（**UI 线程**）。**不能清掉已有的选择**：用户可能在渲染线程
    /// 刚推完状态、还没轮到轮询时就点了按钮，清掉就再也等不到了（冒烟会直接卡住）。
    /// 重试时清选择是 <see cref="ResetAttempt"/> 的事。
    /// </summary>
    internal void BeginAsk() => Refresh();

    /// <summary>收尾：安静关掉（**UI 线程**）。</summary>
    internal void CloseQuietly()
    {
        _closed = true;

        try { Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关渲染进度窗失败：" + ex.Message); }
    }

    // ============ 画 ============

    private void Refresh()
    {
        if (StatusText == null) return;   // XAML 还没初始化完（构造里抛异常时会走到 OnClosing）

        bool retained = _state.Retained;
        bool failed = retained && !string.IsNullOrEmpty(_state.Error);
        bool done = _state.TerminalSucceeded;

        StatusText.Text = RecordRender.StatusText(_state);

        double percent = Math.Clamp(_state.Percent, 0, 100);
        Progress.Value = Math.Floor(percent);
        Progress.Foreground = Brush(failed ? "CaptureDanger" : done ? "CaptureSuccess" : "CapturePrimary");

        DetailsText.Visibility = RecordRender.ShowDetails(_state) ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = RecordRender.DetailsText(_state);

        bool chosen = _choice != RecordRenderChoice.None;

        CancelBtn.Visibility = RecordRender.ShowCancel(_state) ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.IsEnabled = RecordRender.CancelEnabled(_state) && !chosen;

        var retainedVisibility = RecordRender.ShowRetained(_state) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { RetryBtn, KeepBtn, DiscardBtn })
        {
            button.Visibility = retainedVisibility;
            button.IsEnabled = RecordRender.RetainedEnabled(_state) && !chosen;
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // ============ 摆位 ============

    /// <summary>放在录制区域那块屏的工作区正中（多屏时别跑到主屏去）。</summary>
    private void PlaceOnScreen(Int32Rect anchor)
    {
        var screens = Screens.All();
        if (screens.Count == 0) return;

        int cx = anchor.Width > 0 ? anchor.X + anchor.Width / 2 : anchor.X;
        int cy = anchor.Height > 0 ? anchor.Y + anchor.Height / 2 : anchor.Y;

        ScreenGeometry screen = screens[0];
        foreach (var candidate in screens)
        {
            if (cx >= candidate.Left && cx < candidate.Left + candidate.Width
                && cy >= candidate.Top && cy < candidate.Top + candidate.Height)
            {
                screen = candidate;
                break;
            }
        }

        double scale = screen.Scale > 0 ? screen.Scale : 1;
        double width = Width * scale;
        double height = Math.Max(120, ActualHeight > 0 ? ActualHeight : 160) * scale;

        Left = (screen.WorkLeft + Math.Max(0, (screen.WorkWidth - width) / 2)) / scale;
        Top = (screen.WorkTop + Math.Max(0, (screen.WorkHeight - height) / 2)) / scale;
    }

    // ============ 按钮 ============

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancelRequested) return;

        _cancelRequested = true;
        _state.CancelRequested = true;
        Refresh();
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => Choose(RecordRenderChoice.Retry);

    private void Keep_Click(object sender, RoutedEventArgs e) => Choose(RecordRenderChoice.Keep);

    private void Discard_Click(object sender, RoutedEventArgs e) => Choose(RecordRenderChoice.Discard);

    private void Choose(RecordRenderChoice choice)
    {
        if (_choice != RecordRenderChoice.None) return;

        _choice = choice;
        Refresh();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closed) return;

        // 手动关窗：停下来的态 = 保留源文件，渲染中 = 取消（上游 closeRequested 的处理）
        if (_state.Retained) Choose(RecordRenderChoice.Keep);
        else if (!_cancelRequested)
        {
            _cancelRequested = true;
            _state.CancelRequested = true;
            Refresh();
        }

        e.Cancel = true;
    }
}
