using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DPoint = System.Drawing.Point;
using WPoint = System.Windows.Point;
using WSize = System.Windows.Size;
using LTools.Features;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 「选控件」遮罩：移动鼠标时高亮光标下的 UI Automation 控件，单击选中；
/// 顶部「从控件树选…」给悬停拿不到的控件（被遮挡 / 折叠页里）留一个列表出口。
/// </summary>
/// <remarks>
/// <para>
/// 命中不用 UIA 的 <c>FromPoint</c>：遮罩自己盖住全屏，只会命中我们自己。改为照截图吸附的
/// 已验证做法——<c>ScreenSnapTargets.TopWindowAtPoint</c> 找光标下的顶层窗口，再按窗口
/// 后台取一次控件树（<see cref="ControlTargets.Snapshot"/>，按句柄缓存），鼠标移动只在本地
/// <c>Rectangle.Contains</c> 里挑「最深、面积最小」的那个。每次移动都跨进程查 UIA 会拖住事件队列，
/// 连带让 Esc / 右键的取消排队。
/// </para>
/// <para>
/// 命中叶子（Text / Image）时往上抬到最近的可交互父级：点文字中心也点得到按钮，但「设值」
/// 只有 Edit 能做。是否可当目标（名称 / AutomationId 至少一个）、同名控件有几个，
/// 都由 <see cref="ControlTargets"/> 的纯函数判断，这里只负责画和收尾。
/// </para>
/// </remarks>
public partial class ControlPickOverlay : Window
{
    /// <summary>鼠标移动的命中节流（毫秒）：与窗口点选器 / 截图吸附同一口径。</summary>
    private const long MoveThrottleMs = 24;

    /// <summary>按窗口缓存控件树的上限（浏览器 / 多窗口来回扫时不无限涨）。</summary>
    private const int MaxCachedWindows = 40;

    private readonly Rectangle _monitor;
    private readonly double _sx, _sy;
    private readonly Dictionary<IntPtr, ControlTree> _trees = new();
    private IntPtr _window;
    private IntPtr _pending;
    private ControlTree? _tree;
    private string? _problem;
    private string _windowTitle = "", _windowClass = "";
    private int _target = -1;
    private DPoint _lastPoint;
    private long _lastMoveTick;
    private bool _finished;

    /// <summary>选中的控件；取消 / 没选到时为 null。</summary>
    public PickedControl? Result { get; private set; }

    private ControlPickOverlay(Rectangle monitor, double sx, double sy)
    {
        InitializeComponent();
        _monitor = monitor;
        _sx = sx > 0 ? sx : 1;
        _sy = sy > 0 ? sy : 1;

        Left = monitor.Left / _sx;
        Top = monitor.Top / _sy;
        Width = Math.Max(1, monitor.Width / _sx);
        Height = Math.Max(1, monitor.Height / _sy);
    }

    /// <summary>弹出遮罩点选控件；取消返回 null。</summary>
    public static PickedControl? Pick(Window? owner)
    {
        using var session = PickerSession.Begin(owner);
        var overlay = new ControlPickOverlay(session.Monitor, session.ScaleX, session.ScaleY);
        return overlay.ShowDialog() == true ? overlay.Result : null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 物理像素摆放：多显示器不同缩放时 WPF 的 Left/Top（DIP）会算偏。
        // 不带 SWP_NOACTIVATE：这个窗口必须拿到键盘焦点，否则 Esc 永远收不到。
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Win32.SetWindowPos(hwnd, Win32.HwndTopmost,
                _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, 0);

        Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Focus();
            Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    // ============ 悬停：窗口 → 控件树 → 控件 ============

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        WPoint p = e.GetPosition(this);
        if (OverTopBar(e.OriginalSource as DependencyObject))
        {
            ClearHighlight();
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastMoveTick < MoveThrottleMs) return;
        _lastMoveTick = now;

        DPoint screen = ToScreen(p);
        _lastPoint = screen;

        IntPtr hwnd = ScreenSnapTargets.TopWindowAtPoint(new WindowInteropHelper(this).Handle, screen);
        if (hwnd == IntPtr.Zero || !UsableWindow(hwnd))
        {
            ClearHighlight();
            return;
        }

        if (hwnd != _window)
        {
            _window = hwnd;
            _windowTitle = WindowFinder.GetText(hwnd);
            _windowClass = WindowFinder.GetClass(hwnd);
            _tree = null;
            _problem = null;
            _target = -1;
            ClearHighlight();
            EnsureTree(hwnd);
            return;
        }

        if (_tree == null)
        {
            ShowPending();
            return;
        }

        if (_problem != null)
        {
            ShowProblem(_problem);
            return;
        }

        UpdateTarget(screen);
    }

