using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 实时贴图：内容交给 DWM 缩略图，我们只负责跟住源窗口、判状态、必要时退回逐帧抓图。
/// </summary>
/// <remarks>
/// 规律与踩过的坑（缩略图宿主、命中测试、哨兵矩形、句柄重建）写在 <c>docs/SCREEN_CAPTURE_PRO.md</c>，
/// 这里只保留实现。
/// </remarks>
public partial class CapturePinWindow
{
    // ============ 实时贴图 ============

    /// <summary>
    /// 实时贴图时铺在 <c>Surface</c> 上的「一丁点不透明」（alpha = 1/255，肉眼看不出来）：
    /// 分层窗口的命中测试是**按像素 alpha** 的，alpha 0 的像素会被 `WindowFromPoint` 跳过、
    /// 落到下面那层 DWM 宿主上，于是 WPF 把宿主转发回来的鼠标消息当成 spurious 丢掉
    /// （`HwndMouseInputProvider.ReportInput`）；alpha 1 就够让这块算本窗口，拖动 / 右键 / 滚轮照常。
    /// </summary>
    private static readonly Brush LiveHitSurface = CreateLiveHitSurface();

    private static Brush CreateLiveHitSurface()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 开始跟住一个窗口：内容交给 DWM 缩略图实时绘制，我们只管源窗口还在不在、贴图窗口在哪。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前每 <see cref="PinLive.IntervalMs"/> 抓一帧 <c>PrintWindow</c>，那是**有延迟的**：
    /// 抓一帧要几十毫秒、5 fps 的上限摆在那儿，滚动 / 播放 / 拖动都看得出一顿一顿（用户报过）。
    /// 现在照 Snow Shot 的做法（<c>livewindowpresentation.cpp</c>）改用
    /// <see cref="LiveThumbnail"/>：DWM 在合成阶段直接把源窗口画到贴图窗口正下方那一层，
    /// **一帧都不用我们抓**，所以源窗口怎么滚、怎么播放都是同一拍。
    /// </para>
    /// <para>
    /// 缩略图建不起来（远程桌面 / 极老驱动）才退回逐帧抓图（<see cref="_liveFallback"/>），
    /// 观感差一点，但至少还有画面。
    /// </para>
    /// <para>
    /// 跟的是**源窗口里的那一块**（<see cref="_liveRegion"/>，进实时时按贴图现在盖住的位置定下）：
    /// 源窗口怎么挪，贴图跟着挪同样的距离，画面一直是从贴图左上角铺开的同一块内容
    /// （见 <see cref="PinLive.Region"/>）。用户自己拖贴图只是换了个「挂」的位置，不改变显示哪一块。
    /// </para>
    /// </remarks>
    internal void StartLive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        _liveHwnd = hwnd;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        _livePid = pid;
        _liveClass = WindowFinder.GetClass(hwnd);
        _liveGoneTicks = 0;

        if (_live) return;                     // 已经在实时：只换目标窗口

        _live = true;
        _liveState = LiveState.Live;

        // 跟住的那一块 = 贴图现在盖着的窗口内矩形；锚点 = 现在的相对位置。
        // 之后源窗口怎么挪，贴图跟着挪同样的距离（同一块内容始终在贴图里）。
        _livePinRect = WindowRect(_hwnd);
        _livePlaced = _livePinRect;
        var source = WindowRect(_liveHwnd);
        _liveRegion = new Int32Rect(_livePinRect.X - source.X, _livePinRect.Y - source.Y,
            _livePinRect.Width, _livePinRect.Height);
        _liveAnchor = LiveAnchor.Of(_livePinRect, PinLive.Screen(source, _liveRegion));
        _liveBlock = PinLive.Screen(source, _liveRegion);

        StartLiveLayer();                      // 先试着上 DWM 缩略图（拿不到就是 null）
        _liveFallback = _thumbLayer == null;

        RefreshLiveChrome();
        StartLiveHook();

