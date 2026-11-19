using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>收顶部之后贴图处在哪一段（照上游 <c>ScreenshotPinnedHideToTopController::State</c>）。</summary>
internal enum PinHideState
{
    /// <summary>没收顶部。</summary>
    Normal,

    /// <summary>正在往上滑并淡出（250ms InOutCubic）。</summary>
    Entering,

    /// <summary>窗口藏起来了，只剩顶边那条手柄。</summary>
    Hidden,

    /// <summary>鼠标在手柄 / 窗口上，窗口展开在手柄下方。</summary>
    Revealed,
}

/// <summary>
/// 贴图「收到屏幕顶部」：进入动画、手柄、悬停展开 / 延迟收起、屏幕变化重排（P5-i）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshotpinnedhidetotopcontroller.cpp</c>：进入时窗口滑到所在显示器工作区顶边
/// 并淡出，结束时只剩一条 30×6 DIP 的手柄；鼠标靠近手柄（命中区比手柄大一圈）立即展开到
/// 「手柄正下方」，离开手柄与窗口的并集之后 <b>100ms</b> 再收起。
/// </para>
/// <para>
/// **状态只有一个来源** <see cref="_hideState"/>：窗口的可见性、不透明度、手柄是否显示全部由它推出来；
/// 任何会改几何或换形态的操作（拖动 / 缩放 / 取景框 / 缩略图 / 穿透 / 标注 / 右键菜单）都要先
/// <see cref="ExitHideToTop"/>，与上游「任何几何变化都退出收顶部」同一口径。
/// </para>
/// <para>
/// **不激活窗口**：上游展开时会 <c>activate()</c>，LTools 的贴图本来就不抢焦点（见
/// <see cref="CapturePinWindow"/> 的类注释），这里保持不激活，记作平台差异。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    /// <summary>顶边手柄（第一次进收顶部才建；关窗时一起关掉）。</summary>
    private PinHideHandleWindow? _hideHandle;

    private PinHideState _hideState = PinHideState.Normal;

    /// <summary>手柄落在哪块屏（换屏 / 改分辨率时重排用）。</summary>
    private ScreenGeometry? _hideScreen;

    /// <summary>那块屏的工作区（物理像素）。</summary>
    private Int32Rect _hideWork;

    /// <summary>手柄可见部分（物理像素）。</summary>
    private Int32Rect _hideHandleRect;

    /// <summary>手柄命中区（物理像素，比可见部分大一圈）。</summary>
    private Int32Rect _hideHitRect;

    /// <summary>当前这块屏的缩放（物理 ÷ DIP）。</summary>
    private double _hideDpi = 1;

    /// <summary>进入动画开始时窗口的左上角（DIP，取消进入时按它还原）。</summary>
    private double _hideEntryLeft;
    private double _hideEntryTop;

    /// <summary>淡出因子：隐藏 = 0、进入中 = 1 − 进度、其余 = 1（乘在用户不透明度上）。</summary>
    private double _hideFade = 1;

    /// <summary>本张贴图用的 accent 色（第一次进入时分配，之后保持不变）。</summary>
    private int _hideAccent = -1;

    /// <summary>
    /// 「先收起来，等会儿再放回来」（隐藏全部贴图 / 换了分组）：照上游 <c>setSuppressed</c>
    /// ——**保留收顶部模式**，只是把窗口和手柄一起藏掉；放回来时手柄回到原位置。
    /// </summary>
    private bool _hideSuppressed;

    // 进入动画（16ms 一拍）+ 指针存在（55ms 轮询，延迟收起自己算）+ 屏幕变化（1s）
    private DispatcherTimer? _hideAnim;
    private readonly Stopwatch _hideAnimClock = new();
    private double _hideTargetTop;
    private DispatcherTimer? _hidePresence;
    private DispatcherTimer? _hideScreenWatch;

    /// <summary>鼠标已经离开、到点就收起（<see cref="Environment.TickCount64"/>）。</summary>
    private long _hideLeaveAt;

    /// <summary>正在收顶部（含进入动画 / 只剩手柄）。</summary>
    internal bool HideToTopActive => _hideState != PinHideState.Normal;

    /// <summary>窗口已经藏起来、只剩手柄。</summary>
    internal bool HideToTopHidden => _hideState == PinHideState.Hidden;

    /// <summary>这张贴图的手柄颜色序号（0–12）。</summary>
    internal int HideToTopAccent => _hideAccent;

    /// <summary>收顶部模式还在，只是被「隐藏全部 / 换分组」按住（上游 <c>m_suppressed</c>）。</summary>
    internal bool HideToTopSuppressed => _hideSuppressed;

    /// <summary>手柄可见部分的左边（物理像素）；没收顶部时是 <see cref="int.MinValue"/>。</summary>
    internal int HideToTopLeftPhysical =>
        HideToTopActive && !PinHideToTop.IsEmpty(_hideHandleRect) ? _hideHandleRect.X : int.MinValue;

    /// <summary>手柄可见部分的矩形（给同屏其他手柄避让用）。</summary>
    internal Int32Rect? HideToTopHandleRect =>
        HideToTopActive && !PinHideToTop.IsEmpty(_hideHandleRect) ? _hideHandleRect : null;

    /// <summary>手柄在哪块屏（同屏才互相避让）。</summary>
    internal ScreenGeometry? HideToTopScreen => _hideScreen;

    /// <summary>菜单 / 指针动作的入口：没在顶部就收起来，在顶部就退回去。</summary>
    internal void ToggleHideToTop()
    {
        if (HideToTopActive) ExitHideToTop(cancelEntry: true);
        else EnterHideToTop();
    }

    /// <summary>
    /// 收进顶部（照上游 <c>toggleHideToTop</c>）：先按顺序退出穿透 / 锁定 / 缩略图，
    /// 再按窗口中心所在的屏算手柄位置。
    /// </summary>
    internal void EnterHideToTop()
    {
        if (_live || HideToTopActive || !IsVisible) return;

        ExitOcrMode();                        // 只剩一条手柄时读不了字：先退出文字层
        if (_clickThrough) SetClickThrough(false);
        if (_locked) SetLocked(false);
        if (_thumb) SetThumbnail(false);

        double dpi = CurrentDpi();
        var frame = FramePhysical(dpi);
        var screen = PinHideToTop.MonitorAt(Screens.All(),
            frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        if (screen == null) return;

        if (!ReserveHandle(screen, frame.X, allocateAccent: true)) return;

        _hideEntryLeft = Left;
        _hideEntryTop = Top;
        _hideSuppressed = false;
        _hideFade = PinHideToTop.OpacityFactor(hidden: false, entering: true, progress: 0);
        _hideState = PinHideState.Entering;
        _hideLeaveAt = 0;

        ApplyEffectiveOpacity();
        StartHideTimers();

        _hideTargetTop = _hideWork.Y / _hideDpi;
        _hideAnimClock.Restart();
        _hideAnim?.Start();

        CapturePinManager.Sync(this);
    }

    /// <summary>
    /// 退出收顶部（照上游 <c>exit</c>）：隐藏中退出就停在「手柄下方」的展开位置；
    /// 进入动画中途取消就回原位。
    /// </summary>
    /// <param name="cancelEntry">进入动画还没结束时是不是还原到原位（菜单 / 主动点它时为真）。</param>
    internal void ExitHideToTop(bool cancelEntry = true)
    {
        if (!HideToTopActive) return;

        PinHideState previous = _hideState;
        Point target = previous == PinHideState.Hidden
            ? ShownTopLeftDip()
            : new Point(Left, Top);
        if (previous == PinHideState.Entering && cancelEntry)
            target = new Point(_hideEntryLeft, _hideEntryTop);

        _hideState = PinHideState.Normal;
        _hideSuppressed = false;
        _hideLeaveAt = 0;
        _hideAnim?.Stop();
        _hideAnimClock.Stop();
        _hideHandle?.Hide();
        StopHideTimers();

        _hideFade = 1;
        Left = target.X;
        Top = target.Y;
        ApplyEffectiveOpacity();

        Show();
        RaiseToTop();
        CapturePinManager.Sync(this);
    }

    /// <summary>
    /// 按住 / 放开收顶部（照上游 <c>setSuppressed</c>）：按住 = 窗口与手柄一起藏掉、
    /// 保留模式与手柄位置；放开 = 在原位置把手柄放回来。
    /// </summary>
    internal void SetHideToTopSuppressed(bool on)
    {
        if (!HideToTopActive || _hideSuppressed == on) return;

        if (on)
        {
            _hideSuppressed = true;
            _hideLeaveAt = 0;
            _hideAnim?.Stop();
            _hideAnimClock.Stop();
            _hideState = PinHideState.Hidden;
            _hideFade = PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0);
            ApplyEffectiveOpacity();
            Hide();
            _hideHandle?.Hide();
            StopHideTimers();
            return;
        }

        _hideSuppressed = false;
        int preferred = _hideHandleRect.X;
        var screen = _hideScreen;
        if (screen == null || !ReserveHandle(screen, preferred, allocateAccent: false))
        {
            ExitHideToTop(cancelEntry: false);
            return;
        }

        _hideState = PinHideState.Hidden;
        _hideLeaveAt = 0;
        _hideFade = PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0);
        ApplyEffectiveOpacity();
        StartHideTimers();
        ShowHideHandle();
        CapturePinManager.Sync(this);
    }

    /// <summary>藏起来时连手柄一起收掉（保留收顶部模式）；没在收顶部就照常藏窗。</summary>
    internal void SuspendHideToTop()
    {
        if (HideToTopActive)
        {
            SetHideToTopSuppressed(true);
            return;
        }

        Hide();
    }

    /// <summary>放回来：收顶部的手柄回到原位置，其余的照常显示。</summary>
    internal void ResumeHideToTop()
    {
        if (HideToTopActive)
        {
            SetHideToTopSuppressed(false);
            return;
        }

        if (!IsVisible) Show();
        RaiseToTop();
    }

    /// <summary>
    /// 贴回屏幕时按记录恢复「收顶部」（照上游 <c>prepareRestore</c> / <c>finishRestore</c>）：
    /// 不播进入动画，直接把窗口藏起来、只显示手柄。
    /// </summary>
    /// <param name="savedLeft">关掉时手柄的物理左边；<see cref="int.MinValue"/> = 没记过。</param>
    /// <param name="accent">关掉时的 accent 序号；负数 = 重新分配。</param>
    internal void RestoreHideToTop(int savedLeft, int accent)
    {
        if (_live || HideToTopActive) return;

        double dpi = CurrentDpi();
        var frame = FramePhysical(dpi);
        var screen = PinHideToTop.MonitorAt(Screens.All(),
            frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        if (screen == null) return;

        int preferred = savedLeft != int.MinValue
            ? savedLeft
            : (int)Math.Round(Left * Math.Max(0.1, screen.Scale));
        if (!ReserveHandle(screen, preferred, allocateAccent: false)) return;

        _hideAccent = accent >= 0 && accent < PinHideToTop.AccentCount
            ? accent
            : AllocateAccent();

        _hideState = PinHideState.Hidden;
        _hideSuppressed = false;
        _hideLeaveAt = 0;
        _hideFade = PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0);
        ApplyEffectiveOpacity();
        Hide();

        StartHideTimers();
        ShowHideHandle();
        CapturePinManager.Sync(this);
    }

    /// <summary>关窗时收干净：手柄是独立窗口，不关会留在屏幕上。</summary>
    private void ShutdownHideToTop()
    {
        _hideState = PinHideState.Normal;
        _hideSuppressed = false;
        _hideLeaveAt = 0;
        _hideAnim?.Stop();
        _hideAnimClock.Stop();
        StopHideTimers();

        if (_hideHandle == null) return;

        var handle = _hideHandle;
        _hideHandle = null;
        handle.Hide();
        handle.Close();
    }

    // ============ 进入动画 ============

    private void HideAnimationTick(object? sender, EventArgs e)
    {
        if (_hideState != PinHideState.Entering) return;

        double progress = Math.Clamp(
            _hideAnimClock.Elapsed.TotalMilliseconds / PinHideToTop.EnterDurationMs, 0, 1);
        double ease = PinHideToTop.EaseInOutCubic(progress);

        Top = _hideEntryTop + (_hideTargetTop - _hideEntryTop) * ease;
        _hideFade = PinHideToTop.OpacityFactor(hidden: false, entering: true, progress: ease);
        ApplyEffectiveOpacity();

        if (progress >= 1) FinishHideEntry();
    }

    private void FinishHideEntry()
    {
        _hideAnim?.Stop();
        _hideAnimClock.Stop();
        if (_hideState != PinHideState.Entering) return;

        _hideState = PinHideState.Hidden;
        _hideFade = PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0);
        ApplyEffectiveOpacity();
        Hide();
        ShowHideHandle();
        CapturePinManager.Sync(this);
    }

    // ============ 手柄 / 展开 / 收起 ============

    private void ShowHideHandle()
    {
        if (!HideToTopActive || _hideSuppressed || PinHideToTop.IsEmpty(_hideHitRect)) return;

        EnsureHideHandle();
        _hideHandle!.Place(_hideHitRect, _hideHandleRect, _hideDpi, PinHideToTop.AccentColor(_hideAccent));
        _hideHandle.Show();
        _hideHandle.RaiseToTop();
    }

    private void EnsureHideHandle()
    {
        if (_hideHandle != null) return;

        var handle = new PinHideHandleWindow();
        handle.PointerEntered += () =>
        {
            _hideLeaveAt = 0;
            RevealHideToTop();
        };
        handle.PointerLeft += () =>
        {
            if (_hideLeaveAt == 0)
                _hideLeaveAt = Environment.TickCount64 + PinHideToTop.HideDelayMs;
        };
        handle.LeftDoubleClicked += () => InvokeHandlePointerAction(PointerSettings.PinDoubleClick);
        handle.MiddleClicked += () => InvokeHandlePointerAction(PointerSettings.PinMiddleClick);
        handle.RightClicked += () =>
        {
            // 照上游 showContextMenu 的第一句：先退出收顶部，再在光标处弹菜单。
            ExitHideToTop(cancelEntry: true);
            ShowPinMenuAtCursor();
        };
        _hideHandle = handle;
    }

    /// <summary>展开到手柄正下方（照上游 <c>reveal</c>）。</summary>
    private void RevealHideToTop()
    {
        if (_hideState != PinHideState.Hidden || _hideSuppressed) return;

        Point shown = ShownTopLeftDip();
        Left = shown.X;
        Top = shown.Y;
        _hideState = PinHideState.Revealed;
        _hideFade = PinHideToTop.OpacityFactor(hidden: false, entering: false, progress: 1);
        ApplyEffectiveOpacity();

        Show();
        RaiseToTop();
        _hideHandle?.RaiseToTop();
    }

    /// <summary>收起（照上游 <c>hideWindow</c>）：窗口藏起来，手柄留在顶上。</summary>
    private void CollapseHideToTop()
    {
        if (_hideState != PinHideState.Revealed) return;

        _hideState = PinHideState.Hidden;
        _hideFade = PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0);
        ApplyEffectiveOpacity();
        Hide();
        _hideHandle?.RaiseToTop();
    }

    /// <summary>
    /// 手柄上的双击 / 中键：按设置执行（照上游 <c>eventFilter</c> 把按键转给
    /// <c>handleDoubleClick</c> / <c>handleMiddleClick</c>）。
    /// </summary>
    /// <remarks>
    /// 点击发生在手柄窗口上，走不了贴图窗口的 <see cref="MouseReleaseGate"/>（抬起永远回不到贴图），
    /// 所以「关闭 / 删除」在这里**直接做**：手柄自己就是这次点击的接收方，不存在打断同一轮输入的问题。
    /// </remarks>
    private void InvokeHandlePointerAction(PinPointerAction action)
    {
        if (action == PinPointerAction.None) return;
        if (_live && !PointerActions.AllowedInLive(action)) return;

        switch (action)
        {
            case PinPointerAction.Close:
                RequestUserClose();
                return;
            case PinPointerAction.Destroy:
                ConfirmDestroy();
                return;
            default:
                ExecutePointerAction(action);
                return;
        }
    }

    /// <summary>隐藏状态下的窗口位置：手柄左边 / 手柄底边（DIP）。</summary>
    private Point ShownTopLeftDip() => new(
        _hideHandleRect.X / _hideDpi,
        (_hideHandleRect.Y + _hideHandleRect.Height) / _hideDpi);

    // ============ 计时器 ============

    private void EnsureHideTimers()
    {
        _hideAnim ??= CreateHideTimer(DispatcherPriority.Render, HideAnimationTick);
        _hideAnim.Interval = TimeSpan.FromMilliseconds(16);

        _hidePresence ??= CreateHideTimer(DispatcherPriority.Input, HidePresenceTick);
        _hidePresence.Interval = TimeSpan.FromMilliseconds(55);

        _hideScreenWatch ??= CreateHideTimer(DispatcherPriority.Background, HideScreenTick);
        _hideScreenWatch.Interval = TimeSpan.FromSeconds(1);
    }

    private void StartHideTimers()
    {
        EnsureHideTimers();
        _hidePresence!.Start();
        _hideScreenWatch!.Start();
    }

    private void StopHideTimers()
    {
        _hidePresence?.Stop();
        _hideScreenWatch?.Stop();
    }

    private static DispatcherTimer CreateHideTimer(DispatcherPriority priority, EventHandler tick)
    {
        var timer = new DispatcherTimer(priority);
        timer.Tick += tick;
        return timer;
    }

    /// <summary>
    /// 指针存在（照上游 <c>ScreenshotPinnedPointerPresence</c>）：进入手柄 / 窗口立即展开；
    /// 离开「手柄 ∪ 窗口」之后连续 100ms 不在才收起（离开期间又回来就取消）。
    /// </summary>
    private void HidePresenceTick(object? sender, EventArgs e)
    {
        if (_hideSuppressed) return;
        if (_hideState is not (PinHideState.Hidden or PinHideState.Revealed)) return;
        if (!Win32.GetCursorPos(out var point)) return;

        bool inside = PinHideToTop.Contains(_hideHitRect, point.X, point.Y) ||
                      (_hideState == PinHideState.Revealed &&
                       PinHideToTop.Contains(FramePhysical(_hideDpi), point.X, point.Y));

        if (inside)
        {
            _hideLeaveAt = 0;
            RevealHideToTop();
            return;
        }

        if (_hideLeaveAt == 0)
        {
            _hideLeaveAt = Environment.TickCount64 + PinHideToTop.HideDelayMs;
            return;
        }

        if (Environment.TickCount64 < _hideLeaveAt) return;

        _hideLeaveAt = 0;
        CollapseHideToTop();
    }

    /// <summary>
    /// 屏幕变化（拔屏 / 改分辨率 / 缩放）后重排手柄（照上游 <c>reconcileScreen</c>）：
    /// 手柄工作区顶边跟着变，展开中的窗口重新贴到新位置。
    /// </summary>
    private void HideScreenTick(object? sender, EventArgs e)
    {
        if (!HideToTopActive || _hideSuppressed || PinHideToTop.IsEmpty(_hideHandleRect)) return;

        int x = _hideHandleRect.X + _hideHandleRect.Width / 2;
        int y = _hideHandleRect.Y + _hideHandleRect.Height / 2;
        var screen = PinHideToTop.MonitorAt(Screens.All(), x, y);
        if (screen == null) return;
        if (_hideScreen != null && screen.Equals(_hideScreen)) return;

        int preferred = _hideHandleRect.X;
        if (!ReserveHandle(screen, preferred, allocateAccent: false))
        {
            ExitHideToTop(cancelEntry: false);
            return;
        }

        if (_hideState == PinHideState.Revealed)
        {
            Point shown = ShownTopLeftDip();
            Left = shown.X;
            Top = shown.Y;
            RaiseToTop();
        }
        else if (_hideState == PinHideState.Hidden)
        {
            ShowHideHandle();
        }
        else
        {
            _hideTargetTop = _hideWork.Y / _hideDpi;
        }
    }

    // ============ 几何 ============

    /// <summary>
    /// 在手柄表里登记一次：算出这块屏上该放哪（避开同屏其他手柄），并把命中区一起记下来。
    /// </summary>
    private bool ReserveHandle(ScreenGeometry screen, int preferredLeft, bool allocateAccent)
    {
        double dpi = screen.Scale > 0 ? screen.Scale : 1;
        var work = PinHideToTop.WorkAreaOf(screen);
        var handle = PinHideToTop.PlaceHandle(work, preferredLeft, dpi, OccupiedHandles(screen));
        if (PinHideToTop.IsEmpty(handle)) return false;

        _hideScreen = screen;
        _hideDpi = dpi;
        _hideWork = work;
        _hideHandleRect = handle;
        _hideHitRect = PinHideToTop.HitGeometry(handle, work, dpi);

        if (allocateAccent && _hideAccent < 0) _hideAccent = AllocateAccent();
        return true;
    }

    /// <summary>同屏其他贴图已经占着的手柄（别的屏不参与避让）。</summary>
    private List<Int32Rect> OccupiedHandles(ScreenGeometry screen)
    {
        var occupied = new List<Int32Rect>();
        foreach (var pin in CapturePinManager.All)
        {
            if (ReferenceEquals(pin, this)) continue;
            if (pin.HideToTopSuppressed) continue;      // 被按住的那张不参与避让（上游 release()）
            if (pin.HideToTopHandleRect is not { } rect) continue;
            if (pin.HideToTopScreen is not { } other) continue;
            if (!PinHideToTop.SameScreen(screen, other)) continue;

            occupied.Add(rect);
        }

        return occupied;
    }

    /// <summary>挑一个同屏还没人用的 accent（都占了就用本张 Id 的固定色）。</summary>
    private int AllocateAccent()
    {
        var used = new HashSet<int>();
        foreach (var pin in CapturePinManager.All)
        {
            if (ReferenceEquals(pin, this) || !pin.HideToTopActive) continue;
            used.Add(pin.HideToTopAccent);
        }

        int start = PinHideToTop.AccentIndexFor(PinId);
        for (int i = 0; i < PinHideToTop.AccentCount; i++)
        {
            int index = (start + i) % PinHideToTop.AccentCount;
            if (!used.Contains(index)) return index;
        }

        return start;
    }

    /// <summary>窗口矩形（物理像素，按给定缩放）。</summary>
    private Int32Rect FramePhysical(double dpi)
    {
        if (dpi <= 0) dpi = 1;
        return new Int32Rect(
            (int)Math.Round(Left * dpi), (int)Math.Round(Top * dpi),
            Math.Max(1, (int)Math.Round(Width * dpi)),
            Math.Max(1, (int)Math.Round(Height * dpi)));
    }

    /// <summary>窗口现在所在显示器的缩放（取不到就退回截图那块屏的缩放）。</summary>
    private double CurrentDpi()
    {
        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        uint dpi = hwnd != IntPtr.Zero ? Win32.GetDpiForWindow(hwnd) : 0;
        return dpi > 0 ? dpi / 96.0 : _scale;
    }
}
