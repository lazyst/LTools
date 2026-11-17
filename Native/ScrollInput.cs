using System;
using LTools.Features;

namespace LTools.Native;

/// <summary>
/// 长截图的一格滚轮：优先**投消息**（照 snow-shot 的 <c>sendScrollingWheelStep</c>：不动光标、不改前台），
/// 投消息不认的窗口再用 <see cref="WheelAt"/> 发一次真滚轮兜底。
/// </summary>
/// <remarks>
/// <para>
/// 历史教训（2026-11，真实 Chromium 端到端实测）：以前长截图时覆盖层是**不透明**的全屏置顶窗，
/// 把目标窗口整个盖住——Chromium 判定自己被遮挡，**直接丢掉**送进来的滚轮（投消息、<c>WM_VSCROLL</c>、
/// 真滚轮一起失效，页面自数的滚轮事件数一动不动）。现在长截图期间覆盖层变成**分层透明壳**：
/// 选区里 alpha = 0，Windows 的命中测试直接穿透到下面的页面，页面自己就能收到真滚轮、
/// 遮挡判定也不再成立。所以「投消息」这条路重新成立，而且是首选——它不用动光标。
/// </para>
/// <para>
/// 为什么先投消息而不是真滚轮：真滚轮要先 <c>SetCursorPos</c> 把光标挪进选区，一来用户正打算点
/// 小面板上的按钮时光标会被抢走，二来 A 窗口滚完停下来时光标停在页面上会触发悬停动画
/// （拼图时整片行都对不上）。投消息两样都免了。
/// </para>
/// </remarks>
internal static class ScrollInput
{
    /// <summary>投递结果：调用方据此决定「再试一格」「换兜底」还是「停下」。</summary>
    public enum Status
    {
        /// <summary>滚轮消息已经交给目标窗口（首选路径）。</summary>
        Posted,

        /// <summary>真滚轮已经发出去（兜底路径）。</summary>
        Sent,

        /// <summary>那个点上没有可投的窗口（桌面 / 被排除的窗口 / 窗口已经关了）。</summary>
        TargetNotFound,

        /// <summary>屏幕坐标换算到窗口客户区失败（窗口刚销毁），或光标挪不过去。</summary>
        CoordinateFailure,

        /// <summary>窗口还在，但消息没投出去。</summary>
        PostFailed,

        /// <summary>delta = 0 这类空参数。</summary>
        InvalidRequest,
    }

    /// <summary>z 序遍历的安全上限：窗口在枚举途中被销毁时别原地打转。</summary>
    private const int MaxWindows = 20000;

    /// <summary>下钻子窗口的层数上限（Chromium 一般 3–6 层）。</summary>
    private const int MaxDrill = 32;

    /// <summary>
    /// 给 (<paramref name="x"/>, <paramref name="y"/>) 下的窗口投一格滚轮。
    /// <paramref name="delta"/> 用滚轮的原始单位：<c>-120</c> = 往下一格，<c>+120</c> = 往上一格。
    /// </summary>
    public static Status WheelStep(int x, int y, int delta)
    {
        if (delta == 0) return Status.InvalidRequest;

        IntPtr target = FindTarget(x, y);
        if (target == IntPtr.Zero) return Status.TargetNotFound;

        return WheelStepTo(target, x, y, delta);
    }

