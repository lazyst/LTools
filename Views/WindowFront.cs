using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 把窗口真正提到最前：显示 / 唤起窗口一律走这里，别只写 <c>Show()</c> / <c>Activate()</c>。
/// </summary>
/// <remarks>
/// <para>
/// LTools 常驻托盘，第一次开窗时前台窗口往往还是别的程序（例如从面板点「设置」、面板本身
/// 不抢焦点）。Windows 的前台锁会把「抢焦点」压回去，窗口就开在**当前窗口后面**；
/// 而第一次 <c>Show()</c> 里的 <c>Topmost</c> 抖动又常落在布局阶段（窗口还没真显示），等于没做。
/// </para>
/// <para>
/// 这里分两步：先立刻 <c>Activate()</c>，再排到 <see cref="DispatcherPriority.Background"/>
/// 确认一次（那时窗口已经显示）；仍不行的借 <c>Topmost</c> 抬一次——**翻回原值**，
/// 像速记那种用户自己钉住的窗口不会被顺手取消——最后走
/// <see cref="Features.WindowAction.ActivateNoWait"/>（`SetForegroundWindow` + `AttachThreadInput`，不等待）。
/// </para>
/// </remarks>
internal static class WindowFront
{
    /// <summary>没开就 <c>Show()</c>，然后提到最前。</summary>
    public static void Show(Window window)
    {
        if (!window.IsVisible) window.Show();
        Bring(window);
    }

    /// <summary>把已经打开的窗口提到最前（最小化先还原）。</summary>
    public static void Bring(Window window)
    {
        if (!window.IsVisible) return;

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        Nudge(window);

        // 第一次 Show 时窗口还在布局阶段：等一帧再确认（那时 HWND 已经真的可见）。
        window.Dispatcher.BeginInvoke(new Action(() => Nudge(window)), DispatcherPriority.Background);
    }

    /// <summary>给模态窗口（<c>ShowDialog</c>）用：窗口真正显示后再提到最前。</summary>
    /// <remarks>
    /// 模态窗口在 <c>ShowDialog()</c> 里跑自己的消息循环，调用方没法在它显示之后再补一次
    /// 「提到最前」，所以挂在 <c>Loaded</c> / <c>ContentRendered</c> 上（两者都幂等）。
    /// </remarks>
    public static void Attach(Window window)
    {
        window.Loaded += (_, _) => Bring(window);
        window.ContentRendered += (_, _) => Bring(window);
    }

    private static void Nudge(Window window)
    {
        if (!window.IsVisible) return;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 别用 window.IsActive 判断：窗口被前台锁压在后面时，WPF 仍会报 IsActive=True。
        // 只认系统的前台窗口。
        if (Win32.GetForegroundWindow() == hwnd) return;

        window.Activate();

        // 前台锁：借 Topmost 把窗口抬进置顶带，再翻回原值——**视觉上一定在最前**，
        // 哪怕焦点还被锁在别的程序上。
        bool pinned = window.Topmost;
        window.Topmost = true;
        window.Topmost = pinned;

        if (Win32.GetForegroundWindow() == hwnd) return;

        // 最后再试一次真·抢焦点（SetForegroundWindow + AttachThreadInput，不等待）。
        Features.WindowAction.ActivateNoWait(hwnd);
    }
}
