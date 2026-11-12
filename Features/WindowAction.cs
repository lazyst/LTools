using System;
using System.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 「窗口操作」动作：切到前台 / 最大化 / 最小化 / 还原 / 调整到指定大小。
/// </summary>
/// <remarks>
/// <para>
/// 目标窗口按 <see cref="WindowFinder"/> 的规则匹配（标题包含 + 类名精确，命中最上面那个），
/// 与条件里的「窗口」取值器一致，因此「如果 窗口在前台」和「切换到前台」指的是同一个窗口。
/// </para>
/// <para>
/// **不假装成功**：每个操作都会读回真实状态再判断。
/// 切前台 / 最大化 / 最小化 / 还原没达到目标状态就抛错（消息里写清原因），
/// 由步骤的失败策略决定「继续下一步」还是「中止」——否则后面往错误的窗口里打字更难查。
/// 「调整大小」是唯一的例外：窗口可能被应用自己的最小尺寸夹住，这里记一条警告并
/// 把**实际矩形**作为结果返回，${变量.x/.y/.w/.h} 拿到的是窗口真实的样子。
/// </para>
/// <para>
/// 坐标系与鼠标 / 截图一致：虚拟桌面**物理像素**。目标窗口在别的显示器、别的缩放比例下也能正确落位。
/// </para>
/// </remarks>
internal static class WindowAction
{
    /// <summary>执行动作；返回实际窗口矩形（<c>${变量}</c> = <c>x,y,w,h</c>）。</summary>
    public static VarValue Run(WindowActionRequest request)
    {
        string title = (request.Title ?? "").Trim();
        string cls = (request.Class ?? "").Trim();
        if (title.Length == 0 && cls.Length == 0)
            throw new InvalidOperationException($"「{request.Name}」还没有指定目标窗口");

        IntPtr hwnd = WindowFinder.Find(title, cls, out string matched);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"找不到窗口：{WindowFinder.Describe(title, cls)}（可能已关闭，或标题变了）");

        WindowActionKind op = request.Op;
        string label = WindowFinder.GetText(hwnd) is { Length: > 0 } t ? Trim(t) : WindowFinder.GetClass(hwnd);

        switch (op)
        {
            case WindowActionKind.activate:
                if (!Activate(hwnd))
                    throw new InvalidOperationException(
                        $"没能把「{label}」切到前台：系统前台锁拒绝了这次切换（常见于另一个程序正全屏、窗口在别的虚拟桌面，或目标进程无响应）");
                break;

            case WindowActionKind.maximize:
                Win32.ShowWindow(hwnd, Win32.SwMaximize);
                if (!Settle(() => Win32.IsZoomed(hwnd)))
                    throw new InvalidOperationException($"「{label}」没有最大化（窗口可能自己禁止了最大化）");
                break;

            case WindowActionKind.minimize:
                Win32.ShowWindow(hwnd, Win32.SwMinimize);
                if (!Settle(() => Win32.IsIconic(hwnd)))
                    throw new InvalidOperationException($"「{label}」没有最小化（窗口可能自己禁止了最小化）");
                break;

            case WindowActionKind.restore:
                Win32.ShowWindow(hwnd, Win32.SwRestore);
                if (!Settle(() => !Win32.IsIconic(hwnd) && !Win32.IsZoomed(hwnd)))
                    throw new InvalidOperationException($"「{label}」没有还原");
                break;

            case WindowActionKind.resize:
                Resize(hwnd, request);
                break;
        }

