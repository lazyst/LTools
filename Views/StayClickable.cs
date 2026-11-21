using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 让「不属于当前交互流程」的置顶小窗（提示卡片…）在模态对话框打开期间照样能点。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Window.ShowDialog"/> 会把同进程的其它窗口逐个 <c>EnableWindow(false)</c>（WPF 的模态实现），
/// 于是置顶提示卡片**看得见却收不到任何鼠标消息**：✕ / 关闭 / 查看日志 全点不动，
/// 而这时如果卡片又是「不自动关」的，用户就只能等模态窗口关掉（实测过）。
/// 这里盯着 <c>WM_ENABLE(0)</c>，被禁就排队把自己重新启用——窗口是 <c>WS_EX_NOACTIVATE</c> 的，
/// 不会因此抢焦点，也不影响模态窗口自己的阻塞语义（它的 <c>ShowDialog</c> 照旧等它关）。
/// </para>
/// <para>
/// 别在钩子里同步调 <c>EnableWindow</c>：禁用那一拍是模态窗口的初始化流程，抢在前面改容易被随后的动作覆盖。
/// </para>
/// </remarks>
internal static class StayClickable
{
    /// <summary>挂上自愈。<paramref name="paused"/> 返回 true 时不救（正在关闭 / 被截图临时收起）。</summary>
    public static void Attach(Window window, Func<bool>? paused = null)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                if (PresentationSource.FromVisual(window) is not HwndSource source) return;

                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == Win32.WmEnable && wParam == IntPtr.Zero && !handled)
                        window.Dispatcher.BeginInvoke(new Action(() => Reenable(window, paused)),
                            DispatcherPriority.Background);

                    return IntPtr.Zero;
                });
            }
            catch { /* 挂不上就退回旧行为（模态窗口关掉后才能点） */ }
        };
    }

    /// <summary>被收起（隐藏）期间可能正赶上模态窗口禁用：放回来时主动补一次。</summary>
    public static void Nudge(Window window) => Reenable(window, null);

    private static void Reenable(Window window, Func<bool>? paused)
    {
        if (paused != null && paused()) return;

        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || Win32.IsWindowEnabled(hwnd)) return;
            Win32.EnableWindow(hwnd, true);
        }
        catch { /* 正在关闭 */ }
    }
}