    /// <summary>整棵树只在**第一次进入某个窗口**时后台取一次；之后鼠标移动只做本地判断。</summary>
    private void EnsureTree(IntPtr hwnd)
    {
        if (_trees.TryGetValue(hwnd, out ControlTree? cached))
        {
            if (_window == hwnd) ApplyTree(cached);
            return;
        }

        if (_pending == hwnd) return;
        _pending = hwnd;

        Task.Run(() => ControlTargets.Snapshot(hwnd)).ContinueWith(t =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => AcceptTree(hwnd, t)));
        });
    }

    private void AcceptTree(IntPtr hwnd, Task<ControlTree> task)
    {
        if (_pending != hwnd) return;
        _pending = IntPtr.Zero;

        ControlTree tree = task.Status == TaskStatus.RanToCompletion ? task.Result : ControlTree.Empty;
        if (_trees.Count >= MaxCachedWindows) _trees.Clear();
        _trees[hwnd] = tree;

        if (_window != hwnd) return;
        ApplyTree(tree);
    }

    /// <summary>拿到树之后：能读就定位，读不到 / 没有控件就给出明确原因（别停在「识别中」）。</summary>
    private void ApplyTree(ControlTree tree)
    {
        _tree = tree;
        _problem = tree.Unavailable
            ? tree.Problem
            : tree.Count <= 1
                ? "这个窗口里没有可用的控件（可能整窗是一个自绘区域）"
                : null;

        if (_problem != null)
        {
            ShowProblem(_problem);
            return;
        }

        UpdateTarget(_lastPoint);
    }

    private void UpdateTarget(DPoint screen)
    {
        if (_tree == null) return;

        int index = ControlTargets.AtPoint(_tree.Nodes, screen);
        if (index >= 0) index = ControlTargets.RollUp(_tree.Nodes, index);

        // 根节点（整扇窗口）不当目标：点它没有意义，还会把整屏画个框
        if (index >= 0 && _tree.Nodes[index].Depth == 0) index = -1;
        if (index == _target && index >= 0) return;

        _target = index;
        if (index < 0)
        {
            ClearHighlight();
            return;
        }

        ShowTarget(_tree.Nodes[index]);
    }

    /// <summary>
    /// 这个窗口值不值得读控件树：桌面 / 托盘 / 输入法辅助窗、cloaked、点击穿透、过小的壳都排掉。
    /// **不像窗口点选器那样按工具层 / 不抢焦点过滤**：弹出菜单正好是这一类，而菜单项也要能选。
    /// </summary>
    private static bool UsableWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
        if ((ex & Win32.WsExTransparent) != 0) return false;

        Win32.DwmGetWindowAttributeInt(hwnd, Win32.DwmwaCloaked, out int cloaked, sizeof(int));
        if (cloaked != 0) return false;

        var name = new System.Text.StringBuilder(128);
        Win32.GetClassName(hwnd, name, name.Capacity);
        if (WindowPickOverlay.IsShellWindowClass(name.ToString())) return false;

        var (_, _, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        return w >= 8 && h >= 8;
    }

    /// <summary>光标移出遮罩（换到别的显示器）时把高亮收掉，别留一个过期的框。</summary>
    private void Window_MouseLeave(object sender, MouseEventArgs e) => ClearHighlight();

    // ============ 画 ============

    private void ShowTarget(ControlTarget node)
    {
        var (left, top, width, height) = ToDip(node.Bounds);
        if (!ClipToOverlay(ref left, ref top, ref width, ref height))
        {
            ClearHighlight();
            return;
        }

        Place(ControlFill, left, top, width, height);
        Place(ControlOutline, left, top, width, height);

        InfoTitle.Text = $"{node.ControlType} · {ControlTargets.TitleOf(node)}";

        string detail = ControlTargets.DetailOf(node);
        string window = _windowTitle.Length > 0 ? _windowTitle : _windowClass;
        InfoDetail.Text = window.Length > 0
            ? (detail.Length > 0 ? detail + " · 窗口: " + window : "窗口: " + window)
            : detail;

        int same = ControlTargets.CountSame(_tree!.Nodes, node);
        string warn = !ControlTargets.CanTarget(node)
            ? "这个控件没有名称和 AutomationId，选不了：用「鼠标操作」点坐标，或「文字点击」按文字找"
            : same > 1
                ? $"窗口里还有 {same - 1} 个同样的控件；运行时取最先找到的那个"
                : "";
        SetWarn(warn);

        PlaceCard(left, top, width, height);
    }

    private void ShowPending()
    {
        ControlFill.Visibility = ControlOutline.Visibility = Visibility.Collapsed;
        InfoTitle.Text = "识别控件中…";
        InfoDetail.Text = _windowTitle.Length > 0 ? "窗口: " + _windowTitle : _windowClass;
        SetWarn("");
        PlaceCardAtCursor();
    }

    private void ShowProblem(string problem)
    {
        ControlFill.Visibility = ControlOutline.Visibility = Visibility.Collapsed;
        InfoTitle.Text = "读不到这个窗口的控件";
        InfoDetail.Text = _windowTitle.Length > 0 ? "窗口: " + _windowTitle : "";
        SetWarn(problem);
        PlaceCardAtCursor();
    }

    private void SetWarn(string text)
    {
        InfoWarn.Text = text;
        InfoWarn.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearHighlight()
    {
        _target = -1;
        ControlFill.Visibility = ControlOutline.Visibility = Visibility.Collapsed;
        InfoCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>屏幕物理像素 → 遮罩 DIP（多显示器各自的缩放不同）。</summary>
    private (double Left, double Top, double Width, double Height) ToDip(Rectangle rect) =>
        ((rect.X - _monitor.X) / _sx,
            (rect.Y - _monitor.Y) / _sy,
            rect.Width / _sx,
            rect.Height / _sy);

    private DPoint ToScreen(WPoint dip) => new(
        _monitor.X + (int)Math.Round(dip.X * _sx),
        _monitor.Y + (int)Math.Round(dip.Y * _sy));

    /// <summary>与遮罩可视区求交；太小（跨屏 / 基本在屏幕外）时返回 false。</summary>
    private bool ClipToOverlay(ref double left, ref double top, ref double width, ref double height)
    {
        double right = Math.Min(left + width, ActualWidth);
        double bottom = Math.Min(top + height, ActualHeight);
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        width = Math.Max(0, right - left);
        height = Math.Max(0, bottom - top);
        return width >= 2 && height >= 2;
    }

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
        el.Visibility = Visibility.Visible;
    }

    /// <summary>信息卡贴控件矩形下方；下面放不下翻到上方；左右都夹在可视区里。</summary>
    private void PlaceCard(double left, double top, double width, double height)
    {
        InfoCard.Visibility = Visibility.Visible;
        InfoCard.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
        double iw = Math.Min(InfoCard.DesiredSize.Width, Math.Max(0, ActualWidth - 16));
        double ih = InfoCard.DesiredSize.Height;

        double ix = Math.Clamp(left, 8, Math.Max(8, ActualWidth - iw - 8));
        double iy = top + height + 8;
        if (iy + ih > ActualHeight - 8) iy = Math.Max(8, top - ih - 8);

        InfoCard.Margin = new Thickness(ix, iy, 0, 0);
        InfoCard.MaxWidth = iw;
    }

    private void PlaceCardAtCursor()
    {
        InfoCard.Visibility = Visibility.Visible;
        InfoCard.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
        double iw = Math.Min(InfoCard.DesiredSize.Width, Math.Max(0, ActualWidth - 16));
        double ih = InfoCard.DesiredSize.Height;

        if (Win32.GetCursorPos(out var cursor))
        {
            var (left, top, _, _) = ToDip(new Rectangle(cursor.X, cursor.Y, 0, 0));
            double ix = Math.Clamp(left + 16, 8, Math.Max(8, ActualWidth - iw - 8));
            double iy = Math.Clamp(top + 16, 8, Math.Max(8, ActualHeight - ih - 8));
            InfoCard.Margin = new Thickness(ix, iy, 0, 0);
        }
        else
        {
            InfoCard.Margin = new Thickness(8, 60, 0, 0);
        }

        InfoCard.MaxWidth = iw;
    }

    /// <summary>光标在顶部提示条 / 它的按钮上时不取目标（否则会高亮遮罩背后的控件）。</summary>
    private bool OverTopBar(DependencyObject? source)
    {
        for (DependencyObject? src = source; src != null; src = VisualTreeHelper.GetParent(src))
            if (ReferenceEquals(src, TopBar)) return true;

        return false;
    }

    /// <summary>
    /// 光标在信息卡上：卡片不吃悬停（它 <c>IsHitTestVisible=False</c>，按它判悬停会和「取到新目标 → 卡片移动」互相触发闪烁），
    /// 但**点在这里不算选中**——卡片下面的控件不是卡片描述的那一个。
    /// </summary>
    private bool OverCard(WPoint at)
    {
        if (InfoCard.Visibility != Visibility.Visible || InfoCard.ActualWidth <= 0) return false;

        WPoint origin = InfoCard.TranslatePoint(new WPoint(0, 0), this);
        return at.X >= origin.X && at.X <= origin.X + InfoCard.ActualWidth &&
               at.Y >= origin.Y && at.Y <= origin.Y + InfoCard.ActualHeight;
    }

    // ============ 收尾：所有出口走同一个幂等 Finish ============

    private void Finish(bool accept)
    {
        if (_finished) return;
        _finished = true;

        // 按下就走（右键取消 / 单击选中）：这一下点击的抬起别漏给下面的程序
        Features.CapturePro.ExitInput.Swallow();

        try { DialogResult = accept; }
        catch { Close(); }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在顶部按钮上时别把它当成「选中控件」（Preview 隧道先到窗口层）；
        // 点在信息卡上也不算选中（卡片下面的控件不是它描述的那一个）。
        if (OverTopBar(e.OriginalSource as DependencyObject)) return;
        if (OverCard(e.GetPosition(this))) return;

        if (_target < 0 || _tree == null) return;

        ControlTarget node = _tree.Nodes[_target];
        if (!ControlTargets.CanTarget(node)) return;   // 信息卡已经写明原因，别静默关掉

        Result = new PickedControl(node.Name, node.AutomationId, node.ControlType,
            _windowClass, _windowTitle);
        Finish(true);
        e.Handled = true;
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        Finish(false);
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Tree_Click(object sender, RoutedEventArgs e)
    {
        if (_window == IntPtr.Zero) return;
        if (_tree == null)
        {
            ShowPending();
            return;
        }

        PickedControl? picked = ControlTreeDialog.Show(this, _window, _tree, _windowTitle, _windowClass);
        if (picked == null) return;

        Result = picked;
        Finish(true);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _target >= 0 && _tree != null &&
                 ControlTargets.CanTarget(_tree.Nodes[_target]))
        {
            ControlTarget node = _tree.Nodes[_target];
            Result = new PickedControl(node.Name, node.AutomationId, node.ControlType,
                _windowClass, _windowTitle);
            Finish(true);
            e.Handled = true;
        }
    }

    /// <summary>keydown 万一被窗口激活吃掉，keyup 再兜一次（Finish 幂等）。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(false);
            e.Handled = true;
        }
    }
}
