using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features;
using LTools.Hooks;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 「点击窗口选取」遮罩：像截图吸附那样在屏幕上移动鼠标，光标下的顶层窗口立刻亮起
/// **边缘渐变高亮**（四边朝内淡出的光晕 + 沿对角渐变的描边），单击即选中该窗口。
/// </summary>
/// <remarks>
/// <para>
/// 命中走 <see cref="PickWindowAtPoint"/>：按 z 序找第一个**真正可点选**的窗口，
/// 跳过 cloaked / 工具层 / 不抢焦点 / 点击穿透 / 输入法辅助窗——这些壳窗口
/// <c>IsWindowVisible</c> 仍为 true 且常盖住整屏（如「Shell Handwriting Canvas」手写面板），
/// 不复用的 <c>ScreenSnapTargets.TopWindowAtPoint</c>（那里只按「矩形包含」取第一个，
/// 截图器后续还有候选过滤与手动框选兜底，点选器直接当结果就会锁死在同一个壳窗口）。
/// 矩形用 <see cref="WindowFinder.GetVisibleBounds"/>（DWM 可见边框，不含阴影边距），
/// 物理像素按光标所在显示器的 DPI 换成窗口 DIP——与截图框选同一套坐标系。
/// </para>
/// <para>
/// 返回窗口的标题 / 类名 / 进程名三件套；<c>ProcessName</c> 由窗口句柄的 PID 解析，
/// 所以「选进程」也走这个点选器。顶部保留「✕ 取消」与「从列表选…」两个纯鼠标出口，
/// 给没有可见窗口的后台进程 / 精确挑窗口时用；默认交互一律是点击选取。
/// </para>
/// </remarks>
public partial class WindowPickOverlay : Window
{
    /// <summary>四边光晕的宽度（DIP）。</summary>
    private const double Glow = 26;

    /// <summary>鼠标移动的命中节流（毫秒）：每次移动都全量 EnumWindows + DWM 会拖住事件队列。</summary>
    private const long MoveThrottleMs = 24;

    /// <summary>窗口树枚举时跳过的系统辅助窗口（提示气泡、阴影、输入法合成窗、壳桌面等）。</summary>
    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32", "SysShadow", "MSCTFIME UI", "IME",
        "ForegroundStaging", "TaskListThumbnailWnd",
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly System.Drawing.Rectangle _monitor;
    private readonly double _sx, _sy;
    private IntPtr _current;
    private string _title = "", _class = "", _process = "";
    private bool _finished;
    private long _lastMoveTick;

    /// <summary>选中的窗口信息；取消 / 切列表时为 null。</summary>
    public SystemPickerDialog.WindowInfo? Result { get; private set; }

    /// <summary>用户点了「从列表选…」（此时由 <see cref="SystemPickerDialog"/> 打开原列表窗口）。</summary>
    public bool WantsList { get; private set; }

