using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LTools.Native;

/// <summary>
/// 唤起面板瞬间的目标窗口信息（进程 / 标题 / 类名），供上下文场景匹配使用。
/// </summary>
/// <remarks>
/// 必须在创建 / 激活面板窗口**之前**抓取：面板一显示，前台就变成 LTools 自己。
/// 句柄有两种来源：键盘唤起用 <see cref="Win32.GetForegroundWindow"/>；长按右键唤起用
/// <see cref="Win32.TopLevelWindowAtCursor"/>（右键不激活后台窗口，前台可能还停在别的程序上）。
/// 权限受限的进程（不同位数 / 受保护进程）取不到 <see cref="ExePath"/> 时退化为
/// 进程名与窗口条件匹配，不抛异常。
/// </remarks>
internal sealed record ForegroundApp(
    IntPtr Hwnd,
    string ExePath,
    string ExeName,
    string Title,
    string ClassName)
{
    /// <summary>抓取当前前台窗口；失败返回 null。</summary>
    public static ForegroundApp? Capture()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : FromHandle(hwnd);
    }

    /// <summary>句柄是否属于 LTools 自己（光标下正好是自己的浮层时，上下文仍取前台程序）。</summary>
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 是否是 LTools 自己的**不可激活浮层**（提示卡片 / 录制指示器这类
    /// <c>WS_EX_NOACTIVATE</c> 或 <c>WS_EX_TOOLWINDOW</c> 窗口）。
    /// 光标恰好压在浮层上长按右键时，上下文应仍取浮层背后的程序；
    /// 而设置窗口这类普通窗口照常算 LTools。
    /// </summary>
    public static bool IsOwnOverlay(IntPtr hwnd)
    {
        if (!IsOwnWindow(hwnd)) return false;
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle);
        return (ex & (Win32.WsExNoactivate | Win32.WsExToolwindow)) != 0;
    }

    /// <summary>
    /// 桌面 / 任务栏这类 shell 窗口。长按右键取上下文时可以按它匹配场景（如桌面、任务栏），
    /// 但不该把它「激活成前台」——系统也不会把任务栏当普通窗口激活。
    /// </summary>
    public static bool IsShellWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        return ReadClassName(hwnd) is
            "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow";
    }

    /// <summary>
    /// 从已经记录的窗口句柄补全进程 / 标题 / 类名。
    /// 拆成两步是为了让钩子回调只做 <c>GetForegroundWindow</c>（微秒级），
    /// 进程查询放到 UI 线程（<c>MainModule</c> 对个别进程可能较慢，不能阻塞钩子）。
    /// </summary>
    public static ForegroundApp? FromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        string title = ReadWindowText(hwnd);
        string className = ReadClassName(hwnd);
        string path = "";
        string name = "";

        try
        {
            using var p = Process.GetProcessById((int)pid);
            try { path = p.MainModule?.FileName ?? ""; } catch { /* 受保护进程 */ }
            if (!string.IsNullOrEmpty(path))
                name = Path.GetFileName(path);
            else
                name = p.ProcessName + ".exe";   // 退化为进程名
        }
        catch
        {
            // 进程已退出 / 无法打开：保留窗口信息
        }

        return new ForegroundApp(hwnd, path, name, title, className);
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        int len = Win32.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(Math.Max(len + 1, 2));
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
