using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 长截图：覆盖层**不关**，用户自己往下滚，选区里就地显示拼接结果的末尾一屏，
/// 旁边的小面板给整张长图的预览；Enter 完成、Esc 放弃。
/// </summary>
/// <remarks>
/// <para>
/// 三条「为什么这么做」：
/// ① 帧从**目标窗口**取（<see cref="ScreenShot.CaptureRegion"/> 的 <c>PrintWindow</c>），
/// 不抓屏幕——覆盖层这时盖满全屏，抓屏幕只会抓到自己；窗口被挡住也照样画得出来。
/// ② 滚轮由覆盖层**转发**给目标窗口（<see cref="ScrollInput.WheelStep"/>）：覆盖层是置顶的全屏窗口，
/// 真实的滚轮消息落在它身上，页面本身一格都不会动。
/// ③ 选区里的画面是拼接图的**末尾一屏**（1:1，底边对齐），新接的行从下面滑进来（<see cref="ScrollSlideMs"/>），
/// 所以看起来就是页面在原地滚，而不是「一张图忽然跳一下」。
/// </para>
/// <para>
/// 抓帧 + 拼接在后台线程（<see cref="ScrollStitch"/>）；只有几张小位图回 UI 线程。
/// 固定页脚（置顶 / 置底按钮条）由 <see cref="ScrollStitch"/> 丢掉中间的重复、收尾补一次到最下面。
/// 拼好的长图是成品：完成时 <see cref="ResultImage"/> = 整张图，完成动作仍是 <see cref="FinishKind.Scroll"/>。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private const int ScrollSettleMs = 45;          // 滚一格之后等页面落定再抓帧
    private const int ScrollSlideMs = 130;          // 新接的行滑进来的时长
    private const int ScrollPreviewWidth = 408;     // 小预览的采样像素上限（面板 204 DIP，按 200% 也够）
    private const int ScrollPreviewHeight = 472;

    private bool _scrollMode;
    private bool _scrollReady;
    private IntPtr _scrollTarget;
    private readonly ScrollStitch _scroll = new();
    private readonly object _scrollGate = new();
    private Rect _scrollRect;          // 选区（窗口 DIP）：1:1 画面就画在这块里
    private int _scrollView;           // 选区高度（物理像素）= 一屏
    private int _scrollWidth;
    private int _scrollHeight;
    private int _scrollShown;          // 上一次显示的拼接高度（行）
    private int _scrollStill;          // 连续「没动」的帧数（到底了）
    private int _scrollMiss;           // 连续「认不出重叠」的帧数
    private int _scrollPending;        // 已经转发出去、还没看到位移的滚轮格数
    private bool _scrollBusy;          // 后台正在抓帧 / 拼接
    private bool _scrollDirty;         // 又滚了、还没抓帧
    private bool _scrollAgain;         // 这一轮结束后再来一次
    private bool _scrollFull;
    private DispatcherTimer? _scrollTimer;

    /// <summary>长截图模式：覆盖层里的一切鼠标 / 键盘语义都要让路（见各处理器开头的早退）。</summary>
    internal bool ScrollMode => _scrollMode;

    // ============ 进出 ============

    /// <summary>
    /// 进入长截图：等用户自己滚。返回 false 时留在普通框选（原因已经用 toast 说过）。
    /// </summary>
    private bool StartScrollMode()
    {
        if (EditOnly || _scrollMode || BrowsingHistory) return false;
        if (_sel is not Rect sel || !IsValid(sel)) return false;

        if (SelectionGeometry() == null)
        {
            ToastService.Show("选区被减区挖空了，没有能拼接的内容", "长截图", 2600,
                ToastKind.warning, key: "capture-scroll-empty");
            return false;
        }

        SyncArea();
        var area = _doc.Area;
        if (area.Width < 16 || area.Height < 16)
        {
            ToastService.Show("选区太小了，长截图至少要 16 × 16 像素", "长截图", 2600,
                ToastKind.warning, key: "capture-scroll-small");
            return false;
        }

        // 目标窗口：覆盖层自己是全屏置顶，按「别的进程 + 屏幕坐标」找它下面那个顶层窗口
        int cx = area.X + area.Width / 2;
        int cy = area.Y + area.Height / 2;
        var hwnd = ScreenSnapTargets.TopWindowAtPoint(_hwnd, new System.Drawing.Point(cx, cy));
        if (hwnd == IntPtr.Zero || ForegroundApp.IsShellWindow(hwnd))
        {
            ToastService.Show("没找到能滚动的窗口：请框住页面正文再试", "长截图", 2800,
                ToastKind.warning, key: "capture-scroll-target");
            return false;
        }

        _scrollMode = true;
        _scrollReady = false;
        _scrollTarget = hwnd;
        _scrollRect = sel;
        _scrollView = area.Height;
        _scrollWidth = area.Width;
        _scrollHeight = 0;
        _scrollShown = 0;
        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollFull = false;
        _scrollDirty = false;
        _scrollAgain = false;

        Cursor = Cursors.Arrow;
        ScrollTail.Source = null;
        ScrollPreview.Source = null;
        UpdateChrome();                     // 收起框选那套，摆好拼接画面与小面板
        UpdateScrollStatus();

        _ = LoadScrollStart(area);
        return true;
    }

    /// <summary>首帧：从目标窗口取一屏，交给拼接器当起点（后台做，PrintWindow 可能几十毫秒）。</summary>
    private async Task LoadScrollStart(Int32Rect area)
    {
        var hwnd = _scrollTarget;
        int view = _scrollView;
        BitmapSource? tail = null;
        try
        {
            tail = await Task.Run(() =>
            {
                lock (_scrollGate)
                {
                    var frame = ScreenShot.CaptureRegion(hwnd, area);
                    if (frame == null) return null;

                    _scroll.Start(frame);
                    return _scroll.Tail(Math.Min(view, _scroll.Height));
                }
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.ScrollStart", ex);
        }

        if (!_scrollMode) return;
        if (tail == null)
        {
            ExitScrollMode("没抓到页面画面（这个窗口可能不允许后台截取），已回到普通截图");
            return;
        }

        _scrollReady = true;
        _scrollWidth = tail.PixelWidth;
        _scrollHeight = _scroll.Height;
        _scrollShown = _scrollHeight;

        ScrollPreview.Source = _scroll.Preview(ScrollPreviewWidth, ScrollPreviewHeight);
        ShowScrollTail(tail, 0);
        UpdateScrollStatus();
    }

    /// <summary>退出长截图，回到普通框选（原截图与选区都还在，没有丢）。</summary>
    private void ExitScrollMode(string? message = null)
    {
        _scrollTimer?.Stop();
        _scrollMode = false;
        _scrollReady = false;
        _scrollTarget = IntPtr.Zero;
        _scrollShown = 0;
        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollDirty = false;
        _scrollAgain = false;
        _scrollFull = false;

        ScrollTailShift.BeginAnimation(TranslateTransform.YProperty, null);
        ScrollTailShift.Y = 0;
        ScrollTail.Source = null;
        ScrollPreview.Source = null;
        ScrollPane.Visibility = Visibility.Collapsed;
        ScrollFrame.Visibility = Visibility.Collapsed;
        ScrollPanel.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Cross;

        UpdateChrome();

        if (message != null)
            ToastService.Show(message, "长截图", 3000, ToastKind.warning, key: "capture-scroll");
    }

    // ============ 滚轮 → 转发 → 抓帧 → 拼接 ============

    /// <summary>滚轮：只认往下滚（往上滚会把已经接好的行弄乱），转发给目标窗口后安排抓帧。</summary>
    private void Scroll_Wheel(int delta)
    {
        if (!_scrollReady) return;

        if (delta >= 0)
        {
            UpdateScrollStatus("长截图只往下拼接：继续往下滚");
            return;
        }

        int cx = _doc.Area.X + _doc.Area.Width / 2;
        int cy = _doc.Area.Y + _doc.Area.Height / 2;
        var status = ScrollInput.WheelStepTo(_scrollTarget, cx, cy, delta);
        if (status != ScrollInput.Status.Posted)
        {
            UpdateScrollStatus(status == ScrollInput.Status.TargetNotFound
                ? "这个窗口滚不动了（关了 / 最小化了）"
                : "滚轮没投递到页面：点一下页面再滚");
            return;
        }

        _scrollPending++;
        _scrollDirty = true;
        ScheduleScroll();
    }

    /// <summary>安排一次抓帧：等 <see cref="ScrollSettleMs"/> 让页面滚完；已经在等就不再重置（连续滚也能跟上）。</summary>
    private void ScheduleScroll()
    {
        if (_scrollFull) return;

        if (_scrollTimer == null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(ScrollSettleMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = ScrollStep();
            };
            _scrollTimer = timer;
        }

        if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
    }

    /// <summary>抓一帧接上去；上一轮还没结束时只记一笔，等它完了再来一轮。</summary>
    private async Task ScrollStep()
    {
        if (!_scrollMode || !_scrollReady) return;
        if (_scrollBusy)
        {
            _scrollAgain = true;
            return;
        }

        _scrollBusy = true;
        _scrollDirty = false;

        var area = _doc.Area;
        var hwnd = _scrollTarget;
        int view = _scrollView;
        ScrollFrameResult? result = null;

        try
        {
            result = await Task.Run(() => AdvanceScroll(hwnd, area, view));
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.ScrollAppend", ex);
        }
        finally
        {
            _scrollBusy = false;
        }

        if (!_scrollMode) return;

        bool again = false;
        if (result != null) again = ApplyScrollFrame(result);
        if (again || _scrollAgain || _scrollDirty)
        {
            _scrollAgain = false;
            _scrollDirty = false;
            ScheduleScroll();
        }
    }

    /// <summary>后台线程：抓这一帧、接上去、顺手做出「末尾一屏」和整张预览。</summary>
    private ScrollFrameResult AdvanceScroll(IntPtr hwnd, Int32Rect area, int view)
    {
        lock (_scrollGate)
        {
            var frame = ScreenShot.CaptureRegion(hwnd, area);
            if (frame == null) return new ScrollFrameResult(-1, 0, _scroll.Height, _scroll.Width, null, null, false);

            int before = _scroll.Height;
            int? shift = _scroll.Append(frame);
            int after = _scroll.Height;
            bool full = _scroll.Full;

            if (shift == null)
                return new ScrollFrameResult(-2, 0, after, _scroll.Width, null, null, full);
            if (shift.Value == 0)
                return new ScrollFrameResult(0, 0, after, _scroll.Width, null, null, full);
            if (after <= before)
                return new ScrollFrameResult(shift.Value, 0, after, _scroll.Width, null, null, full);

            int added = after - before;
            var tail = _scroll.Tail(Math.Min(view + added, after));
            var preview = _scroll.Preview(ScrollPreviewWidth, ScrollPreviewHeight);
            return new ScrollFrameResult(shift.Value, added, after, _scroll.Width, tail, preview, full);
        }
    }

    /// <summary>一帧的结果搬到界面上：末尾一屏滑进来、预览换掉、状态行说清楚。返回 true = 再看一帧。</summary>
    private bool ApplyScrollFrame(ScrollFrameResult result)
    {
        _scrollFull = result.Full;

        if (result.Shift == -1)
        {
            UpdateScrollStatus("抓不到这一帧（窗口可能最小化了）：再滚两下试试");
            return false;
        }

        if (result.Shift == -2)
        {
            _scrollMiss++;
            _scrollStill = 0;
            UpdateScrollStatus(_scrollMiss >= 3
                ? "认不出重叠：慢一点、一格一格滚"
                : "这一帧没认出重叠，继续往下滚");
            return _scrollMiss < 3 && _scrollPending > 0;    // 页面可能还没滚完：再看一眼
        }

        if (result.Shift == 0)
        {
            _scrollStill++;
            _scrollMiss = 0;
            UpdateScrollStatus(_scrollStill >= 3 ? "已经到底了 · Enter 完成" : "这一格还没动，再看一眼");
            return _scrollStill < 3 && _scrollPending > 0;
        }

        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollHeight = result.Height;
        _scrollShown = result.Height;
        _scrollWidth = result.Width;

        if (result.Tail != null) ShowScrollTail(result.Tail, result.Added);
        if (result.Preview != null) ScrollPreview.Source = result.Preview;
        UpdateScrollStatus();
        return false;
    }

    /// <summary>选区里的 1:1 画面：末尾一屏贴着选区底边，新接的行从下面滑进来。</summary>
    private void ShowScrollTail(BitmapSource tail, int added)
    {
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        ScrollTail.Source = tail;
        ScrollTail.Width = tail.PixelWidth / scale;
        ScrollTail.Height = tail.PixelHeight / scale;
        Canvas.SetLeft(ScrollTail, 0);
        Canvas.SetTop(ScrollTail, Math.Max(0, _scrollRect.Height - ScrollTail.Height));

        ScrollTailShift.BeginAnimation(TranslateTransform.YProperty, null);

        double offset = added > 0 ? added / scale : 0;
        if (offset <= 0)
        {
            ScrollTailShift.Y = 0;
            return;
        }

        ScrollTailShift.Y = offset;
        var slide = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(ScrollSlideMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ScrollTailShift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>状态行：拼到多大 + 怎么操作；<paramref name="note"/> 是这一拍要额外说的一句。</summary>
    private void UpdateScrollStatus(string? note = null)
    {
        string text = _scrollHeight > 0
            ? $"{_scrollWidth} × {_scrollHeight} px · {_scrollHeight / (double)Math.Max(1, _scrollView):0.#} 屏"
            : "正在读取页面…";

        text += "\n滚轮往下滚 · Enter 完成 · Esc 放弃";
        if (_scrollFull) text += "\n已到长度上限";
        if (!string.IsNullOrEmpty(note)) text += "\n" + note;
        ScrollStatus.Text = text;
    }

    // ============ 完成 / 放弃 ============

    private void ScrollDone_Click(object sender, RoutedEventArgs e) => FinishScroll();

    private void ScrollCancel_Click(object sender, RoutedEventArgs e) => CancelScroll();

    /// <summary>完成：拼好的长图当成品交回（<see cref="CaptureSession"/> 负责复制 + 提示）。</summary>
    private void FinishScroll()
    {
        if (!_scrollMode) return;

        if (!_scrollReady || _scrollHeight <= 0)
        {
            ToastService.Show("还没读到页面画面，稍等一下再完成", "长截图", 2400,
                ToastKind.warning, key: "capture-scroll-wait");
            return;
        }

        BitmapSource image;
        try
        {
            lock (_scrollGate)
            {
                _scroll.Finish();       // 底部固定条补一次到最下面
                image = _scroll.Render();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.ScrollRender", ex);
            TrayService.Notify("长截图导出失败：" + ex.Message);
            return;
        }

        _doc.Area = new Int32Rect(_doc.Area.X, _doc.Area.Y, image.PixelWidth, image.PixelHeight);

        ExitScrollMode();
        ScrollRequested = true;
        ResultImage = image;
        Finish = FinishKind.Scroll;
        _confirmed = true;
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }
    }

    /// <summary>放弃：回到普通框选（原截图、选区、标注都还在）。</summary>
    private void CancelScroll() => ExitScrollMode("已放弃这次长截图，回到框选");

    // ============ 界面（长截图模式的窗口层元素） ============

    /// <summary>长截图模式的界面：只留选区 + 1:1 拼接画面 + 小面板 + 顶部提示。</summary>
    private void ApplyScrollChrome()
    {
        Rect sel = _scrollRect;

        Surface.Selection = sel;
        Surface.SelectionGeometry = SelectionGeometry();
        Surface.PendingPolygon = null;
        Surface.Hover = Rect.Empty;
        Surface.ShowHandles = false;
        Surface.InvalidateVisual();

        ShapeBar.Visibility = Visibility.Collapsed;
        InfoBox.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        TextEntry.Visibility = Visibility.Collapsed;
        HideDetect();
        HidePicker();
        PositionGrips(sel, false);

        // 1:1 的拼接画面正好盖在选区里（Surface 的压暗只在选区外面，所以看不出来）
        Canvas.SetLeft(ScrollPane, sel.X);
        Canvas.SetTop(ScrollPane, sel.Y);
        ScrollPane.Width = Math.Max(1, sel.Width);
        ScrollPane.Height = Math.Max(1, sel.Height);
        ScrollPane.Visibility = Visibility.Visible;

        // 蓝色描边：告诉用户「这一块是在长截图里」
        Canvas.SetLeft(ScrollFrame, sel.X - 1);
        Canvas.SetTop(ScrollFrame, sel.Y - 1);
        ScrollFrame.Width = Math.Max(1, sel.Width + 2);
        ScrollFrame.Height = Math.Max(1, sel.Height + 2);
        ScrollFrame.Visibility = Visibility.Visible;

        // 先让面板可见再量尺寸：Collapsed 的元素 DesiredSize 是 0，量出来会把面板摆到左上角
        ScrollPanel.Visibility = Visibility.Visible;
        PlaceScrollPanel(sel);
        HintText.Text = "长截图 · 滚轮往下滚页面，自动接成一张长图 · Enter 完成 · Esc 放弃";
        PlaceHint();
    }

    /// <summary>小面板：优选出选区外面（<see cref="ScrollLayout.PlaceIn"/>），实在没地方才压在选区上。</summary>
    private void PlaceScrollPanel(Rect sel)
    {
        ScrollPanel.InvalidateMeasure();
        ScrollPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Max(1, ScrollPanel.DesiredSize.Width);
        double h = Math.Max(1, ScrollPanel.DesiredSize.Height);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var at = ScrollLayout.PlaceIn(sel, new Size(w, h), new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(ScrollPanel, Math.Clamp(at.X, 8, Math.Max(8, ActualWidth - w - 8)));
        Canvas.SetTop(ScrollPanel, Math.Clamp(at.Y, 8, Math.Max(8, ActualHeight - h - 8)));
    }

    /// <summary>事件源是不是来自小面板（它的按钮要自己收点击，别当成「点页面」）。</summary>
    private bool IsOnScrollPanel(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ScrollPanel)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>窗口关掉时把计时器停掉（正常出口都会先 <see cref="ExitScrollMode"/>，这里是兜底）。</summary>
    private void Window_Closed(object sender, EventArgs e)
    {
        _scrollTimer?.Stop();
        _scrollMode = false;
        _scrollReady = false;
    }

    /// <summary>一帧的处理结果（<see cref="Shift"/>：&gt;0 = 接了多少行，0 = 没动，-1 = 抓不到，-2 = 认不出）。</summary>
    private sealed record ScrollFrameResult(int Shift, int Added, int Height, int Width,
        BitmapSource? Tail, BitmapSource? Preview, bool Full);
}