    private WindowPickOverlay(System.Drawing.Rectangle monitor, double sx, double sy, bool allowList)
    {
        InitializeComponent();
        _monitor = monitor;
        _sx = sx > 0 ? sx : 1;
        _sy = sy > 0 ? sy : 1;

        Left = monitor.Left / _sx;
        Top = monitor.Top / _sy;
        Width = Math.Max(1, monitor.Width / _sx);
        Height = Math.Max(1, monitor.Height / _sy);

        if (!allowList) ListButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>弹出遮罩点选。返回「选中的窗口 / 是否要切列表」。</summary>
    public static (SystemPickerDialog.WindowInfo? Info, bool WantsList) Pick(Window? owner, bool allowList = true)
    {
        // 点选期间收起 LTools 全部窗口（含 owner），别让自己的窗口挡在前面 / 混进候选。
        // 遮罩是 Topmost 全屏，**不设 Owner**：owner 被最小化后，owned window 会被系统一起隐藏
        // （照 ScreenPicker.TryPickRegion 的已验证做法）。
        var restore = TransientMinimize.MinimizeAll(owner);
        // 裸右键长按手势让路：它吞掉 down/up 再注入一次右键，遮罩会「连点两次才退出」。
        MouseHook.SetWindowPickerActive(true);
        try
        {
            System.Threading.Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完
            var monitor = ScreenPicker.MonitorUnderCursor();
            var (sx, sy) = ScreenPickerWindow.MonitorScale(monitor);
            var overlay = new WindowPickOverlay(monitor, sx, sy, allowList);
            bool? ok = overlay.ShowDialog();
            return (ok == true ? overlay.Result : null, overlay.WantsList);
        }
        finally
        {
            MouseHook.SetWindowPickerActive(false);
            restore();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 物理像素摆放：多显示器不同缩放时 WPF 的 Left/Top（DIP）会算偏。
        // 不带 SWP_NOACTIVATE：这个窗口必须拿到键盘焦点，否则 Esc 永远收不到。
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Win32.SetWindowPos(hwnd, Win32.HwndTopmost,
                _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, 0);

        // Loaded 阶段直接 Focus() 可能失败（窗口刚创建、还没有逻辑焦点），
        // 显式激活 + 排到输入优先级再抢一次，Esc / Enter 才有着落。
        Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    // ============ 命中：只挑「真正看得见、能点选」的窗口 ============

    /// <summary>
    /// 一个窗口能不能作为点选目标。抽成纯谓词，便于无头冒烟直接验证过滤规则
    /// （cloaked 壳窗口 / 工具层 / 不抢焦点 / 点击穿透 / IME 辅助窗 / 过小的辅助窗一律跳过）。
    /// </summary>
    internal static bool IsPickable(long exStyle, int cloaked, string cls, int w, int h)
    {
        if (cloaked != 0) return false;
        if ((exStyle & (Win32.WsExTransparent | Win32.WsExToolwindow | Win32.WsExNoactivate)) != 0)
            return false;
        if (w < 8 || h < 8) return false;
        if (SkipClasses.Contains(cls)) return false;
        return true;
    }

    /// <summary>
    /// 光标下第一个可点选的**顶层**窗口（按 z 序从前往后；跳过遮罩自己与本进程窗口）。
    /// 只保留与光标所在显示器有交集、且真的包含该点的窗口，避免跨屏 / 壳窗口误命中。
    /// </summary>
    internal static IntPtr PickWindowAtPoint(IntPtr overlayHwnd, System.Drawing.Point point,
        System.Drawing.Rectangle monitor)
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

                // 便宜的判断先做：ex-style / cloaked / 类名不过关的直接丢掉，
                // 别为每个窗口都去问 DWM 要矩形。
                long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
                if ((ex & (Win32.WsExTransparent | Win32.WsExToolwindow | Win32.WsExNoactivate)) != 0)
                    return true;
                Win32.DwmGetWindowAttributeInt(hwnd, Win32.DwmwaCloaked, out int cloaked, sizeof(int));
                var cls = new StringBuilder(128);
                Win32.GetClassName(hwnd, cls, cls.Capacity);
                if (cloaked != 0 || SkipClasses.Contains(cls.ToString())) return true;

                var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
                var clipped = System.Drawing.Rectangle.Intersect(
                    new System.Drawing.Rectangle(x, y, w, h), monitor);
                if (!IsPickable(ex, cloaked, cls.ToString(), clipped.Width, clipped.Height)) return true;
                if (!clipped.Contains(point)) return true;

                found = hwnd;
                return false;   // 第一个合格的 = z 序最上层那个
            }, IntPtr.Zero);
        }
        catch { return IntPtr.Zero; }
        return found;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        // 节流：鼠标每秒上百次移动，每次全量枚举窗口会把 UI 线程拖住，
        // 连带让右键 / Esc 的取消事件排队（表现为「点很多次才退出」）。
        long now = Environment.TickCount64;
        if (now - _lastMoveTick < MoveThrottleMs) return;
        _lastMoveTick = now;

        Point p = e.GetPosition(this);   // 窗口 DIP
        var screen = new System.Drawing.Point(
            _monitor.X + (int)Math.Round(p.X * _sx),
            _monitor.Y + (int)Math.Round(p.Y * _sy));

        IntPtr hwnd = PickWindowAtPoint(new WindowInteropHelper(this).Handle, screen, _monitor);
        if (hwnd == IntPtr.Zero) { ClearHighlight(); return; }
        if (hwnd == _current) return;   // 同一窗口内移动不必重画 / 重查进程

