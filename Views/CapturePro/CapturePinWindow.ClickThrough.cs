using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的「点击穿透」与「抖动隐藏同组其他贴图」。
/// </summary>
/// <remarks>
/// <para>
/// <b>点击穿透</b>照上游 <c>setClickThroughMode</c>：贴图加上 <c>WS_EX_TRANSPARENT</c>，
/// 鼠标事件全部穿到下面的窗口去；同时把不透明度换成**穿透不透明度**（默认 50%），
/// 旁边摆三个独立控件（不透明度滑条 / 移动 / 退出）——它们才是这期间唯一能点到的东西，
/// 否则用户会被一张点不到的图困住。
/// </para>
/// <para>
/// <b>抖动</b>照上游 <c>beginWindowShake</c> / <c>sampleWindowShake</c>：拖动贴图时左右快速来回甩六下，
/// 把同组其他贴图藏起来；再甩一次显示回来。识别状态机在 <see cref="PinShakeDetector"/>。
/// </para>
/// <para>
/// 两条规则都只作用于**普通贴图**：实时贴图（跟着源窗口刷新）不参加穿透与抖动，
/// 它的尺寸 / 位置跟源窗口绑死，藏起来也没意义。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    /// <summary>点击穿透时那块控制栏（第一次进穿透才建）。</summary>
    private PinClickThroughBar? _ctBar;

    /// <summary>现在是不是穿透态。</summary>
    private bool _clickThrough;

    /// <summary>用户自己调的不透明度（0–100，滚轮 / 菜单改）。</summary>
    private int _opacityPercent = 100;

    /// <summary>穿透态的不透明度（0–100，默认 50，滑条改）。</summary>
    private int _clickThroughOpacity = PinClickThrough.DefaultOpacity;

    // 抖动：位置是「拖动起点起的累计位移」（DIP），时间用 Stopwatch，全部由鼠标事件驱动。
    private readonly PinShakeDetector _shake = new();
    private readonly Stopwatch _shakeClock = new();
    private Point _shakePointer;
    private Vector _shakeOffset;
    private bool _shakeActive;

    // 穿透态下用移动按钮拖动：起点光标 + 起点窗口位置（DIP）。
    private Point _ctDragStart;
    private Point _ctMoveBase;
    private bool _ctLockedHint;

    /// <summary>现在是不是点击穿透态（鼠标点不到贴图本身）。</summary>
    public bool ClickThrough => _clickThrough;

    /// <summary>穿透态的不透明度（0–100）。</summary>
    public int ClickThroughOpacity => _clickThroughOpacity;

    /// <summary>用户自己调的不透明度（0–100）。</summary>
    public int OpacityPercent => _opacityPercent;

    // ============ 点击穿透 ============

    /// <summary>进 / 出点击穿透。进的时候先收拾掉缩略图、拖动与菜单。</summary>
    public void SetClickThrough(bool on)
    {
        if (on == _clickThrough) return;

        if (on)
        {
            if (_thumb) SetThumbnail(false);          // 缩略图与穿透互斥（照上游 setThumbnailMode）
            CancelGesture();
            _menu?.IsOpen = false;
            _menu = null;

            _clickThrough = true;
            ApplyEffectiveOpacity();
            SetTransparent(true);

            if (!EnsureClickThroughBar() || !SyncClickThroughGeometry())
            {
                // 摆不下就退回去：没有退出按钮的穿透态等于把用户困住。
                SetClickThrough(false);
                ToastService.Show("当前屏幕放不下穿透控件，已取消点击穿透", "贴图", 3200, ToastKind.warning,
                    key: "pin-click-through");
                return;
            }

            RefreshHoverChrome();
            ApplyCursor();
            CapturePinManager.Sync(this);
            return;
        }

        _clickThrough = false;
        _shake.Reset();
        _shakeActive = false;
        SetTransparent(false);
        ApplyEffectiveOpacity();

        if (_ctBar != null)
        {
            _ctBar.ExitClicked -= OnClickThroughExit;
            _ctBar.OpacityChanged -= OnClickThroughOpacityChanged;
            _ctBar.Hide();
            _ctBar.Close();
            _ctBar = null;
        }

        RefreshHoverChrome();
        ApplyCursor();
        RaiseToTop();
        CapturePinManager.Sync(this);
    }

    /// <summary>改穿透不透明度（滑条 / 恢复记录调）。</summary>
    public void SetClickThroughOpacity(int percent)
    {
        int next = PinClickThrough.NormalizeOpacity(percent);
        if (next == _clickThroughOpacity) return;

        _clickThroughOpacity = next;
        _ctBar?.SetOpacity(next);
        ApplyEffectiveOpacity();
        CapturePinManager.Sync(this);
    }

    /// <summary>改用户不透明度（滚轮 / 右键菜单调；不影响穿透态那一个）。</summary>
    public void SetOpacityPercent(int percent)
    {
        int next = Math.Clamp(percent, (int)Math.Round(PinMath.OpacityMin * 100), 100);
        if (next == _opacityPercent) return;

        _opacityPercent = next;
        ApplyEffectiveOpacity();
    }

    /// <summary>按当前形态落到窗口不透明度上：穿透态用穿透值，否则用用户自己那个。</summary>
    private void ApplyEffectiveOpacity()
    {
        double value = PinClickThrough.EffectiveOpacity(_clickThrough, _opacityPercent, _clickThroughOpacity);
        Opacity = value;
        _thumbLayer?.SetOpacity(value);
    }

    /// <summary>给窗口加 / 去掉 <c>WS_EX_TRANSPARENT</c>（鼠标穿透）。</summary>
    private void SetTransparent(bool on)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
        ex = on
            ? ex | Win32.WsExTransparent | Win32.WsExNoactivate
            : ex & ~Win32.WsExTransparent;
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, (IntPtr)ex);
    }

    /// <summary>建（或重建）控制栏并接上事件；贴图先关窗，控制栏跟着一起走。</summary>
    private bool EnsureClickThroughBar()
    {
        if (_ctBar != null) return true;

        try
        {
            var bar = new PinClickThroughBar { Owner = this };
            bar.SetLocked(_locked);
            bar.SetOpacity(_clickThroughOpacity);
            bar.OpacityChanged += OnClickThroughOpacityChanged;
            bar.ExitClicked += OnClickThroughExit;
            bar.MoveStarted += OnClickThroughMoveStart;
            bar.MoveMoved += OnClickThroughMove;
            bar.MoveEnded += OnClickThroughMoveEnd;
            bar.Show();
            _ctBar = bar;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinClickThrough", "创建穿透控件失败：" + ex.Message);
            _ctBar = null;
            return false;
        }
    }

    /// <summary>
    /// 按「贴图内容矩形 + 所在显示器工作区」重摆三个控件；摆不下就退出穿透。
    /// </summary>
    /// <returns>控件栏在位返回 true；退出穿透 / 还没建栏返回 false。</returns>
    private bool SyncClickThroughGeometry()
    {
        if (!_clickThrough || _ctBar == null) return false;

        var content = new Rect(Left, Top, Math.Max(1, ActualWidth > 0 ? ActualWidth : Width),
            Math.Max(1, ActualHeight > 0 ? ActualHeight : Height));
        var layout = PinClickThrough.Layout(content, WindowSizing.WorkAreaOf(this));

        if (layout.IsEmpty)
        {
            SetClickThrough(false);
            return false;
        }

        _ctBar.SetTopmost(Topmost);
        _ctBar.Place(layout.Origin);
        return true;
    }

    /// <summary>关窗时收拾干净（控制栏是独立窗口，不关会留在屏幕上）。</summary>
    private void ShutdownClickThrough()
    {
        _shake.Reset();
        _shakeActive = false;
        _shakeClock.Stop();

        if (_ctBar != null)
        {
            _ctBar.ExitClicked -= OnClickThroughExit;
            _ctBar.OpacityChanged -= OnClickThroughOpacityChanged;
            var bar = _ctBar;
            _ctBar = null;
            bar.Hide();
            bar.Close();
        }

        _clickThrough = false;
    }

    private void OnClickThroughOpacityChanged(int percent) => SetClickThroughOpacity(percent);

    private void OnClickThroughExit() => SetClickThrough(false);

    private void OnClickThroughMoveStart(Point cursor)
    {
        _ctLockedHint = false;
        if (_locked)
        {
            if (!_ctLockedHint)
            {
                _ctLockedHint = true;
                ToastService.Show("贴图位置已锁定，先解锁才能移动", "贴图", 2400, ToastKind.warning,
                    key: "pin-locked");
            }

            return;
        }

        _ctDragStart = cursor;
        _ctMoveBase = new Point(Left, Top);
        RaiseToTop();
    }

    private void OnClickThroughMove(Point cursor)
    {
        if (_locked) return;

        Left = _ctMoveBase.X + (cursor.X - _ctDragStart.X);
        Top = _ctMoveBase.Y + (cursor.Y - _ctDragStart.Y);
    }

    private void OnClickThroughMoveEnd()
    {
        _ctLockedHint = false;
        CapturePinManager.Sync(this);
    }

    // ============ 抖动隐藏同组其他贴图 ============

    /// <summary>开始一段拖动时调（非平移手势）。条件不满足就不武装。</summary>
    private void BeginShake(Point cursor)
    {
        _shake.Reset();
        _shakeActive = false;

        if (_live || _locked || _clickThrough) return;
        if (!IsVisible || !PinToScreenStore.ShakeWindowToHideOthers) return;

        _shakeActive = true;
        _shakePointer = cursor;
        _shakeOffset = default;
        _shake.Begin(0, 0, 0);
        _shakeClock.Restart();
    }

    /// <summary>拖动中每次鼠标移动调；识别到抖动就切换一次同组其他贴图的可见性。</summary>
    private void SampleShake(Point cursor)
    {
        if (!_shakeActive) return;

        _shakeOffset += cursor - _shakePointer;
        _shakePointer = cursor;

        if (!_shake.Sample(_shakeOffset.X, _shakeOffset.Y, _shakeClock.ElapsedMilliseconds)) return;

        _shakeActive = false;
        _shakeClock.Stop();
        ToggleOthersForShake();
    }

    /// <summary>拖动结束（<paramref name="cancel"/> = 半路中断）时收手。</summary>
    private void EndShake(bool cancel)
    {
        _shakeActive = false;
        _shake.Reset();
        _shakeClock.Stop();
        if (cancel) _shakeOffset = default;
    }

    /// <summary>抖动命中的那一下：同组其他贴图「都藏着的」就全放出来，否则全藏起来。</summary>
    private void ToggleOthersForShake()
    {
        if (!PinToScreenStore.ShakeWindowToHideOthers) return;

        var result = CapturePinManager.ToggleOthersForShake(this);
        if (!result.Changed) return;

        ToastService.Show(result.Showed ? "已显示其他贴图" : "已隐藏其他贴图", "贴图", 3000,
            ToastKind.success, key: "pin-shake");
    }

    /// <summary>打断正在进行的拖动 / 多选（进穿透前调）。</summary>
    private void CancelGesture()
    {
        EndShake(true);
        if (!_dragging && !_panning && !_maybeSelect) return;

        _dragging = false;
        _panning = false;
        _maybeSelect = false;
        CapturePinManager.EndMove();
        try { ReleaseMouseCapture(); } catch { /* 没捕获 */ }
    }
}
