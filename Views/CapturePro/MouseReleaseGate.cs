using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 把 <see cref="MouseReleaseGesture"/> 接到一个 WPF 窗口上：抓鼠标捕获、吞掉抬起、
/// 排一拍再执行，并在「窗口藏了 / 关了 / 失去激活 / 捕获被抢走」时作废。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>MouseReleaseActionController</c> 的事件过滤：它盯 <c>ApplicationDeactivate</c>、
/// 范围窗口的 <c>Hide</c> / <c>Destroy</c> / <c>WindowDeactivate</c>、捕获的 <c>UngrabMouse</c>，
/// 以及 <c>MouseMove</c> 里「按钮已经不再按下」。WPF 这边对应：
/// 鼠标捕获的 <c>LostMouseCapture</c>（捕获被别人抢走）、<c>IsVisibleChanged</c>（藏了）、
/// <c>Deactivated</c>（失去激活；无激活窗口永远收不到，所以和上游一样是个空操作）、
/// <c>Closed</c>（关了）。
/// </para>
/// <para>
/// 执行点是 <see cref="DispatcherPriority.Background"/>：抬起事件自己会被吞掉，
/// 要等这一轮输入分发完再动窗口（<c>Close()</c> 在 <c>PreviewMouseUp</c> 里直接做会把
/// 同一轮的其它处理器打断）。
/// </para>
/// </remarks>
internal sealed class MouseReleaseGate
{
    private readonly Window _owner;
    private readonly MouseReleaseGesture _gesture = new();
    private bool _selfRelease;
    private bool _wasActive;

    public MouseReleaseGate(Window owner)
    {
        _owner = owner;
        owner.PreviewMouseUp += OnPreviewMouseUp;
        owner.LostMouseCapture += OnLostMouseCapture;
        owner.IsVisibleChanged += OnIsVisibleChanged;
        owner.Activated += OnActivated;
        owner.Deactivated += OnDeactivated;
        owner.Closed += OnClosed;
    }

    /// <summary>已经武装、还没执行。</summary>
    public bool Pending => _gesture.Pending;

    /// <summary>武装的是哪个键（没武装时 null；冒烟用它断言「右键按下只武装」）。</summary>
    public PointerButton? Button => _gesture.Button;

    /// <summary>
    /// 武装一次。范围窗口不可见、别的窗口正拿着鼠标捕获、或者已经有一次待执行的 → false
    /// （照上游：这时**什么都不做**，不是「按下就做」）。
    /// </summary>
    public bool Arm(PointerButton button, Action? action)
    {
        if (!_owner.IsVisible || CapturedElsewhere()) return false;
        if (!_gesture.Arm(button, action, true)) return false;

        // 抬起一定要回到这个窗口：不抓捕获的话，拖到窗外松手就没有 MouseUp，
        // 动作永远挂在半空中（上游同一句 GrabMouse）。
        if (!_owner.IsMouseCaptured && !_owner.CaptureMouse())
        {
            _gesture.Cancel();
            return false;
        }

        return true;
    }

    /// <summary>作废这一次（调用方自己要关窗 / 取消手势时用）。</summary>
    public void Cancel()
    {
        _gesture.Cancel();
        ReleaseOwnCapture();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gesture.Pending) return;
        if (ToPointer(e.ChangedButton) is not { } button || _gesture.Button != button) return;

        // 这一下抬起是这次动作的「扳机」，不让窗口再做别的事。
        e.Handled = true;

        long revision = _gesture.Release(button);
        ReleaseOwnCapture();
        if (revision == 0) return;

        _owner.Dispatcher.BeginInvoke(new Action(() => Run(revision)), DispatcherPriority.Background);
    }

    private void Run(long revision)
    {
        if (!_gesture.Take(revision, _owner.IsVisible, out Action? action) || action == null) return;
        action();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_selfRelease || !_gesture.Pending) return;
        _gesture.Cancel();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_owner.IsVisible) return;
        Cancel();
    }

    private void OnActivated(object? sender, EventArgs e) => _wasActive = true;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_gesture.Pending && _wasActive) Cancel();
        _wasActive = false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _gesture.Cancel();
        _selfRelease = true;      // 关窗时的捕获丢失不算「被抢走」
    }

    private void ReleaseOwnCapture()
    {
        if (!_owner.IsMouseCaptured) return;

        _selfRelease = true;
        try { _owner.ReleaseMouseCapture(); }
        catch (InvalidOperationException) { /* 窗口已经在关 */ }
        finally { _selfRelease = false; }
    }

    /// <summary>别的地方（其它窗口 / 别的控件）正拿着鼠标捕获 → 现在不能武装。</summary>
    private bool CapturedElsewhere()
    {
        IInputElement? captured = Mouse.Captured;
        if (captured == null) return false;
        if (ReferenceEquals(captured, _owner)) return false;

        if (captured is DependencyObject node)
        {
            Window? window = node as Window ?? Window.GetWindow(node);
            if (window != null) return !ReferenceEquals(window, _owner);
        }

        // 认不出属于哪个窗口：保守地当成「别的地方拿着」（上游同样拒绝）。
        return true;
    }

    private static PointerButton? ToPointer(MouseButton button) => button switch
    {
        MouseButton.Left => PointerButton.Left,
        MouseButton.Middle => PointerButton.Middle,
        MouseButton.Right => PointerButton.Right,
        _ => null,
    };
}