    /// <summary>
    /// 给**指定窗口**投一格滚轮（长截图用：目标窗口在进入模式时就定了，
    /// 不能因为光标挪到别的窗口上就把滚轮投到别处——那会和 PrintWindow 抓帧的窗口对不上）。
    /// <paramref name="horizontal"/> = true 时投 <c>WM_MOUSEHWHEEL</c>（横向滚动）：
    /// <paramref name="delta"/> 仍是「原始单位」，正数往右、负数往左（与纵向的「正数往上」同一套符号约定）。
    /// </summary>
    public static Status WheelStepTo(IntPtr window, int x, int y, int delta, bool horizontal = false)
    {
        if (delta == 0) return Status.InvalidRequest;
        if (window == IntPtr.Zero) return Status.TargetNotFound;
        if (!Win32.IsWindow(window)) return Status.TargetNotFound;

        IntPtr target = window;
        for (int i = 0; i < MaxDrill; i++)
        {
            var client = new Win32.Point { X = x, Y = y };
            if (!Win32.ScreenToClient(target, ref client)) return Status.CoordinateFailure;

            IntPtr child = Win32.ChildWindowFromPointEx(target, client,
                Win32.CwpSkipinvisible | Win32.CwpSkipdisabled | Win32.CwpSkiptransparent);
            if (child == IntPtr.Zero || child == target) break;
            target = child;
        }

        // wParam 高位 = delta（低 16 位是按键状态，这里是 0）
        int wparam = (delta & 0xFFFF) << 16;
        // lParam = MAKELPARAM(屏幕 x, 屏幕 y)，多屏时可能是负坐标，按 32 位原样塞
        int lparam = ((y & 0xFFFF) << 16) | (x & 0xFFFF);

        bool posted = Win32.PostMessage(target, (uint)(horizontal ? Win32.WmMousehwheel : Win32.WmMousewheel),
            new IntPtr(wparam), new IntPtr(lparam));

        return posted ? Status.Posted : Status.PostFailed;
    }

    /// <summary>
    /// 兜底：把光标放到 (<paramref name="x"/>, <paramref name="y"/>) 再发一格**真滚轮**。
    /// 投消息不被认的窗口（WPF 目标之类）才走这条——代价是光标会被抢走，调用方要负责用完还原。
    /// <paramref name="delta"/> 用滚轮的原始单位（<c>-120</c> = 往下一格、<c>+120</c> = 往上一格；
    /// 横向则是 <c>-120</c> = 往左、<c>+120</c> = 往右）。
    /// </summary>
    public static Status WheelAt(int x, int y, int delta, bool horizontal = false)
    {
        if (delta == 0) return Status.InvalidRequest;
        if (!Win32.SetCursorPos(x, y)) return Status.CoordinateFailure;

        // 注入事件带 MouseOps.Tag：全局鼠标钩子按它忽略（自动滚动自己发的滚轮不会又被观察一次）
        Win32.mouse_event(
            horizontal ? Win32.MouseeventfHwheel : Win32.MouseeventfWheel,
            0, 0, unchecked((uint)delta), MouseOps.Tag);

        return Status.Sent;
    }

    /// <summary>光标点下那个「会滚动」的顶层窗口；找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr FindTarget(int x, int y)
    {
        uint self = Win32.GetCurrentProcessId();
        var point = new Win32.Point { X = x, Y = y };

        IntPtr window = Win32.GetTopWindow(IntPtr.Zero);
        for (int i = 0; window != IntPtr.Zero && i < MaxWindows; i++)
        {
            if (IsEligible(window, point, self)) return window;
            window = Win32.GetWindow(window, Win32.GwHwndnext);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 这个顶层窗口能不能当滚动目标（照 snow-shot）：别的进程 + 可见 + 可用 + 没最小化 +
    /// 不是「分层 + 透明」的覆盖层 + 矩形盖住目标点。
    /// </summary>
    private static bool IsEligible(IntPtr window, Win32.Point point, uint self)
    {
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == self) return false;          // 长截图面板 / 覆盖层自己
        if (!Win32.IsWindowVisible(window)) return false;
        if (!Win32.IsWindowEnabled(window)) return false;
        if (Win32.IsIconic(window)) return false;

        long ex = Win32.GetWindowLongPtr(window, Win32.GwlExstyle).ToInt64();
        if ((ex & Win32.WsExLayered) != 0 && (ex & Win32.WsExTransparent) != 0) return false;

        if (!Win32.GetWindowRect(window, out var bounds)) return false;

        return point.X >= bounds.Left && point.X < bounds.Right &&
               point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }
}
