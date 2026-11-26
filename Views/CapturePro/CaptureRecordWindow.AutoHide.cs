using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏工具条「贴边自动收起 / 悬停唤出」（照 Snow Shot 的
/// <c>screenrecordingtoolbarwindow.cpp</c> 里的 <c>AutoHide</c> 状态机）。
/// </summary>
/// <remarks>
/// <para>
/// 行为：工具条被拖到屏幕左 / 右边缘（间距 ≤ 16 像素）并且正在录制时，鼠标离开它
/// 200ms 后收起成一枚贴边小条；指针停到小条上 100ms（或点它）就原样展开。
/// 展开期间每 50ms 看一次指针 —— 上游同一个节奏，不能只靠 200ms 的 UI 心跳，
/// 否则「移开 → 收起」会拖到半秒以上。
/// </para>
/// <para>
/// 什么时候**不**收起（上游 <c>protectedInteraction</c>）：指针还在工具条上、
/// 在小条与工具条之间的过渡区里、正在拖动工具条、有悬停弹层 / 取色器 / 格式菜单 /
/// 我们自己的对话框开着、鼠标键按着。这一条不满足就会出现「正拖着 / 正调着色，
/// 面板忽然没了」。
/// </para>
/// <para>
/// 只有「真的在录」（<c>_recording &amp;&amp; !Finished</c>）才参与：摆着等用户点录制时
/// 面板不该自己消失。设置项是 <see cref="RecordOptions.AutoHideToolbar"/>（默认关，跟上游一致）。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    private RecordAutoHideTab? _autoHideTab;
    private DispatcherTimer? _autoHideReveal;
    private DispatcherTimer? _autoHideRetreat;
    private DispatcherTimer? _autoHideWatch;

    /// <summary>工具条现在是不是收起来了（小条在屏幕上）。</summary>
    private bool _autoHidden;

    /// <summary>算 dock 时用的那一行（物理像素）与屏幕，用来判断「主行有没有动过」。</summary>
    private Int32Rect _autoHideRow;
    private RecordToolbarSide _autoHideSide;
    private Int32Rect _autoHideTabRect;
    private Int32Rect _autoHideTransfer;
    private double _autoHideScale = 1;

    /// <summary>在算 dock 时挡住重入（Show / Hide 会同步派发消息）。</summary>
    private bool _autoHideEvaluating;

    internal bool AutoHidden => _autoHidden;

    internal RecordToolbarSide AutoHideSide => _autoHideSide;

    /// <summary>收起条窗口（冒烟用来查几何 / ex-style；没有收起来过就是 null）。</summary>
    internal RecordAutoHideTab? AutoHideTabWindow => _autoHideTab;

    /// <summary>设置里开着、真的在录、且不在收尾。</summary>
    private bool AutoHideEligible =>
        _plan.Options.AutoHideToolbar && _recording && !_controller.Finished;

    private void InitAutoHide()
    {
        _autoHideReveal = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(RecordAutoHide.RevealDelayMs),
        };
        _autoHideReveal.Tick += (_, _) =>
        {
            _autoHideReveal.Stop();
            if (_autoHidden && IsPointerInTab()) RevealAutoHide();
        };

        _autoHideRetreat = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(RecordAutoHide.RetreatDelayMs),
        };
        _autoHideRetreat.Tick += (_, _) =>
        {
            _autoHideRetreat.Stop();
            CollapseAutoHide();
        };

        _autoHideWatch = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(RecordAutoHide.WatchIntervalMs),
        };
        _autoHideWatch.Tick += (_, _) => ReconcileAutoHide();
    }

    /// <summary>
    /// 重新判一次：该不该收起 / 该不该唤出。UI 心跳（200ms）、50ms 观察计时器、
    /// 拖完 / 改完设置 / 换了区域都调这里（与上游 <c>reconcile</c> 同一套分支）。
    /// </summary>
    internal void ReconcileAutoHide()
    {
        if (_closing || _autoHideEvaluating) return;
        if (_autoHideReveal == null || _autoHideRetreat == null || _autoHideWatch == null) return;

        _autoHideEvaluating = true;
        try
        {
            bool presented = IsVisible || _autoHidden;

            if (!presented || !AutoHideEligible)
            {
                StopAutoHideTimers();
                if (_autoHidden) RevealAutoHide();
                _autoHideTransfer = default;
                return;
            }

            // 正拖着工具条：位置一直在变，既不算 Dock 也不该收起
            if (_dragging)
            {
                StopAutoHideTimers();
                return;
            }

            if (!UpdateAutoHideDock())
            {
                _autoHideRetreat.Stop();
                _autoHideWatch.Stop();
                if (_autoHidden) RevealAutoHide();
                _autoHideTransfer = default;
                return;
            }

            if (_autoHidden)
            {
                _autoHideWatch.Stop();
                if (IsPointerInTab())
                {
                    if (!_autoHideReveal.IsEnabled) _autoHideReveal.Start();
                }
                else
                {
                    _autoHideReveal.Stop();
                }

                return;
            }

            // 展开着、并且贴边：每 50ms 看一次，指针离开 200ms 后收起
            if (!_autoHideWatch.IsEnabled) _autoHideWatch.Start();

            if (AutoHideProtected())
            {
                _autoHideRetreat.Stop();
            }
            else if (!_autoHideRetreat.IsEnabled)
            {
                _autoHideRetreat.Start();
            }
        }
        finally
        {
            _autoHideEvaluating = false;
        }
    }

    /// <summary>收起来：先摆出小条、再把工具条藏掉（上游同一个顺序）。</summary>
    internal void CollapseAutoHide()
    {
        if (_closing || _autoHidden) return;
        if (!AutoHideEligible || _dragging) return;
        if (!UpdateAutoHideDock()) return;
        if (AutoHideProtected()) return;

        var rect = _autoHideTabRect;
        if (RecordAutoHide.IsEmpty(rect)) return;

        var window = EnsureAutoHideTab();

        try
        {
            window.ShowAt(rect, _autoHideSide, _autoHideScale);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "显示录屏收起条失败：" + ex.Message);
            window.HideTab();
            return;
        }

        _autoHidden = true;
        _autoHideRetreat?.Stop();
        _autoHideWatch?.Stop();
        _autoHideReveal?.Stop();

        foreach (var popover in _popovers) popover.Hide();
        _micGainPop?.Hide();
        _speakerGainPop?.Hide();
        CloseColorPicker();
        if (_formatMenu != null)
        {
            _formatMenu.IsOpen = false;
            _formatMenu = null;
        }

        Hide();
    }

    /// <summary>展开：把小条收掉、把工具条按原位置显示回来，并记下过渡区。</summary>
    internal void RevealAutoHide()
    {
        if (!_autoHidden) return;

        _autoHidden = false;
        _autoHideReveal?.Stop();
        _autoHideRetreat?.Stop();
        _autoHideWatch?.Stop();

        var tab = _autoHideTabRect;
        var side = _autoHideSide;
        _autoHideTab?.HideTab();

        if (_closing) return;

        try
        {
            Show();
            RaiseAbove();
            UpdateLayout();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "唤出录屏工具条失败：" + ex.Message);
            return;
        }

        _autoHideTransfer = UpdateAutoHideDock()
            ? RecordAutoHide.TransferRegion(_autoHideRow, tab, side)
            : default;

        ReconcileAutoHide();
    }

    /// <summary>面板关掉 / 收尾之后：小条与三个计时器一起收干净。</summary>
    private void DismissAutoHide()
    {
        _autoHidden = false;
        _autoHideTransfer = default;
        _autoHideTabRect = default;
        StopAutoHideTimers();

        var window = _autoHideTab;
        _autoHideTab = null;
        if (window == null) return;

        window.Clicked -= RevealAutoHide;
        try { window.Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关录屏收起条失败：" + ex.Message); }
    }

    private void StopAutoHideTimers()
    {
        _autoHideReveal?.Stop();
        _autoHideRetreat?.Stop();
        _autoHideWatch?.Stop();
    }

    private RecordAutoHideTab EnsureAutoHideTab()
    {
        if (_autoHideTab != null) return _autoHideTab;

        var window = new RecordAutoHideTab();
        window.Clicked += RevealAutoHide;
        _autoHideTab = window;
        return window;
    }

    private bool IsPointerInTab() =>
        Win32.GetCursorPos(out var point) && RecordAutoHide.Contains(_autoHideTabRect, point.X, point.Y);

    /// <summary>
    /// 指针还在我们身上吗（照上游 <c>protectedInteraction</c>）：工具条矩形、小条到工具条之间的
    /// 过渡区、拖动中、鼠标键按着、任何悬停弹层 / 取色器 / 格式菜单 / 我们自己的对话框开着。
    /// </summary>
    private bool AutoHideProtected()
    {
        if (!Win32.GetCursorPos(out var point)) return true;

        if (_hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var bar)
            && point.X >= bar.Left && point.X < bar.Right
            && point.Y >= bar.Top && point.Y < bar.Bottom)
        {
            _autoHideTransfer = default;
            return true;
        }

        if (RecordAutoHide.Contains(_autoHideTransfer, point.X, point.Y)) return true;
        _autoHideTransfer = default;

        if (_dragging) return true;
        if (AnyMouseButtonDown()) return true;
        if (_popovers.Any(popover => popover.IsOpen)) return true;
        if (_micGainPop?.IsOpen == true || _speakerGainPop?.IsOpen == true) return true;
        if (_pickerPopup != null) return true;
        if (_formatMenu?.IsOpen == true) return true;
        if (AnyOwnedDialogOpen()) return true;

        return false;
    }

    private static bool AnyMouseButtonDown()
    {
        // 左 / 右 / 中 / X1 / X2：按着鼠标就不收（上游 QGuiApplication::mouseButtons）
        return KeyState.IsDown(0x01) || KeyState.IsDown(0x02) || KeyState.IsDown(0x04)
            || KeyState.IsDown(0x05) || KeyState.IsDown(0x06);
    }

    /// <summary>我们自己的对话框（录屏选项 / 裁剪 / 另存）开着时别收起。</summary>
    private bool AnyOwnedDialogOpen()
    {
        var app = Application.Current;
        if (app == null) return false;

        foreach (Window window in app.Windows)
        {
            if (ReferenceEquals(window, this)) continue;
            if (window.IsVisible && ReferenceEquals(window.Owner, this)) return true;
        }

        return false;
    }

    /// <summary>
    /// 算一次主行位置 / 所在屏幕 / 贴哪边 / 小条几何。主行没动过就沿用上一次的边
    /// （上游：两边间距相等时保持上一次的选择，来回抖的时候不会左右跳）。
    /// </summary>
    private bool UpdateAutoHideDock()
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(_hwnd, out var windowRect) || windowRect.Right <= windowRect.Left) return false;

        double scale = ActualWidth > 1 ? (windowRect.Right - windowRect.Left) / ActualWidth : _autoHideScale;
        if (scale <= 0) scale = 1;

        System.Windows.Point offset = ActionBar.TranslatePoint(new System.Windows.Point(0, 0), this);
        var row = new Int32Rect(
            windowRect.Left + (int)Math.Round(offset.X * scale),
            windowRect.Top + (int)Math.Round(offset.Y * scale),
            Math.Max(1, (int)Math.Round(ActionBar.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(ActionBar.ActualHeight * scale)));

        var screen = ScreenForRow(row) ?? ScreenOf(_area);
        if (screen == null) return false;

        var bounds = new Int32Rect(screen.Left, screen.Top, screen.Width, screen.Height);

        if (row != _autoHideRow || bounds != _autoHideBounds)
        {
            _autoHideSide = RecordAutoHide.SideForRow(row, bounds, _autoHideSide);
            _autoHideRow = row;
            _autoHideBounds = bounds;
        }

        _autoHideScale = screen.Scale > 0 ? screen.Scale : scale;
        _autoHideTabRect = RecordAutoHide.TabGeometry(row, bounds, _autoHideSide, _autoHideScale);

        // 已经收起来时跟着主行改位置（上游 collapsed && side != None 时同步 control）。
        // 只有几何真的变了才重摆：每 200ms 都重摆会把悬停高亮擦掉。
        if (_autoHidden && _autoHideSide != RecordToolbarSide.None
            && !RecordAutoHide.IsEmpty(_autoHideTabRect)
            && _autoHideTab is { } tab
            && (tab.PhysicalBounds != _autoHideTabRect || tab.Side != _autoHideSide))
        {
            try { tab.ShowAt(_autoHideTabRect, _autoHideSide, _autoHideScale); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Record", "挪录屏收起条失败：" + ex.Message); }
        }

        return _autoHideSide != RecordToolbarSide.None;
    }

    private Int32Rect _autoHideBounds;

    /// <summary>主行落在哪块显示器上（相交面积最大的那块；都不相交就是 null）。</summary>
    private static ScreenGeometry? ScreenForRow(Int32Rect row)
    {
        ScreenGeometry? best = null;
        long bestArea = 0;

        foreach (var screen in Screens.All())
        {
            int left = Math.Max(row.X, screen.Left);
            int top = Math.Max(row.Y, screen.Top);
            int right = Math.Min(row.X + row.Width, screen.Left + screen.Width);
            int bottom = Math.Min(row.Y + row.Height, screen.Top + screen.Height);
            if (right <= left || bottom <= top) continue;

            long area = (long)(right - left) * (bottom - top);
            if (area > bestArea)
            {
                best = screen;
                bestArea = area;
            }
        }

        return best;
    }
}