        // due = 0：退回抓帧的路线要马上抓第一帧（别在 UI 线程上等 PrintWindow）；
        // 缩略图路线这一拍只做状态检查，内容由 DWM 自己画。
        _liveTimer = new System.Threading.Timer(_ => RefreshLive(), null,
            0, PinLive.IntervalMs);
    }

    /// <summary>停止实时：留在屏幕上的是最后一帧，之后的行为与普通贴图完全一样。</summary>
    public void StopLive()
    {
        if (!_live) return;
        _live = false;

        _liveTimer?.Dispose();
        _liveTimer = null;

        StopLiveHook();
        StopLiveLayer();

        _liveFallback = false;
        _livePid = 0;
        _liveClass = "";
        _liveGoneTicks = 0;
        RefreshLiveChrome();
    }

    /// <summary>
    /// 建内容层：宿主窗口 + DWM 缩略图，贴到本窗口正下方；同时把本窗口的内容区让成透明。
    /// </summary>
    private void StartLiveLayer()
    {
        StopLiveLayer();
        if (_hwnd == IntPtr.Zero) return;
        if (_liveRegion.Width <= 0 || _liveRegion.Height <= 0) return;   // 还没定下跟住哪一块

        var crop = PinLive.Region(WindowRect(_liveHwnd), _liveRegion);

        var layer = LiveThumbnail.Attach(_liveHwnd, _hwnd,
            ToWin32(crop?.Source ?? default), ToWin32(crop?.Target ?? default));
        if (layer == null) return;             // 系统不吃缩略图：调用方退回逐帧抓图

        _thumbLayer = layer;
        layer.SetOpacity(Opacity);

        // 内容区让成逐像素透明：分层窗口里 alpha = 0 的地方鼠标会直接穿过去，
        // 落到下面那层宿主上，宿主再把消息转发回来（见 LiveThumbnail.WindowProc）。
        Shot.Visibility = Visibility.Collapsed;
        ApplyChrome();     // 实时画面就是内容：底色与 1px 描边都收起（会盖在源窗口上）

        // 透明不代表「点不到」：WPF 的命中测试要有东西可命中，否则转发回来的 WM_LBUTTONDOWN
        // 会变成「没点到任何元素」，拖动、右键菜单、滚轮一起失灵。给整块 Surface 铺一层
        // alpha = 1/255 的底（肉眼看不出来，缩略图照常透出来），让这块像素在**系统层面**也算本窗口——
        // 光画一层 alpha 0 的画笔不够：WindowFromPoint 会落到下面那层 DWM 宿主上，
        // HwndMouseInputProvider.ReportInput 的 GetCapture / WindowFromPoint 校验会把转发回来的
        // 鼠标消息当成「spurious mouse event」直接丢掉（实测拖动完全没反应）。
        Surface.Background = LiveHitSurface;
    }

    private void StopLiveLayer()
    {
        if (_thumbLayer != null)
        {
            _thumbLayer.Dispose();
            _thumbLayer = null;
        }

        Surface.Background = null;                 // 回到普通贴图：不透明底自己就是命中区

        Shot.Visibility = Visibility.Visible;
        ApplyChrome();                             // 底色 / 描边按选区外观恢复（带圆角的图仍不铺底）
    }

    /// <summary>缩略图的矩形要跟着两边窗口走：挂源窗口的位置变化（源窗口移动 / 改尺寸）。</summary>
    private void StartLiveHook()
    {
        StopLiveHook();
        if (_liveHwnd == IntPtr.Zero) return;

        _liveHookProc ??= LiveLocationChanged;
        _liveHook = Win32.SetWinEventHook(Win32.EventObjectLocationChange, Win32.EventObjectLocationChange,
            IntPtr.Zero, _liveHookProc, 0, 0, Win32.WineventOutofcontext | Win32.WineventSkipownprocess);
    }

    private void StopLiveHook()
    {
        if (_liveHook != IntPtr.Zero)
        {
            try { Win32.UnhookWinEvent(_liveHook); } catch { /* 已经没了就算了 */ }
            _liveHook = IntPtr.Zero;
        }
    }

    /// <summary>源窗口挪动 / 改尺寸：重算缩略图该显示哪一块（钩子回调在本窗口的 UI 线程上）。</summary>
    private void LiveLocationChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint thread, uint time)
    {
        // idObject 0 = 窗口本身；子控件的位置变化也会发这个事件，那些不用管
        if (hwnd != _liveHwnd || idObject != 0) return;
        SyncLiveLayer();
    }

    /// <summary>
    /// 把缩略图对齐到贴图窗口：先让贴图跟着源窗口挪（保持相对偏移），再重算该画源窗口里的哪一块。
    /// 两边任何一边动了都调它——源窗口的位置事件、贴图自己的 <c>WM_WINDOWPOSCHANGED</c>、200ms 兜底拍。
    /// </summary>
    private void SyncLiveLayer()
    {
        if (!_live) return;
        if (_hwnd == IntPtr.Zero || _liveRegion.Width <= 0 || _liveRegion.Height <= 0) return;

        var source = WindowRect(_liveHwnd);
        var pin = WindowRect(_hwnd);
        if (pin.Width <= 0 || pin.Height <= 0) return;

        // 跟住的那一块现在在屏幕上的位置。源窗口最小化（或者已经关了）时它的矩形是哨兵值
        // （(-32000,-32000)，尺寸也只剩标题栏），拿它算锚点 / 算裁剪会把贴图甩到屏幕外——
        // 用户看到的就是「最小化再打开，实时贴图没了」。这时沿用上一次的好值，还原之后再接着跟。
        bool trackable = PinLive.Trackable(source, Win32.IsIconic(_liveHwnd), DesktopRect);
        var block = trackable ? PinLive.Screen(source, _liveRegion) : _liveBlock;
        if (trackable) _liveBlock = block;

        // 用户自己拖过贴图（位置和上一次对齐的不一样，而且不是我们挪的）：这次的相对位置才是新锚点，
        // 否则下一来源窗口一动，贴图就被拽回原处——用户会以为自己拖不动它。
        if (!_liveLayingOut && !SameRect(pin, _livePlaced))
            _liveAnchor = LiveAnchor.Of(pin, block);

        // 源窗口挪了：贴图跟着挪同样的距离（最小化时不跟，它停在原地等窗口回来）
        if (trackable)
        {
            var want = _liveAnchor.Place(block, pin.Width, pin.Height);
            if (!SameRect(want, pin))
            {
                _liveLayingOut = true;
                try
                {
                    Win32.SetWindowPos(_hwnd, IntPtr.Zero, want.X, want.Y, want.Width, want.Height,
                        Win32.SwpNoactivate | Win32.SwpNozorder);
                }
                finally { _liveLayingOut = false; }

                pin = want;
            }
        }

        _livePlaced = pin;
        _livePinRect = pin;                     // 抓帧兜底（PrintWindow）按这个矩形裁

        var layer = _thumbLayer;
        if (layer == null) return;              // 退回逐帧抓图时没有内容层，跟随已经做完了

        layer.SyncGeometry();

        // 画面永远从贴图左上角铺开：拖贴图只换「挂」的位置，不改变显示源窗口的哪一块。
        // 源窗口最小化时没有可画的（整层透明，WPF 那边露出占位提示）。
        var crop = trackable ? PinLive.Region(source, _liveRegion) : null;
        layer.UpdateCrop(ToWin32(crop?.Source ?? default), ToWin32(crop?.Target ?? default),
            crop.HasValue);
    }

    private static bool SameRect(Int32Rect left, Int32Rect right) =>
        left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;

    /// <summary>抓一拍（定时器调它；冒烟也直接调，便于断言）。返回这一拍看到的状态。</summary>
    internal LiveState RefreshLive()
    {
        if (!_live) return _liveState;
        if (Interlocked.CompareExchange(ref _liveBusy, 1, 0) != 0) return _liveState;

        try
        {
            // 有些程序最小化 / 还原时会把窗口销毁再建一个：老句柄没了，但同一个程序还有窗口。
            // 认出来就悄悄改跟新窗口（同一块内容、贴图留在原地），别把实时收掉——
            // 用户报的「最小化再打开，实时贴图就自动关闭了」多半就是这一条。
            if (!Win32.IsWindow(_liveHwnd))
            {
                var successor = FindSourceWindow();
                if (successor != IntPtr.Zero)
                {
                    OnUi(() => { if (_live) RebindSource(successor); });
                    return _liveState;
                }
            }

            var state = PinLive.StateOf(Win32.IsWindow(_liveHwnd), Win32.IsIconic(_liveHwnd));

            if (state == LiveState.Closed)
            {
                // 只信「连续几拍都没了」：瞬时读不到（窗口正在重建 / 系统忙）不该把贴图收掉。
                if (Interlocked.Increment(ref _liveGoneTicks) < PinLive.GoneTicks) return LiveState.Closed;

                OnUi(() =>
                {
                    if (!_live) return;
                    StopLive();
                    Close();
                    ToastService.Show("源窗口已关闭，实时贴图收起来了", "实时贴图", 2800,
                        ToastKind.info, key: "pin-live");
                });
                return LiveState.Closed;
            }

            _liveGoneTicks = 0;

            if (state != _liveState)
                OnUi(() =>
                {
                    bool backToLive = _liveState != LiveState.Live && state == LiveState.Live;
                    _liveState = state;
                    RefreshLiveChrome();
                    // 从最小化回来时 DWM 可能已经把缩略图丢了：强制重推一遍属性，别吃缓存。
                    if (backToLive) _thumbLayer?.Refresh();
                    SyncLiveLayer();
                });
            else
                OnUi(SyncLiveLayer);           // 兜底：窗口管理器偶尔不发位置事件（Aero Snap 之类）

            // 缩略图层被系统收掉（宿主销毁 / 属性丢了）时补回来，否则贴图会一直空着。
            if (!_liveFallback && _thumbLayer is { Attached: false }) OnUi(StartLiveLayer);

            // 缩略图路线不抓帧：内容由 DWM 直接画，这里只跑状态。
            if (!_liveFallback || !PinLive.ShouldCapture(state)) return state;

            BitmapSource? frame = null;
            try { frame = LiveFrame(_liveHwnd, _livePinRect); }
            catch (Exception ex) { AppLog.Warn("CapturePro.PinLive", ex.Message); }

            if (frame != null)
                OnUi(() =>
                {
                    if (!_live) return;
                    _content = frame;
                    Shot.Source = frame;
                });

            return state;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinLive", $"实时抓帧失败：{ex.Message}");
            return _liveState;
        }
        finally
        {
            Interlocked.Exchange(ref _liveBusy, 0);
        }
    }

    /// <summary>
    /// 源窗口句柄没了以后，在**同一个程序**里找一个能接替的顶层窗口——只认「原地重建」那一种：
    /// 类名相同、盖住跟住的那块一半以上、可见、没最小化、不是工具窗口（不抢焦点的）。
    /// 找不到返回 <see cref="IntPtr.Zero"/>（调用方还有几拍宽限，之后才真的收贴图）。
    /// </summary>
    /// <remarks>
    /// 以前这里退化成「同程序里面积最大的那个」，用户随手关掉一个 Chrome 窗口时贴图会跳到
    /// 另一个完全不相干的 Chrome 窗口上，看着像「关掉的窗口还活着」。要求几何重合 + 类名相同
    /// 之后，只有真正原地重建窗口的程序（少数）会命中——那正是这条兜底要救的场景。
    /// </remarks>
    private IntPtr FindSourceWindow()
    {
        if (_livePid == 0) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != _livePid) return true;
            if (hwnd == _hwnd) return true;                      // 自己不是源窗口
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
            if (ForegroundApp.IsShellWindow(hwnd)) return true;

            // 我们自己的缩略图宿主与其它贴图都是 NOACTIVATE / 工具窗口：不能拿它们当源窗口。
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
            if ((ex & (Win32.WsExToolwindow | Win32.WsExNoactivate)) != 0) return true;

            // 同一个程序里类名不同的窗口多半是别的东西（提示框 / 悬浮面板 / 设置页）。
            if (_liveClass.Length > 0 &&
                !string.Equals(WindowFinder.GetClass(hwnd), _liveClass, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!PinLive.CanSucceed(_liveBlock, WindowRect(hwnd))) return true;

            found = hwnd;
            return false;                                        // 找到一个就够了
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 源窗口换了句柄：改跟新窗口，贴图留在原地（锚点按当前相对位置重算，不跳），
    /// 缩略图那一层跟着换源重挂。
    /// </summary>
    private void RebindSource(IntPtr hwnd)
    {
        _liveHwnd = hwnd;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        _livePid = pid;
        _liveClass = WindowFinder.GetClass(hwnd);
        _liveGoneTicks = 0;

        var source = WindowRect(hwnd);
        if (PinLive.Trackable(source, Win32.IsIconic(hwnd), DesktopRect))
        {
            _liveBlock = PinLive.Screen(source, _liveRegion);
            _liveAnchor = LiveAnchor.Of(WindowRect(_hwnd), _liveBlock);
        }

        StopLiveHook();
        StartLiveHook();

        if (_thumbLayer == null || !_thumbLayer.Attached) StartLiveLayer();
        else SyncLiveLayer();

        RefreshLiveChrome();
        AppLog.Info("CapturePro.PinLive", "源窗口换了句柄，已改跟同一程序的窗口");
    }

    /// <summary>窗口的屏幕矩形（物理像素）；拿不到时给空矩形（缩略图会自己判成「不显示」）。</summary>
    private static Int32Rect WindowRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var rect)) return default;
        return ToInt32(rect);
    }

    private static Int32Rect ToInt32(Win32.Rect rect) =>
        new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));

    /// <summary>虚拟桌面矩形（物理像素）：判「源窗口这一拍的矩形靠不靠谱」用（见 <see cref="PinLive.Trackable"/>）。</summary>
    private static Int32Rect DesktopRect => new(ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
        ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);

    private static Win32.Rect ToWin32(Int32Rect rect) =>
        new() { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height };

    /// <summary>WPF 副作用统一从这里走：已在 UI 线程就同步做（菜单 / 冒烟），否则排队（定时器线程）。</summary>
    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        Ui.Post(action);
    }

    /// <summary>
    /// 状态装饰：实时 = 右下角红点徽标；源窗口最小化 = 铺一块不透明的占位提示（不重复挂徽标）。
    /// 都不挡拖动（<c>IsHitTestVisible=False</c>），所以小窗口 / 缩略图里也照样显示。
    /// </summary>
    private void RefreshLiveChrome()
    {
        if (!_live)
        {
            LiveBadge.Visibility = Visibility.Collapsed;
            LiveCover.Visibility = Visibility.Collapsed;
            return;
        }

        // 最小化时内容层整层透明（缩略图没东西可画）：不铺一块底就会直接透到**贴图后面的窗口**上，
        // 看着像贴图坏了。占位已经把「源窗口已最小化」写清楚，右下角徽标就别重复了。
        bool minimized = _liveState == LiveState.Minimized;
        LiveCover.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;

        double width = ActualWidth > 0 ? ActualWidth : _livePinRect.Width;
        double height = ActualHeight > 0 ? ActualHeight : _livePinRect.Height;
        var textVisibility = minimized && PinLive.CoverShowsText(width, height)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LiveCoverTitle.Visibility = textVisibility;
        LiveCoverHint.Visibility = textVisibility;

        LiveBadge.Visibility = minimized ? Visibility.Collapsed : Visibility.Visible;
        LiveBadgeText.Text = PinLive.Badge(_liveState);
        LiveDot.Fill = new SolidColorBrush(_liveState == LiveState.Live
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : Color.FromRgb(0xF5, 0x9E, 0x0B));
    }
}
