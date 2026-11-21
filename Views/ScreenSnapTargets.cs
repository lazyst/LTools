using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 框选时的「窗口」探测：按 z 序枚举 Win32 窗口与它们的子窗口（快、不阻塞）。
/// </summary>
/// <remarks>
/// <para>
/// 控件那一半在 <see cref="Features.CapturePro.SnapTree"/> / <see cref="Features.CapturePro.SnapProbe"/>：
/// 截图窗口自己盖住了整个屏幕，所以 UIA 的 <c>FromPoint</c> 只会命中我们自己，只能先按 z 序找光标下
/// 第一个不属于本进程的顶层窗口，再从它往下钻。
/// </para>
/// <para>
/// 这里的方法都可以在 UI 线程调用（Win32 部分很快）。控件那边不再有「限深 / 每层上限 / 总数上限」——
/// 旧实现「一次性 BFS 整棵树」按层序截断，深层内容永远最后才轮到，是有窗口只能吸整窗的原因。
/// </para>
/// </remarks>
internal static class ScreenSnapTargets
{
    /// <summary>窗口树枚举时跳过的系统辅助窗口（提示气泡、阴影、输入法合成窗等）。</summary>
    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32",
        "SysShadow",
        "MSCTFIME UI",
        "IME",
        "ForegroundStaging",
        "TaskListThumbnailWnd",
    };

    // ============ Win32 窗口树 ============

    /// <summary>当前显示器上所有可选的窗口 / 子窗口（含被其他窗口盖住的部分，按 z 序从前往后）。</summary>
    public static List<SnapBox> FromWindows(Rectangle monitor)
    {
        var list = new List<SnapBox>(256);
        uint self = (uint)Environment.ProcessId;

        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                if (TryWindow(hwnd, self, monitor, out var box))
                {
                    list.Add(box with { Owner = hwnd });

                    Win32.EnumChildWindows(hwnd, (child, _) =>
                    {
                        if (child != hwnd && TryWindow(child, self, monitor, out var cbox, topLevel: false))
                            list.Add(cbox with { Owner = hwnd });
                        return true;
                    }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败就退化成「只能手动框选」，不影响截图
        }

        return list;
    }

    private static bool TryWindow(IntPtr hwnd, uint selfPid, Rectangle monitor, out SnapBox box,
        bool topLevel = true)
    {
        box = default;

        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == selfPid) return false;

        // 被 DWM「隐身」的窗口（最小化到别的虚拟桌面、UWP 的休眠壳）IsWindowVisible 仍是 true，
        // 不排掉会吸到屏幕上看不见的东西。只查顶层窗口：子窗口不单独隐身。
        if (topLevel && IsCloaked(hwnd)) return false;

        if (Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64() is long ex
            && (ex & Win32.WsExTransparent) != 0) return false;

        var cls = new StringBuilder(128);
        Win32.GetClassName(hwnd, cls, cls.Capacity);
        if (SkipClasses.Contains(cls.ToString())) return false;

        if (!TryBounds(hwnd, out var r)) return false;
        var clipped = Rectangle.Intersect(r, monitor);
        if (clipped.Width < 8 || clipped.Height < 8) return false;

        box = new SnapBox(clipped, WindowLabel(hwnd, cls.ToString(), r));
        return true;
    }

    /// <summary>窗口实际可见边框：优先 DWM 的 extended frame bounds（不含不可见的阴影边距）。</summary>
    private static bool TryBounds(IntPtr hwnd, out Rectangle bounds)
    {
        bounds = default;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmwaExtendedFrameBounds,
                out Win32.Rect dwm, System.Runtime.InteropServices.Marshal.SizeOf<Win32.Rect>()) == 0)
        {
            bounds = Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
            if (bounds.Width > 0 && bounds.Height > 0) return true;
        }

        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static string WindowLabel(IntPtr hwnd, string cls, Rectangle bounds)
    {
        int len = Win32.GetWindowTextLength(hwnd);
        if (len > 0)
        {
            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length > 0) return Shorten(title);
        }

        // 没有标题的窗口（工具栏、面板、桌面）：用类名更好辨认
        if (cls == "Progman" || cls == "WorkerW") return "桌面";
        if (cls == "Shell_TrayWnd") return "任务栏";
        return Shorten(cls.Length > 0 ? cls : $"{bounds.Width} × {bounds.Height}");
    }

    // ============ 窗口命中 ============

    /// <summary>
    /// 光标下第一个不属于本进程、且包含该点的**顶层**窗口（跳过盖在最上面的截图窗口，不往下钻）。
    /// 用于悬停时只保留「最上层那个窗口及其子控件」的候选，避免选到被遮住的窗口里的控件。
    /// </summary>
    public static IntPtr TopWindowAtPoint(IntPtr overlayHwnd, Point point)
    {
        uint self = (uint)Environment.ProcessId;
        IntPtr found = IntPtr.Zero;

        try
        {
            Win32.EnumWindows((hwnd, _) =>
            {
                if (hwnd == overlayHwnd) return true;
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
                if (!TryBounds(hwnd, out var r) || !r.Contains(point)) return true;

                // 命中之后再查 cloaked（每次移动只多一次 DWM 调用），排掉看不见的壳窗口
                if (IsCloaked(hwnd)) return true;

                found = hwnd;
                return false;   // EnumWindows 按 z 序从前往后，第一个命中的就是最上面的
            }, IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }

        return found;
    }

    /// <summary>DWM 的「隐身」标记：<c>IsWindowVisible</c> 为 true 但画面上不存在。</summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return Win32.DwmGetWindowAttributeInt(hwnd, Win32.DwmwaCloaked,
                out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Shorten(string s) => s.Length <= 28 ? s : s[..28] + "…";
}