        var (x, y, w, h) = ReadRect(hwnd);
        AppLog.Info("Window", $"{WindowActionLabel.Of(op)}「{label}」→ {x},{y} · {w}×{h}");
        return new VarValue
        {
            Text = $"{x},{y},{w},{h}",
            X = x,
            Y = y,
            W = w,
            H = h,
            Flag = true,
        };
    }

    // ============ 切到前台 ============

    /// <summary>
    /// 把窗口切到前台。系统有「前台锁」（只有当前前台进程 / 刚收到输入的进程才能改前台），
    /// 所以先直接试，失败后把当前前台线程与目标线程都 AttachThreadInput 到本线程再试一次。
    /// 两种都失败才算失败，绝不安慰性地返回 true。
    /// </summary>
    private static bool Activate(IntPtr hwnd)
    {
        if (Win32.GetForegroundWindow() == hwnd) return true;

        // 最小化的窗口先还原（SW_RESTORE 本身会激活，省一次前台锁博弈）。
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SwRestore);

        Win32.SetForegroundWindow(hwnd);
        if (WaitForeground(hwnd)) return true;

        IntPtr foreground = Win32.GetForegroundWindow();
        uint self = Win32.GetCurrentThreadId();
        uint fgThread = foreground != IntPtr.Zero ? Win32.GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
        uint targetThread = Win32.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

        bool attachedFg = fgThread != 0 && fgThread != self && Win32.AttachThreadInput(self, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != self && targetThread != fgThread
                              && Win32.AttachThreadInput(self, targetThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            // 一定要解开：挂着别人的输入队列会让双方都变卡。
            if (attachedTarget) Win32.AttachThreadInput(self, targetThread, false);
            if (attachedFg) Win32.AttachThreadInput(self, fgThread, false);
        }

        return WaitForeground(hwnd);
    }

    /// <summary>前台切换是异步的（目标要处理 WM_ACTIVATE），轮询等一小会。</summary>
    private static bool WaitForeground(IntPtr hwnd) =>
        Settle(() => Win32.GetForegroundWindow() == hwnd, 800);

    /// <summary>
    /// 非阻塞地把窗口切到前台（长按右键唤起面板时用：用户在哪个窗口上长按，就先激活哪个窗口，
    /// 照 Quicker——面板关闭后焦点回到那个程序，之后的动作也作用在它身上）。
    /// 唤起路径不能像 <see cref="Activate"/> 那样 Thread.Sleep 等结果，只发起、不等待。
    /// </summary>
    internal static void ActivateNoWait(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Win32.GetForegroundWindow() == hwnd) return;

        try
        {
            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SwRestore);

            IntPtr foreground = Win32.GetForegroundWindow();
            uint self = Win32.GetCurrentThreadId();
            uint fgThread = foreground != IntPtr.Zero ? Win32.GetWindowThreadProcessId(foreground, IntPtr.Zero) : 0;
            uint targetThread = Win32.GetWindowThreadProcessId(hwnd, IntPtr.Zero);

            bool attachedFg = fgThread != 0 && fgThread != self && Win32.AttachThreadInput(self, fgThread, true);
            bool attachedTarget = targetThread != 0 && targetThread != self && targetThread != fgThread
                                  && Win32.AttachThreadInput(self, targetThread, true);
            try
            {
                Win32.BringWindowToTop(hwnd);
                Win32.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attachedTarget) Win32.AttachThreadInput(self, targetThread, false);
                if (attachedFg) Win32.AttachThreadInput(self, fgThread, false);
            }
        }
        catch
        {
            // 目标进程已退出 / 拒绝激活：保持现状，上下文仍按光标下的窗口解析
        }
    }

    // ============ 调整大小 ============

    private static void Resize(IntPtr hwnd, WindowActionRequest request)
    {
        int[]? rect = request.Rect;
        if (rect == null || rect.Length < 4)
            throw new InvalidOperationException($"「{request.Name}」还没有设置目标大小");

        int x = rect[0], y = rect[1];
        int w = Math.Max(1, rect[2]), h = Math.Max(1, rect[3]);

        // 最大化 / 最小化时 SetWindowPos 改的是「还原后」的矩形，窗口状态不变；
        // 先还原再摆位。还原会激活窗口（系统行为），这里不再抢回前台。
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SwShownoactivate);
        if (Win32.IsZoomed(hwnd)) Win32.ShowWindow(hwnd, Win32.SwRestore);

        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Win32.SwpNozorder | Win32.SwpNoactivate);

        // 等窗口处理完 WM_SIZE（部分应用还会带动画 / 重新布局）。
        Thread.Sleep(250);
        var (ax, ay, aw, ah) = ReadRect(hwnd);

        const int tolerance = 8;
        if (Math.Abs(ax - x) > tolerance || Math.Abs(ay - y) > tolerance ||
            Math.Abs(aw - w) > tolerance || Math.Abs(ah - h) > tolerance)
        {
            // 被夹住是正常现象（应用有最小尺寸、或自己拒绝了这次调整）：如实记下来，不中断流程。
            AppLog.Warn("Window",
                $"「{WindowFinder.GetText(hwnd)}」没有完全按 {x},{y} · {w}×{h} 调整，实际为 {ax},{ay} · {aw}×{ah}（窗口可能限制了最小尺寸）");
        }
    }

    // ============ 小工具 ============

    private static (int X, int Y, int W, int H) ReadRect(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return (0, 0, 0, 0);
        return (r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }

    /// <summary>轮询等待条件成立（默认 900ms）；用于窗口状态 / 前台切换这类异步生效的操作。</summary>
    private static bool Settle(Func<bool> done, int timeoutMs = 900)
    {
        int waited = 0;
        while (true)
        {
            if (done()) return true;
            if (waited >= timeoutMs) return false;
            Thread.Sleep(25);
            waited += 25;
        }
    }

    private static string Trim(string text) => text.Length <= 32 ? text : text[..32] + "…";
}

/// <summary>
/// 「窗口操作」的入参。动作单元把参数求值后装成它，不再为了复用这套实现去拼一个
/// <see cref="ActionDto"/>。
/// </summary>
internal sealed record WindowActionRequest
{
    /// <summary>动作 / 步骤名（只用于报错与日志）。</summary>
    public string Name { get; init; } = "";

    /// <summary>目标窗口标题包含的文本（空 = 不按标题过滤）。</summary>
    public string? Title { get; init; }

    /// <summary>目标窗口类名（精确匹配，一般留空）。</summary>
    public string? Class { get; init; }

    /// <summary>操作（默认切到前台）。</summary>
    public WindowActionKind Op { get; init; } = WindowActionKind.activate;

    /// <summary><see cref="WindowActionKind.resize"/> 的目标矩形 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? Rect { get; init; }
}
