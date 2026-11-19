using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 长截图：覆盖层变成**透明壳**（只留小面板 + 细描边），页面自己收滚轮，用户怎么滚就怎么拼。
/// 自动滚动时由程序替用户滚；Enter 完成、Esc 放弃。
/// </summary>
/// <remarks>
/// <para>
/// 五条「为什么这么做」（照 snow-shot 的长截图，2026-11 真实 Chromium 端到端实测定稿）：
/// ① **覆盖层不能盖住目标窗口**。Chromium 一判定自己被整个盖住就把送进来的滚轮全丢掉
/// （投消息 / <c>WM_VSCROLL</c> / 真滚轮一起失效，页面自数的滚轮事件数一动不动）——这是
/// 「长截图拼不出 / 效果不理想」的根。所以长截图期间 <see cref="Root"/> 变透明、冻结画面收起：
/// 选区里 alpha = 0，Windows 的命中测试直接穿透到下面的页面（snow-shot 的
/// <c>scrollinput.cpp</c> 同一句注释：「Layered transparent overlays pass mouse input through」）。
/// ② **滚轮归页面**，覆盖层不转发、不吞。滚轮落在目标窗口里就由 <see cref="MouseHook"/> 顺手
/// **观察**一份（<see cref="ScrollCaptureHookState"/>），只用来触发抓帧；页面真滚到哪儿由
/// 拼接器自己算（运动估计），观察到的滚轮格数只是个节奏参考。
/// ③ 帧从**目标窗口**取（<see cref="ScreenShot.CaptureRegion"/> 的 <c>PrintWindow</c>），不抓屏幕。
/// ④ **自动滚动**照 snow-shot 的 <c>ScreenshotScrollingAutoScroller</c> + <c>AdaptiveScrollingCaptureCadence</c>：
/// 投一格 → 等落定 → 抓帧 → 拼接 → 按实测成本 + 退避算下一格等多久。投递先走 snow-shot 的
/// 「直接投 <c>WM_MOUSEWHEEL</c>」（<see cref="ScrollInput.WheelStepTo"/>，不动光标），
/// 窗口不认（WPF 目标）再退到真滚轮（<see cref="ScrollInput.WheelAt"/>）。
/// ⑤ 抓帧 + 拼接在后台线程（<see cref="ScrollStitch"/>）；只有一张小预览回 UI 线程。
/// 末尾固定条（置顶 / 置底按钮条）由 <see cref="ScrollStitch"/> 丢掉中间的重复、收尾补一次到最末尾。
/// 拼好的长图是成品：完成时 <see cref="ResultImage"/> = 整张图，完成动作仍是 <see cref="FinishKind.Scroll"/>。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private const int ScrollSettleMs = 120;         // 滚一格之后等页面落定再抓帧（浏览器是平滑滚动，45ms 太急）
    private const int ScrollSettleMaxMs = 420;      // 用户一直不停地滚：最多推这么久就强行收一帧（安全轮询）
    private const int ScrollPreviewWidth = 408;     // 小预览的采样像素上限（面板 204 DIP，按 200% 也够）
    private const int ScrollPreviewHeight = 472;
    private const int ScrollAutoDelta = 120;        // 自动滚动一次投几格（一个 WHEEL_DELTA）
    private const int ScrollAutoMaxStill = 3;       // 连续几帧没动 = 到底
    private const int ScrollAutoMaxMiss = 4;        // 自动滚动连续几帧认不出重叠 = 放弃（页面不配合）

    private bool _scrollMode;
    private bool _scrollReady;
    private IntPtr _scrollTarget;
    private ScrollStitch _scroll = new();
    private readonly object _scrollGate = new();
    private Rect _scrollRect;          // 选区（窗口 DIP）：小面板围着它摆
    private int _scrollView;           // 沿轴的一屏长度（物理像素）
    private int _scrollWidth;          // 拼接图的宽（物理像素）
    private int _scrollHeight;         // 拼接图的高（物理像素）
    private int _scrollShown;          // 上一次拿到的拼接长度（状态行「几屏」用）
    private int _scrollStill;          // 连续「没动」的帧数
    private int _scrollMiss;           // 连续「认不出重叠」的帧数
    private int _scrollPending;        // 观察到、还没抓到的滚轮格数
    private bool _scrollBusy;          // 后台正在抓帧 / 拼接
    private bool _scrollDirty;         // 又滚了、还没抓帧
    private bool _scrollAgain;         // 这一轮结束后再来一次
    private bool _scrollFull;
    private StitchAxis _scrollAxis = StitchAxis.Vertical;
    private bool _scrollAuto;          // 自动滚动开着
    private bool _scrollRealWheel;     // 投消息不认 → 改成真滚轮（会把光标挪进选区）
    private int _scrollAutoInterval = ScreenshotBehavior.DefaultScrollingAutoScrollIntervalMs;
    private readonly ScrollCadence _scrollCadence = new();
    private readonly Stopwatch _scrollAutoClock = new();   // 距上一次投滚轮过了多久（节奏按「两次投递之间」算）
    private int _scrollGen;             // 拼接代次：换方向 / 重开时 +1，旧的异步结果据此作废
    private long _scrollDirtyTick;      // 连续滚动时「第一次还没抓的滚」的时刻（安全轮询用）
    private DispatcherTimer? _scrollTimer;      // 等页面落定 → 抓帧
    private DispatcherTimer? _scrollAutoTimer;  // 自动滚动的下一格
    private int _scrollFinishTries;             // 完成时等「路上那一帧」落地试了几次

    // 透明壳：进去时的窗口外观与光标，退出来原样还回去
    private Brush? _scrollSavedBackground;
    private Visibility _scrollSavedSurface;
    private Cursor? _scrollSavedCursor;
    private Win32.Point _scrollSavedCursorPoint;
    private bool _scrollCursorMoved;

    /// <summary>目标窗口的内存位图抓不到时用的「窗口矩形」（观察滚轮的范围）。</summary>
    private Int32Rect ScrollTargetRect()
    {
        if (_scrollTarget != IntPtr.Zero && Win32.GetWindowRect(_scrollTarget, out var r) &&
            r.Right > r.Left && r.Bottom > r.Top)
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

        return _doc.Area;
    }

    // ============ 进出 ============

    /// <summary>
    /// 进入长截图：等用户自己滚（或打开自动滚动）。返回 false 时留在普通框选（原因已经用 toast 说过）。
    /// </summary>
    private bool StartScrollMode()
    {
        if (EditOnly || _scrollMode || BrowsingHistory) return false;

        // 预选态直接进长截图：先把这块预选落成普通选区（智能选区暂停）
        EndSmartPreviewKeepingSelection();

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
        _scrollView = _scrollAxis == StitchAxis.Vertical ? area.Height : area.Width;
        _scrollWidth = area.Width;
        _scrollHeight = 0;
        _scrollShown = 0;
        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollFull = false;
        _scrollDirty = false;
        _scrollAgain = false;
        _scrollDirtyTick = 0;
        _scrollAuto = false;
        _scrollRealWheel = false;
        _scrollCursorMoved = false;
        _scrollFinishTries = 0;
        _scrollAutoInterval = ScreenshotBehavior.ScrollingAutoScrollIntervalMs;
        _scrollCadence.Reset();
        StopAutoTimer();
        _scrollGen++;
        lock (_scrollGate) _scroll = new ScrollStitch(_scrollAxis);

        EnterScrollShell();
        ScrollPreview.Source = null;
        UpdateScrollAutoChrome();
        UpdateChrome();                     // 收起框选那套，摆好小面板
        UpdateScrollStatus();

        _ = LoadScrollStart(area);
        return true;
    }

    // ============ 透明壳 ============

    /// <summary>
    /// 进长截图：覆盖层不再画冻结画面与压暗，只留小面板 + 细描边 —— 页面自己收滚轮才滚得动。
    /// 同时把要观察的滚轮范围（目标窗口矩形）与三个键交给钩子。
    /// </summary>
    private void EnterScrollShell()
    {
        _scrollSavedBackground = Root.Background;
        _scrollSavedSurface = Surface.Visibility;
        _scrollSavedCursor = Cursor;
        _scrollSavedCursorPoint = default;
        if (Win32.GetCursorPos(out var point)) _scrollSavedCursorPoint = point;

        Root.Background = Brushes.Transparent;
        Surface.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;

        var watch = ScrollTargetRect();
        ScrollCaptureHookState.Start(
            watch.X, watch.Y, watch.X + watch.Width, watch.Y + watch.Height,
            OnScrollWheelObserved, OnScrollKey);

        FocusScrollTarget();
    }

    /// <summary>退出长截图：还回覆盖层原来的样子（冻结画面、压暗、光标），并让钩子恢复原状。</summary>
    private void ExitScrollShell()
    {
        ScrollCaptureHookState.Stop();
        RestoreScrollCursor();

        Root.Background = _scrollSavedBackground ?? Brushes.Transparent;
        Surface.Visibility = _scrollSavedSurface;
        Cursor = _scrollSavedCursor ?? Cursors.Cross;
        _scrollSavedBackground = null;
        _scrollSavedCursor = null;
    }

    /// <summary>
    /// 把目标窗口抬到前台：真滚轮要么按「光标下的窗口」路由、要么按「前台窗口」路由，
    /// 两条路都要落到页面上（另外用户点过小面板之后前台会变成覆盖层，得顶回去）。
    /// </summary>
    private void FocusScrollTarget()
    {
        if (_scrollTarget == IntPtr.Zero) return;
        if (Win32.GetForegroundWindow() == _scrollTarget) return;
        WindowAction.ActivateNoWait(_scrollTarget);
    }

    /// <summary>兜底真滚轮会把光标挪进选区：用完还回用户原来停的地方。</summary>
    private void RestoreScrollCursor()
    {
        if (!_scrollCursorMoved) return;
        _scrollCursorMoved = false;
        if (_scrollSavedCursorPoint.X != 0 || _scrollSavedCursorPoint.Y != 0)
            Win32.SetCursorPos(_scrollSavedCursorPoint.X, _scrollSavedCursorPoint.Y);
    }

    // ============ 钩子回调（UI 线程） ============

    /// <summary>
    /// 钩子观察到一格**真滚轮**落在目标窗口里（页面自己已经滚了）→ 安排抓帧。
    /// 往反方向滚不丢：拼接器认得「往回滚」，成品不会变长。
    /// </summary>
    private void OnScrollWheelObserved()
    {
        if (!_scrollMode) return;

        if (_scrollAuto)
        {
            UpdateScrollStatus("自动滚动中：按空格停下就能自己滚");
            return;
        }

        _scrollPending++;
        _scrollDirty = true;
        ScheduleScroll(restarted: true);
    }

    /// <summary>钩子吞下来的三个键：Enter 完成 / Esc 放弃 / 空格开关自动滚动。</summary>
    private void OnScrollKey(ScrollCaptureHookState.Key key)
    {
        if (!_scrollMode) return;

        switch (key)
        {
            case ScrollCaptureHookState.Key.Finish: FinishScroll(); break;
            case ScrollCaptureHookState.Key.Cancel: CancelScroll(); break;
            case ScrollCaptureHookState.Key.AutoScroll: ToggleAutoScroll(); break;
        }
    }

    /// <summary>首帧：从目标窗口取一屏，交给拼接器当起点（后台做，PrintWindow 可能几十毫秒）。</summary>
    private async Task LoadScrollStart(Int32Rect area)
    {
        var hwnd = _scrollTarget;
        int view = _scrollView;
        int gen = _scrollGen;
        BitmapSource? preview = null;
        try
        {
            preview = await Task.Run(() =>
            {
                // 抓帧在锁外（PrintWindow 可能几十毫秒，别把拼接器锁住），落进拼接器时再确认方向没换过
                var frame = ScreenShot.CaptureRegion(hwnd, area);
                if (frame == null) return null;

                lock (_scrollGate)
                {
                    if (gen != _scrollGen) return null;
                    _scroll.Start(frame);
                    return _scroll.Preview(ScrollPreviewWidth, ScrollPreviewHeight);
                }
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.ScrollStart", ex);
        }

        // 换方向 / 退出之后这一帧作废（否则会把旧方向的图贴到新拼接上）
        if (!_scrollMode || gen != _scrollGen) return;

        if (preview == null)
        {
            ExitScrollMode("没抓到页面画面（这个窗口可能不允许后台截取），已回到普通截图");
            return;
        }

        _scrollReady = true;
        _scrollWidth = _scroll.OutputWidth;
        _scrollHeight = _scroll.OutputHeight;
        _scrollShown = _scroll.Extent;

        ScrollPreview.Source = preview;
        UpdateScrollStatus();
        UpdateScrollAutoChrome();

        // 读首帧期间用户已经滚过了：这一帧是「滚动之前」的，赶紧再抓一帧接上
        if (_scrollDirty) ScheduleScroll();

        // 换方向前自动滚动开着：读完首帧接着滚，别让用户再按一次空格
        if (_scrollAuto) AutoScrollKick();
    }

    /// <summary>
    /// 重开一次拼接（换方向时用）：从当前画面重新开始，已经拼到的内容作废。
    /// 自动滚动开着的话，读完首帧接着自动滚（用户不用再按一次空格）。
    /// </summary>
    private void RestartScroll()
    {
        if (!_scrollMode) return;

        StopAutoTimer();
        _scrollReady = false;
        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollDirty = false;
        _scrollAgain = false;
        _scrollDirtyTick = 0;
        _scrollFull = false;
        _scrollCadence.Reset();
        _scrollAutoClock.Reset();
        _scrollWidth = 0;
        _scrollHeight = 0;
        _scrollShown = 0;
        ScrollPreview.Source = null;

        _scrollGen++;                       // 旧的抓帧结果作废
        lock (_scrollGate) _scroll = new ScrollStitch(_scrollAxis);

        var area = _doc.Area;
        _scrollView = _scrollAxis == StitchAxis.Vertical ? area.Height : area.Width;

        UpdateScrollAutoChrome();
        UpdateScrollStatus("已重新开始拼接");
        _ = LoadScrollStart(area);
    }

    /// <summary>退出长截图，回到普通框选（原截图与选区都还在，没有丢）。</summary>
    private void ExitScrollMode(string? message = null)
    {
        StopAutoTimer();
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
        _scrollDirtyTick = 0;
        _scrollFull = false;
        _scrollAuto = false;
        _scrollRealWheel = false;
        _scrollFinishTries = 0;

        _scrollGen++;                       // 还在飞的那一帧作废
        lock (_scrollGate) _scroll = new ScrollStitch(_scrollAxis);

        ExitScrollShell();
        ScrollPreview.Source = null;
        ScrollFrame.Visibility = Visibility.Collapsed;
        ScrollPanel.Visibility = Visibility.Collapsed;

        UpdateChrome();

        if (message != null)
            ToastService.Show(message, "长截图", 3000, ToastKind.warning, key: "capture-scroll");
    }

    // ============ 滚轮 → 抓帧 → 拼接 ============

    /// <summary>
    /// 自动滚动投一格滚轮给目标窗口。返回 false = 没投出去（状态行已经说明原因）。
    /// <paramref name="delta"/> 是纵向滚轮的原始值（负数 = 往下）；横向时换算成往右。
    /// </summary>
    private bool PostScrollStep(int delta)
    {
        int cx = _doc.Area.X + _doc.Area.Width / 2;
        int cy = _doc.Area.Y + _doc.Area.Height / 2;

        bool horizontal = _scrollAxis == StitchAxis.Horizontal;
        // 横向：往右 = +120（WM_MOUSEHWHEEL 的正方向）；纵向：往下 = -120（WM_MOUSEWHEEL 的负方向）
        int step = horizontal ? Math.Abs(delta) : -Math.Abs(delta);

        if (!_scrollRealWheel)
        {
            var posted = ScrollInput.WheelStepTo(_scrollTarget, cx, cy, step, horizontal);
            if (posted == ScrollInput.Status.Posted) return true;

            if (posted == ScrollInput.Status.TargetNotFound)
            {
                UpdateScrollStatus("这个窗口滚不动了（关了 / 最小化了）");
                return false;
            }

            // 窗口不认投递的滚轮（WPF 目标）：改成真滚轮，光标会挪进选区
            _scrollRealWheel = true;
            UpdateScrollStatus("这个窗口不认投递的滚轮：改用真滚轮");
        }

        var status = ScrollInput.WheelAt(cx, cy, step, horizontal);
        if (status == ScrollInput.Status.Sent)
        {
            _scrollCursorMoved = true;
            return true;
        }

        UpdateScrollStatus(status == ScrollInput.Status.CoordinateFailure
            ? "光标挪不进选区：先点一下页面再试"
            : "这一格滚轮发不出去");
        return false;
    }

    /// <summary>
    /// 安排一次抓帧（等页面落定）。<paramref name="restarted"/> = 用户又滚了一格：
    /// 把「落定」往后推（连滚时不会每格抓一次），但最多推 <see cref="ScrollSettleMaxMs"/>。
    /// </summary>
    private void ScheduleScroll(bool restarted = false)
    {
        if (_scrollFull) return;

        if (_scrollTimer == null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _scrollDirtyTick = 0;
                _ = ScrollStep();
            };
            _scrollTimer = timer;
        }

        long now = Environment.TickCount64;

        if (restarted)
        {
            if (_scrollDirtyTick == 0) _scrollDirtyTick = now;
            if (now - _scrollDirtyTick < ScrollSettleMaxMs)
            {
                _scrollTimer.Stop();
                _scrollTimer.Interval = TimeSpan.FromMilliseconds(ScrollSettleMs);
                _scrollTimer.Start();
                return;
            }
        }

        if (_scrollTimer.IsEnabled) return;

        _scrollTimer.Interval = TimeSpan.FromMilliseconds(ScrollSettleMs);
        _scrollTimer.Start();
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
        int gen = _scrollGen;
        var stitch = _scroll;
        ScrollFrameResult? result = null;

        try
        {
            result = await Task.Run(() => AdvanceScroll(stitch, hwnd, area, view));
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.ScrollAppend", ex);
        }
        finally
        {
            _scrollBusy = false;
        }

        // 这一帧抓的时候已经换了方向 / 退出了模式：丢掉，别接进新的拼接图
        if (!_scrollMode || gen != _scrollGen) return;

        bool again = false;
        if (result != null) again = ApplyScrollFrame(result);
        if (again || _scrollAgain || _scrollDirty)
        {
            _scrollAgain = false;
            _scrollDirty = false;
            ScheduleScroll();
            return;
        }

        if (_scrollAuto) ArmAutoScroll();
    }

    /// <summary>后台线程：抓这一帧、接上去、顺手做出整张预览，并量出两段耗时。</summary>
    private ScrollFrameResult AdvanceScroll(ScrollStitch stitch, IntPtr hwnd, Int32Rect area, int view)
    {
        _ = view;
        var clock = Stopwatch.StartNew();
        var frame = ScreenShot.CaptureRegion(hwnd, area);
        double captureMs = clock.Elapsed.TotalMilliseconds;

        // 抓帧在锁外（PrintWindow 可能几十毫秒），拼接才进锁——换方向时不用等一次抓帧
        lock (_scrollGate)
        {
            if (frame == null)
                return new ScrollFrameResult(ScrollFrameStatus.Failed, 0, stitch.Extent, 0, null, false, captureMs, 0);

            clock.Restart();
            int before = stitch.Extent;
            int? shift = stitch.Append(frame);
            int after = stitch.Extent;
            bool full = stitch.Full;
            int width = stitch.OutputWidth;
            int height = stitch.OutputHeight;
            double stitchMs = clock.Elapsed.TotalMilliseconds;

            if (shift == null)
                return new ScrollFrameResult(ScrollFrameStatus.Unrecognized, 0, after, width, null, full, captureMs, stitchMs);
            if (shift.Value == 0)
                return new ScrollFrameResult(ScrollFrameStatus.Still, 0, after, width, null, full, captureMs, stitchMs);
            if (after <= before)
                return new ScrollFrameResult(ScrollFrameStatus.Contained, shift.Value, after, width, null, full, captureMs, stitchMs);

            var preview = stitch.Preview(ScrollPreviewWidth, ScrollPreviewHeight);
            return new ScrollFrameResult(ScrollFrameStatus.Accepted, shift.Value, after, width,
                preview, full, captureMs, stitchMs, height);
        }
    }

    /// <summary>一帧的结果搬到界面上：预览换掉、状态行说清楚。返回 true = 再看一帧。</summary>
    private bool ApplyScrollFrame(ScrollFrameResult result)
    {
        _scrollFull = result.Full;
        if (result.CaptureMs > 0) _scrollCadence.RecordCapture(result.CaptureMs);
        if (result.StitchMs > 0) _scrollCadence.RecordStitch(result.StitchMs);

        if (result.Status == ScrollFrameStatus.Failed)
        {
            _scrollCadence.NoteProgress(false);
            UpdateScrollStatus("抓不到这一帧（窗口可能最小化了）：再滚两下试试");
            if (_scrollAuto) StopAutoScroll("抓不到画面，自动滚动已停");
            return false;
        }

        if (result.Status == ScrollFrameStatus.Unrecognized)
        {
            _scrollMiss++;
            _scrollStill = 0;
            _scrollCadence.NoteProgress(false);

            if (_scrollAuto)
            {
                // 自动滚动：这一格没接上就交给节奏重投一格（退避后更慢），别原地连抓同一帧。
                // 连抓也接不上就说明页面真的不配合（惯性滚动 / 吃滚轮的自绘滚动条），停下让用户自己来。
                if (_scrollMiss >= ScrollAutoMaxMiss)
                {
                    StopAutoScroll("认不出重叠，自动滚动已停 · 可以自己滚");
                    return false;
                }

                UpdateScrollStatus("这一帧没认出重叠：放慢一点再试");
                return false;
            }

            UpdateScrollStatus(_scrollMiss >= 3
                ? "认不出重叠：慢一点、一格一格滚"
                : "这一帧没认出重叠，继续往下滚");
            return _scrollMiss < ScrollAutoMaxMiss && _scrollPending > 0;
        }

        if (result.Status == ScrollFrameStatus.Still)
        {
            _scrollStill++;
            _scrollMiss = 0;
            _scrollCadence.NoteProgress(false);

            // 投消息不认的窗口（WPF 目标）：先换成真滚轮再判「到底」
            if (_scrollAuto && !_scrollRealWheel && _scrollStill >= 2)
            {
                _scrollRealWheel = true;
                UpdateScrollStatus("这个窗口不认投递的滚轮：改用真滚轮（光标会挪进选区）");
                return false;
            }

            if (_scrollStill < ScrollAutoMaxStill)
            {
                UpdateScrollStatus("这一格还没动：页面可能还在滚");
                if (_scrollAuto) return false;      // 交给节奏重投一格（带退避）
                return _scrollPending > 0;          // 浏览器是平滑滚动，再看一帧
            }

            // 连续几帧都没动：到底了。不退出模式——用户还能往回滚 / 按 Enter 完成
            UpdateScrollStatus("看起来已经到底了 · Enter 完成");
            if (_scrollAuto) StopAutoScroll("到底了，自动滚动已停");
            return false;
        }

        // Contained（往回滚，内容已经在画布里）与 Accepted（接上新内容）都算「滚动有效」
        _scrollStill = 0;
        _scrollMiss = 0;
        _scrollPending = 0;
        _scrollCadence.NoteProgress(true);
        _scrollHeight = result.Height > 0 ? result.Height : _scrollHeight;
        _scrollWidth = result.Width > 0 ? result.Width : _scrollWidth;
        _scrollShown = result.Extent;

        if (result.Preview != null) ScrollPreview.Source = result.Preview;
        UpdateScrollStatus();

        if (_scrollFull && _scrollAuto) StopAutoScroll("已到长度上限，自动滚动已停");
        return false;
    }

    // ============ 完成 / 放弃 ============

    /// <summary>长截图完成之后去哪儿：直接复制走 / 交给图片编辑 / 交给切图。</summary>
    internal enum ScrollExit
    {
        Copy,
        Edit,
        Split,
    }

    /// <summary>这次长截图完成后要做什么（<see cref="FinishKind.Scroll"/> 的出口才知道）。</summary>
    internal ScrollExit ScrollAfter { get; private set; } = ScrollExit.Copy;

    private void ScrollDone_Click(object sender, RoutedEventArgs e) => FinishScroll(ScrollExit.Copy);

    private void ScrollEdit_Click(object sender, RoutedEventArgs e) => FinishScroll(ScrollExit.Edit);

    private void ScrollSplit_Click(object sender, RoutedEventArgs e) => FinishScroll(ScrollExit.Split);

    private void ScrollCancel_Click(object sender, RoutedEventArgs e) => CancelScroll();

    /// <summary>
    /// 完成：拼好的长图当成品交回（<see cref="CaptureSession"/> 负责复制 + 提示）。
    /// 「编辑 / 切图」同样先收尾拼图，只是交回之后由会话把图交给对应的窗口。
    /// </summary>
    private void FinishScroll(ScrollExit exit = ScrollExit.Copy)
    {
        if (!_scrollMode) return;

        if (!_scrollReady || _scrollHeight <= 0)
        {
            ToastService.Show("还没读到页面画面，稍等一下再完成", "长截图", 2400,
                ToastKind.warning, key: "capture-scroll-wait");
            return;
        }

        StopAutoTimer();
        _scrollTimer?.Stop();

        // 用户很可能「刚滚完 / 还在滚」就按 Enter：这一屏还在路上（后台正在抓、或者落定计时还没到），
        // 直接渲染就会把最后不到一屏的新内容整段丢掉（实测末格只滚 22 像素，成品正好少 22 行）。
        // 先等路上的那一帧落地，再自己抓一帧与当前画面对齐（没动就是 Still，幂等），最后才渲染。
        if (_scrollBusy && _scrollFinishTries++ < 8)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FinishScroll(exit);
            };
            timer.Start();
            return;
        }

        _scrollFinishTries = 0;

        BitmapSource image;
        try
        {
            lock (_scrollGate)
            {
                // 与后台那条完全同一套抓帧 + 拼接：滚轮之后画面变了就补上，没变就什么也不做
                var last = AdvanceScroll(_scroll, _scrollTarget, _doc.Area, _scrollView);
                if (last.Extent > _scrollHeight) _scrollHeight = last.Extent;

                _scroll.Finish();       // 收尾（固定条已经算在 Extent 里，重复调用无害）
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
        ScrollAfter = exit;
        ScrollRequested = exit == ScrollExit.Copy;
        ResultImage = image;
        Finish = FinishKind.Scroll;
        _confirmed = true;
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }
    }

    /// <summary>放弃：回到普通框选（原截图、选区、标注都还在）。</summary>
    private void CancelScroll() => ExitScrollMode("已放弃这次长截图，回到框选");

    /// <summary>窗口关掉时把计时器与钩子状态停掉（正常出口都会先 <see cref="ExitScrollMode"/>，这里是兜底）。</summary>
    private void Window_Closed(object sender, EventArgs e)
    {
        // 退出那一下（右键 / Esc / Enter）的抬起别漏给下面的程序
        ExitInput.Swallow();

        StopAutoTimer();
        _scrollTimer?.Stop();
        StopSmartTicker();
        ScrollCaptureHookState.Stop();
        _scrollMode = false;
        _scrollReady = false;
        _scrollAuto = false;

        SelectionStore.Flush();     // 选区外观是防抖写的，别把最后一下丢在缓存里
    }

    /// <summary>一帧的去向（<see cref="ScrollFrameStatus"/>）：抓不到 / 认不出 / 没动 / 往回滚 / 接上了。</summary>
    private enum ScrollFrameStatus
    {
        /// <summary>这一帧没抓到（窗口关了 / 最小化了）。</summary>
        Failed,

        /// <summary>和上一帧找不到重叠（页面不配合 / 惯性滚动还没落定）。</summary>
        Unrecognized,

        /// <summary>画面没动（到底了 / 滚轮没打到目标窗口）。</summary>
        Still,

        /// <summary>往回滚了：内容已经在画布里，成品不变长。</summary>
        Contained,

        /// <summary>接上了新内容，成品变长。</summary>
        Accepted,
    }

    /// <summary>
    /// 一帧的处理结果。<see cref="Shift"/> 是运动估计给出的带符号位移（负数 = 往下滚 / 往右滚），
    /// 只用于诊断；界面只认 <see cref="Status"/>。
    /// <see cref="CaptureMs"/> / <see cref="StitchMs"/> 给自动滚动算节奏用。
    /// </summary>
    private sealed record ScrollFrameResult(ScrollFrameStatus Status, int Shift, int Extent, int Width,
        BitmapSource? Preview, bool Full,
        double CaptureMs = 0, double StitchMs = 0, int Height = 0);
}
