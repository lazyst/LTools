using System;

namespace LTools.Native;

/// <summary>
/// 往「选区中心下那个窗口」投递一格滚轮（<c>WM_MOUSEWHEEL</c>），不动光标、不改前台。
/// </summary>
/// <remarks>
/// <para>
/// 照 snow-shot 的 <c>sendScrollingWheelStep</c>（<c>snow_shot/src/platform/windows/scrollinput.cpp</c>）：
/// ① 从 z 序最上层往下找第一个「别的进程 + 可见 + 可用 + 未最小化 + 不是全透明分层窗口 + 矩形盖住中心点」
/// 的顶层窗口；② 用 <c>ChildWindowFromPointEx</c> 一直钻到最里层子窗口（Chromium 的滚动容器就是深层子窗口）；
/// ③ <c>PostMessage</c> 把滚轮消息投过去（lParam = 屏幕坐标，wParam 高位 = delta）。
/// </para>
/// <para>
/// 为什么不模拟真实滚轮：模拟滚轮要靠**前台**窗口，而长截图面板是置顶的，一出现就抢走前台；
/// 而且得先把光标挪到页面上，鼠标停在链接 / 按钮上会触发悬停动画，拼图时整片行都对不上
/// （真机上「第一帧就认不出重叠」的一大来源）。直接投递消息两样都免了。
/// </para>
/// </remarks>
internal static class ScrollInput
{
    /// <summary>投递结果：调用方据此决定「再试一格」还是「换目标」。</summary>
    public enum Status
    {
        /// <summary>消息已经交给目标窗口。</summary>
        Posted,

        /// <summary>那个点上没有可投的窗口（桌面 / 被排除的窗口）。</summary>
        TargetNotFound,

        /// <summary>屏幕坐标换算到窗口客户区失败（窗口刚销毁）。</summary>
        CoordinateFailure,

        /// <summary>窗口在但消息没投出去。</summary>
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
    /// </summary>
    public static Status WheelStepTo(IntPtr window, int x, int y, int delta)
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

        // wParam 高位 = delta（低 16 位是按键状态，这里给 0）
        int wparam = (delta & 0xFFFF) << 16;
        // lParam = MAKELPARAM(屏幕 x, 屏幕 y)，多屏时可能是负坐标，按 32 位原样塞
        int lparam = ((y & 0xFFFF) << 16) | (x & 0xFFFF);

        bool posted = Win32.PostMessage(target, Win32.WmMousewheel,
            new IntPtr(wparam), new IntPtr(lparam));

        return posted ? Status.Posted : Status.PostFailed;
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

    /// <summary>这个顶层窗口能不能当滚动目标（见类型注释里的五条）。</summary>
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
