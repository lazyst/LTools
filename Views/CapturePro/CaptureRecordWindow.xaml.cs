using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏工具条（照 Snow Shot 的录屏面板）：两行白色圆角面板。
/// </summary>
/// <remarks>
/// <para>
/// 第一行：拖动把手 / 导出设置开关 / 绘制工具 / 撤销重做 / 录制 · 暂停 · 时长 · 麦克风 · 扬声器 /
/// 文件夹 · 关闭 · 裁剪 · 另存 · 复制。第二行（导出设置）：格式、激光笔与点击色卡、键鼠特效、
/// 跟随鼠标、摄像头、录制延迟、特效设置、齿轮。
/// </para>
/// <para>
/// **不抢焦点**（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
/// 录制时用户还在别的程序里操作。代价是收不到 <c>Esc</c>，所以每个出口都是鼠标按钮。
/// 自己**不入镜**：<c>WDA_EXCLUDEFROMCAPTURE</c>（老系统调用失败时会被录进去，但不影响录制本身）。
/// </para>
/// <para>
/// 面板能改的东西全部写回 <see cref="RecordPlan"/>：录制开始前改的（格式 / 帧率 / 延迟 /
/// 声音 / 摄像头 / 跟随）由会话在开录时读走；录制中也能改的（光标高亮 / 点击涟漪 / 拖尾 /
/// 按键浮层）由 <see cref="RecordComposer"/> 每帧问一遍 <see cref="RecordOptions"/>，所以立刻生效。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow : Window
{
    private Int32Rect _area;
    private readonly RecordPlan _plan;
    private readonly RecordController _controller;
    private readonly RecordStartGate? _gate;
    private readonly DispatcherTimer _timer;

    private RecordRegionWindow? _region;
    private IntPtr _hwnd;
    private bool _closing;
    private bool _recording;
    private bool _finished;

    private bool _dragging;
    private Win32.Point _dragCursor;
    private double _dragLeft, _dragTop;

    internal CaptureRecordWindow(Int32Rect area, RecordPlan plan, RecordController controller)
        : this(area, plan, controller, null)
    {
    }

    internal CaptureRecordWindow(Int32Rect area, RecordPlan plan, RecordController controller,
        RecordStartGate? gate)
    {
        InitializeComponent();

        _area = area;
        _plan = plan;
        _controller = controller;
        _gate = gate;

        // 没有 gate = 立刻就在录（内置功能 / 端到端 harness 走这条）；有 gate = 先摆面板等用户点
        _recording = gate == null;

        BuildActionRow();
        BuildStyleRow();
        InitAutoHide();
        InitWebcam();

        MetaText.Text = MetaSummary();
        _hasLastRecord = LastRecord() != null;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Sync();

        DpiChanged += (_, _) => PlaceBar();

        Sync();
    }

    internal RecordController Controller => _controller;

    /// <summary>面板现在是不是「正在录」（false = 摆着等用户点录制）。</summary>
    internal bool IsRecording => _recording;

    /// <summary>当前要录的区域（屏幕物理像素）：空闲时用户可以拖边框改它。</summary>
    internal Int32Rect Area => _area;

    /// <summary>
    /// 会话在用户点「录制」之后取走最终区域：照这里重新摆面板并刷新摘要（区域框这时已经关了）。
    /// </summary>
    internal void ApplyArea(Int32Rect area)
    {
        if (area == _area) return;

        _area = area;
        ResetAreaHistory();
        MetaText.Text = MetaSummary();
        PlaceBar();
        ReconcileWebcam();
    }

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceBar();
        UpdateLayout();
        PlaceBar();   // 量过尺寸之后再落一次（SizeToContent 会把它撑大）

        ShowRegion();

        // 没有等待期（内置功能 / harness 直接开录）：这时就已经在录了，历史现在就记
        if (_recording) RememberArea();

        Sync();
        ReconcileAutoHide();
        _timer.Start();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        RecordShortcutRoute.Clear();
        DismissAutoHide();
        HideWebcamWindow();
        ReleasePreviewCamera();
        CloseRegion();
        foreach (var popover in _popovers) popover.Hide();
        _micGainPop?.Hide();
        _speakerGainPop?.Hide();
        FlushGains();
        CloseColorPicker();
        CloseAnnotation();

        // 任何别的关闭方式（系统关窗 / 任务管理器）= 取消，别留下一直在录的后台线程
        _gate?.Cancel();
        if (!_closing) _controller.Cancel();
    }

    /// <summary>收尾之后由会话调用：安静地关掉，不算用户取消。</summary>
    internal void CloseQuietly()
    {
        _closing = true;
        _timer.Stop();
        DismissAutoHide();
        HideWebcamWindow();
        ReleasePreviewCamera();
        CloseRegion();
        foreach (var popover in _popovers) popover.Hide();
        _micGainPop?.Hide();
        _speakerGainPop?.Hide();
        FlushGains();
        CloseColorPicker();
        CloseAnnotation();

        try { Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关控制条失败：" + ex.Message); }
    }

    /// <summary>用户点了「录制」、倒计时也过完了：切成录制中的外观。</summary>
    /// <remarks>
    /// 由录制线程在倒计时结束后调用（<c>RecordSession.RunCore</c>），而这里要碰控件、还要截一张
    /// 冻结画面给标注层——统一转到 UI 线程执行，别在录制线程上改 WPF 对象。
    /// </remarks>
    internal void BeginRecording()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(BeginRecording);
            return;
        }

        _recording = true;
        CloseRegion();
        SuspendWebcam();
        RememberArea();
        Sync();
        ShowAnnotations();
    }

    /// <summary>把面板顶回最上面（标注层建好之后调一次，别让它盖住工具条）。</summary>
    internal void RaiseAbove()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "置顶录屏面板失败：" + ex.Message);
        }
    }

    // ============ 状态 ============

    private void Sync()
    {
        SyncUi();
        ReconcileAutoHide();
        ReconcileWebcam();

        if (!_finished && _recording && _controller.Finished)
        {
            _finished = true;
            UpdateToolButtons();      // 收尾过程中立刻把三个收尾按钮按「历史里有没有录屏」重算
            ReconcileAutoHide();      // 收尾中不再收起（eligible 掉了就唤回来）
        }

        PublishShortcuts();
    }

    // ============ 空闲时的可编辑区域框 ============

    /// <summary>
    /// 面板摆着等开录时，把「要录哪一块」画在屏幕上（Snow Shot 的空闲录屏区域窗）。
    /// </summary>
    /// <remarks>
    /// 只有走「面板先摆出来」这条入口（<see cref="_gate"/> 不为空）才建；从内置功能 / 端到端
    /// harness 直接开录时没有等待期，录制期由 <see cref="RecordBorderWindow"/> 负责。
    /// 建完把面板再顶一次，免得区域框的蓝边压在按钮上。
    /// </remarks>
    private void ShowRegion()
    {
        if (_gate == null || _region != null || _closing) return;

        try
        {
            _region = new RecordRegionWindow(_area);
            _region.AreaChanged += OnRegionChanged;
            _region.Show();
            RaiseAbove();
        }
        catch (Exception ex)
        {
            _region = null;
            AppLog.Warn("CapturePro.Record", "显示录屏区域框失败：" + ex.Message);
        }
    }

    private void OnRegionChanged(Int32Rect area)
    {
        _area = area;
        ResetAreaHistory();
        MetaText.Text = MetaSummary();
        PlaceBar();
        ReconcileWebcam();
    }

    /// <summary>收掉空闲区域框（开录 / 取消 / 关面板都要走这里，别让蓝框留在屏幕上）。</summary>
    private void CloseRegion()
    {
        var region = _region;
        _region = null;
        if (region == null) return;

        region.AreaChanged -= OnRegionChanged;
        try { region.Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关录屏区域框失败：" + ex.Message); }
    }

    // ============ 出口 ============

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording)
        {
            // 摆着等开始：这一下就是「开录」，区域到此定稿（先关框，会话随后取 Area）
            CloseRegion();
            SuspendWebcam();   // 倒计时就开始收画中画（上游 countdownActive 时预览不可见）
            _gate?.Start();
            return;
        }

        if (_controller.Finished) return;

        _controller.Stop();
        Sync();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording || _controller.Finished) return;

        _controller.TogglePause();
        Sync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseRegion();
        _gate?.Cancel();
        _controller.Cancel();
        Sync();
    }

    // ============ 界面 ============

    /// <summary>
    /// 落在录制区下方居中（Snow Shot：贴选区下沿 6px、放不下翻到上方）。
    /// 两行是同一个窗口，所以按整块的高度算，两块一起挪。
    /// </summary>
    private void PlaceBar()
    {
        var screen = ScreenOf(_area);
        if (screen == null) return;

        double scale = screen.Scale > 0 ? screen.Scale : 1;
        double width = What(ActualWidth, DesiredSize.Width);
        double height = What(ActualHeight, DesiredSize.Height);

        double areaLeft = _area.X / scale;
        double areaTop = _area.Y / scale;
        double areaWidth = _area.Width / scale;
        double areaBottom = (_area.Y + _area.Height) / scale;

        const double gap = 6;
        const double edge = 8;

        double left = Math.Clamp(areaLeft + (areaWidth - width) / 2,
            edge, Math.Max(edge, screen.WorkWidth / scale - width - edge));

        double top = areaBottom + gap;
        if (top + height > (screen.WorkTop + screen.WorkHeight) / scale - edge)
        {
            double above = areaTop - height - gap;
            if (above >= screen.WorkTop / scale + edge) top = above;
        }

        Left = left;
        Top = Math.Clamp(top, screen.WorkTop / scale + edge,
            Math.Max(screen.WorkTop / scale + edge, (screen.WorkTop + screen.WorkHeight) / scale - height - edge));

        // 位置变了：dock 判定与小条位置跟着重算（贴边收起状态由控制器自己决定去留）
        ReconcileAutoHide();
    }

    private static double What(double actual, double desired) => actual > 1 ? actual : desired;

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

    // ============ 拖动 ============

    /// <summary>
    /// 面板可以拖着走（Snow Shot 的把手）。窗口是 <c>WS_EX_NOACTIVATE</c>，用不了
    /// <see cref="Window.DragMove"/>（它要一次真实的 NCLBUTTONDOWN 循环），所以自己算位移：
    /// 光标位移用 <c>GetCursorPos</c> 的物理像素，再按窗口 DPI 换成 DIP。
    /// </summary>
    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;

        _dragging = true;
        Win32.GetCursorPos(out _dragCursor);
        _dragLeft = Left;
        _dragTop = Top;

        ReconcileAutoHide();

        ActionBar.CaptureMouse();
        e.Handled = true;
    }

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!Win32.GetCursorPos(out var cursor)) return;

        double scale = ScreenOf(_area)?.Scale ?? 1;
        if (scale <= 0) scale = 1;

        Left = _dragLeft + (cursor.X - _dragCursor.X) / scale;
        Top = _dragTop + (cursor.Y - _dragCursor.Y) / scale;
    }

    private void Bar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ActionBar.ReleaseMouseCapture();

        ReconcileAutoHide();
    }

    private void Bar_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;

        // 空白处右键 = 取消（与「Esc 退出」等价；面板收不到键盘，只好给个鼠标出口）
        e.Handled = true;
        Cancel_Click(sender, e);
    }

    private static bool IsOnControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar) return true;
            if (source is Border { Name: "ActionBar" or "StyleBar" }) return false;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
        }

        return false;
    }

    // ============ 顶层窗口 ============

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
    }
}
