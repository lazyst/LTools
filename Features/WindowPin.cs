using System.Runtime.InteropServices;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 窗口置顶（对应原版 lib/WindowPin.ahk）。CapsLock+右键 切换光标下窗口置顶/取消。
/// 桌面窗口（Progman/WorkerW）、任务栏工具窗口不置顶；其余直接切换，无全屏确认层。
/// </summary>
internal static class WindowPin
{
    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x8;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;

    /// <summary>切换光标下窗口置顶状态。</summary>
    public static void ToggleAtCursor(int x, int y)
    {
        var raw = Win32.WindowFromPoint(new Win32.Point { X = x, Y = y });
        if (raw == IntPtr.Zero) { Notify("光标下无有效窗口"); return; }
        // WindowFromPoint 返回的是子控件（如按钮），需上溯到顶层拥有者窗口——
        // 对子窗口设 WS_EX_TOPMOST 无意义，这正是旧版“置顶无效”的根因。
        var hwnd = Win32.GetAncestor(raw, Win32.GaRootOwner);
        if (hwnd == IntPtr.Zero) hwnd = raw;

        if (!IsTaskbarWindow(hwnd)) { Notify("当前窗口不是任务栏窗口，无法置顶"); return; }

        var className = GetClassNameStr(hwnd);
        if (className is "Progman" or "WorkerW") { Notify("桌面窗口不能置顶"); return; }

        bool isPinned = (GetWindowLong(hwnd, GwlExstyle) & WsExTopmost) != 0;
        if (isPinned)
        {
            Win32.SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize);
            Notify("已取消置顶");
        }
        else
        {
            Win32.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize);
            Notify("已置顶窗口");
        }
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        // 排除纯工具窗口（WS_EX_TOOLWINDOW，不可见的辅助窗口）。
        var ex = GetWindowLong(hwnd, GwlExstyle);
        if ((ex & 0x80) != 0) return false; // WS_EX_TOOLWINDOW
        return true;
    }

    private static string GetClassNameStr(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void Notify(string msg) => TrayService.Notify(msg);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