        _current = hwnd;
        _title = TitleOf(hwnd);
        _class = ClassOf(hwnd);
        _process = ProcessOf(hwnd);

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        double left = (x - _monitor.X) / _sx;
        double top = (y - _monitor.Y) / _sy;
        double width = w / _sx, height = h / _sy;
        if (width <= 0 || height <= 0) { ClearHighlight(); return; }

        // 窗口可能跨显示器 / 超出遮罩：与可视区求交，别把光晕画到屏幕外。
        double r = Math.Min(left + width, ActualWidth);
        double b = Math.Min(top + height, ActualHeight);
        left = Math.Max(0, left); top = Math.Max(0, top);
        width = Math.Max(0, r - left); height = Math.Max(0, b - top);
        if (width < 4 || height < 4) { ClearHighlight(); return; }

        double gh = Math.Min(Glow, height / 2);
        double gw = Math.Min(Glow, width / 2);
        Place(GlowTopRect, left, top, width, gh);
        Place(GlowBottomRect, left, top + height - gh, width, gh);
        Place(GlowLeftRect, left, top, gw, height);
        Place(GlowRightRect, left + width - gw, top, gw, height);
        Place(Outline, left, top, width, height);

        InfoText.Text = string.IsNullOrWhiteSpace(_title)
            ? $"{_process} · {_class}"
            : $"{_title}   ·   {_process} · {_class}";
        PlaceInfoBar(left, top, width, height);
    }

    // ============ 收尾：所有出口走同一个幂等 Finish ============

    /// <summary>
    /// 唯一的关闭入口。右键连点 / Esc 连按 / 按钮重复触发只会生效一次；
    /// <c>DialogResult</c> 万一在非 ShowDialog 场景下抛异常，还有 <see cref="Window.Close"/> 兜底。
    /// </summary>
    private void Finish(bool accept, bool wantsList)
    {
        if (_finished) return;
        _finished = true;
        WantsList = wantsList;
        if (accept) Result = new SystemPickerDialog.WindowInfo(_title, _class, _process);
        try { DialogResult = accept; }
        catch { Close(); }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在顶部按钮上时别把它当成「选中窗口」（Preview 隧道先到窗口层，
        // 直接 Finish 会把按钮的 Click 吞掉）。
        if (OverBarButton(e.OriginalSource as DependencyObject)) return;
        if (_current == IntPtr.Zero) return;
        Finish(true, false);
        e.Handled = true;
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        Finish(false, false);
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false, false);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(false, false); e.Handled = true; }
        else if (e.Key == Key.Enter && _current != IntPtr.Zero)
        {
            Finish(true, false);
            e.Handled = true;
        }
    }

    /// <summary>keydown 万一被窗口激活吃掉，keyup 再兜一次（Finish 幂等）。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(false, false); e.Handled = true; }
    }

    private void List_Click(object sender, RoutedEventArgs e) => Finish(false, true);

    private bool OverBarButton(DependencyObject? src)
    {
        while (src != null)
        {
            if (ReferenceEquals(src, CancelButton) || ReferenceEquals(src, ListButton)) return true;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        System.Windows.Controls.Canvas.SetLeft(el, x);
        System.Windows.Controls.Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
        el.Visibility = Visibility.Visible;
    }

    private void PlaceInfoBar(double left, double top, double width, double height)
    {
        InfoBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double iw = Math.Min(InfoBar.DesiredSize.Width, Math.Max(0, ActualWidth - 16));
        double ih = InfoBar.DesiredSize.Height;
        double ix = Math.Clamp(left, 8, Math.Max(8, ActualWidth - iw - 8));
        double iy = top + height + 8;
        if (iy + ih > ActualHeight - 8) iy = Math.Max(8, top - ih - 8);   // 放不下翻到窗口上方
        InfoBar.Margin = new Thickness(ix, iy, 0, 0);
        InfoBar.MaxWidth = iw;
        InfoBar.Visibility = Visibility.Visible;
    }

    private void ClearHighlight()
    {
        _current = IntPtr.Zero;
        GlowTopRect.Visibility = GlowBottomRect.Visibility = Visibility.Collapsed;
        GlowLeftRect.Visibility = GlowRightRect.Visibility = Visibility.Collapsed;
        Outline.Visibility = Visibility.Collapsed;
        InfoBar.Visibility = Visibility.Collapsed;
    }

    private static string TitleOf(IntPtr hwnd)
    {
        int len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcessOf(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }   // 进程刚退出 / 权限不足
    }
}
