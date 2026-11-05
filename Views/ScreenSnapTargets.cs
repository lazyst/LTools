using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LTools.Native;

namespace LTools.Views;

/// <summary>框选时可以吸附的一个矩形：窗口、子窗口或 UI Automation 控件（屏幕物理像素）。</summary>
internal readonly record struct SnapBox(Rectangle Bounds, string Label, IntPtr Owner = default)
{
    /// <summary>面积（用于在重叠的候选里挑最小的那个）。</summary>
    public long Area => (long)Bounds.Width * Bounds.Height;
}

/// <summary>
/// 框选时的「窗口 / 控件」探测：先枚举 Win32 窗口树（快、不阻塞），再用 UI Automation
/// 在光标所在窗口里往下钻一层，拿到自绘界面（WPF / 浏览器 / Electron）里的按钮、输入框等。
/// </summary>
/// <remarks>
/// 截图窗口自己盖住了整个屏幕，所以 UIA 的 <c>FromPoint</c> 只会命中我们自己。
/// 这里改为：按 z 序找光标下第一个不属于本进程的顶层窗口，再用 ChildWindowFromPointEx
/// 递归下钻到最深的子窗口，最后交给 UIA 从该窗口的根元素往下找包含光标的最小子元素。
/// 所有方法都可以在 UI 线程调用（Win32 部分很快），UIA 部分请放到后台线程并限流。
/// </remarks>
internal static class ScreenSnapTargets
{
    private const int MaxUiaDepth = 8;
    private const int MaxUiaChildren = 160;

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

    private static readonly Lazy<UIA3Automation> Automation = new(() => new UIA3Automation
    {
        // 目标程序无响应时不要让探测把后台线程挂死
        ConnectionTimeout = TimeSpan.FromMilliseconds(300),
        TransactionTimeout = TimeSpan.FromMilliseconds(400),
    });

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
                        if (child != hwnd && TryWindow(child, self, monitor, out var cbox))
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

    private static bool TryWindow(IntPtr hwnd, uint selfPid, Rectangle monitor, out SnapBox box)
    {
        box = default;

        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == selfPid) return false;

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

    // ============ UI Automation ============

    /// <summary>
    /// 光标下窗口里的 UIA 元素（由内到外，含窗口本身）。在后台线程调用。
    /// 目标程序没有暴露 UIA 时返回空列表，调用方退回 Win32 结果。
    /// </summary>
    public static List<SnapBox> FromUia(IntPtr overlayHwnd, Point point, Rectangle monitor)
    {
        var result = new List<SnapBox>(10);
        try
        {
            IntPtr top = TopWindowUnderPoint(overlayHwnd, point);
            if (top == IntPtr.Zero) return result;

            IntPtr root = Win32.GetAncestor(top, Win32.GaRoot);
            if (root == IntPtr.Zero) root = top;

            AutomationElement? element = Automation.Value.FromHandle(root);
            if (element == null) return result;

            // UIA 没有「按点列子元素」的接口：从顶层窗口一层层往下，只跟进包含光标的那个子元素。
            var path = new List<(AutomationElement El, Rectangle Bounds)>();
            var current = element;
            var currentBounds = SafeBounds(current);
            path.Add((current, currentBounds));

            for (int depth = 0; depth < MaxUiaDepth; depth++)
            {
                AutomationElement? next = null;
                Rectangle nextBounds = default;
                long best = long.MaxValue;
                int seen = 0;

                foreach (var child in SafeChildren(current))
                {
                    if (++seen > MaxUiaChildren) break;

                    Rectangle b = SafeBounds(child);
                    if (b.Width < 4 || b.Height < 4) continue;
                    if (!b.Contains(point)) continue;
                    if (b == currentBounds) continue;

                    long area = (long)b.Width * b.Height;
                    if (area < best) { best = area; next = child; nextBounds = b; }
                }

                if (next == null) break;
                current = next;
                currentBounds = nextBounds;
                path.Add((current, currentBounds));
            }

            // 由内到外：滚轮第一次切换就是「父级」
            for (int i = path.Count - 1; i >= 0; i--)
                Add(result, path[i].Bounds, LabelOf(path[i].El), point, monitor, root);
        }
        catch
        {
            // UIA 不可用（目标无响应 / 权限隔离）：保留 Win32 的窗口级结果
        }
        return result;
    }

    private static void Add(List<SnapBox> list, Rectangle bounds, string label, Point point,
        Rectangle monitor, IntPtr owner)
    {
        var clipped = Rectangle.Intersect(bounds, monitor);
        if (clipped.Width < 4 || clipped.Height < 4 || !clipped.Contains(point)) return;
        if (list.Count > 0 && list[^1].Bounds == clipped) return;
        list.Add(new SnapBox(clipped, label, owner));
    }

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

    /// <summary>光标下最深的那层窗口（顶层窗口 + 逐层子窗口），UIA 从这里往下钻。</summary>
    private static IntPtr TopWindowUnderPoint(IntPtr overlayHwnd, Point point)
    {
        IntPtr found = TopWindowAtPoint(overlayHwnd, point);
        if (found == IntPtr.Zero) return IntPtr.Zero;

        // 往下钻到最深的子窗口（标准 Win32 控件有独立 HWND；自绘界面靠后面的 UIA）
        var current = found;
        for (int depth = 0; depth < 12; depth++)
        {
            IntPtr child = Win32.ChildWindowFromPointEx(current,
                new Win32.Point { X = point.X, Y = point.Y },
                Win32.CwpSkipinvisible | Win32.CwpSkiptransparent);
            if (child == IntPtr.Zero || child == current) break;
            current = child;
        }
        return current;
    }

    private static Rectangle SafeBounds(AutomationElement element)
    {
        try { return element.Properties.BoundingRectangle.ValueOrDefault; }
        catch { return Rectangle.Empty; }
    }

    private static AutomationElement[] SafeChildren(AutomationElement element)
    {
        try { return element.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    private static string LabelOf(AutomationElement element)
    {
        try
        {
            string kind = KindLabel(element.Properties.ControlType.ValueOrDefault);
            string name = element.Properties.Name.ValueOrDefault?.Trim() ?? "";
            return name.Length == 0 ? kind : $"{kind} · {Shorten(name)}";
        }
        catch
        {
            return "控件";
        }
    }

    private static string KindLabel(ControlType type) => type switch
    {
        ControlType.Button => "按钮",
        ControlType.Edit => "输入框",
        ControlType.Text => "文本",
        ControlType.Document => "文档",
        ControlType.List => "列表",
        ControlType.ListItem => "列表项",
        ControlType.Menu => "菜单",
        ControlType.MenuItem => "菜单项",
        ControlType.Tab => "选项卡",
        ControlType.TabItem => "选项卡",
        ControlType.Tree => "树",
        ControlType.TreeItem => "树节点",
        ControlType.ComboBox => "下拉框",
        ControlType.CheckBox => "复选框",
        ControlType.RadioButton => "单选框",
        ControlType.Slider => "滑块",
        ControlType.ToolBar => "工具栏",
        ControlType.StatusBar => "状态栏",
        ControlType.Pane => "面板",
        ControlType.Window => "窗口",
        ControlType.Image => "图片",
        ControlType.Hyperlink => "链接",
        ControlType.Table => "表格",
        ControlType.DataGrid => "表格",
        ControlType.DataItem => "表格行",
        ControlType.Custom => "控件",
        _ => "元素",
    };

    private static string Shorten(string s) => s.Length <= 28 ? s : s[..28] + "…";
}
