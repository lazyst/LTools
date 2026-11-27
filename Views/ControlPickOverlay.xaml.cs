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
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 「选控件」遮罩：移动鼠标时高亮光标下的 UI Automation 控件，单击选中；
/// 顶部「从控件树选…」给悬停拿不到的控件（被遮挡 / 折叠页里）留一个列表出口。
/// </summary>
/// <remarks>
/// <para>
/// 命中不用 UIA 的 <c>FromPoint</c>：遮罩自己盖住全屏，只会命中我们自己。改为照截图吸附的
/// 已验证做法——<c>ScreenSnapTargets.TopWindowAtPoint</c> 找光标下的顶层窗口，再沿光标那条支路
/// **懒下钻**（<see cref="SnapProbe"/> + <see cref="ControlSnapSource"/>），节点上带着控件身份。
/// 只走十几层、几十次跨进程调用就够定位，不再像旧实现那样一进窗口就吞下整棵树
/// （深 12 / 1000 节点 / 每个节点 7 个属性，慢应用要等几秒还会被截断）。
/// </para>
/// <para>
/// 命中叶子（Text / Image）时往上抬到最近的可交互父级：点文字中心也点得到按钮，但「设值」
/// 只有 Edit 能做。是否可当目标（名称 / AutomationId 至少一个）、该选哪一层，
/// 都由 <see cref="ControlTargets"/> 的纯函数判断，这里只负责画和收尾。
/// </para>
/// <para>
/// 扫全量的入口只剩两个，都在**用到时才扫**：顶部「从控件树选…」的
/// <see cref="ControlTreeDialog"/> 与 MCP 的 <c>ltools_uia_tree</c>。
/// </para>
/// </remarks>
public partial class ControlPickOverlay : Window
{
    /// <summary>鼠标移动的命中节流（毫秒）：与窗口点选器 / 截图吸附同一口径。</summary>
    private const long MoveThrottleMs = 24;

    /// <summary>同名计数在光标停稳之后才问 provider（换控件就作废上一次）。</summary>
    private static readonly TimeSpan CountDelay = TimeSpan.FromMilliseconds(220);

    private readonly Rectangle _monitor;
    private readonly double _sx, _sy;

    /// <summary>悬停只读「光标那条支路」：与截图吸附共用一套懒下钻，只是节点多带身份。</summary>
    private readonly ControlSnapSource _source = new();
    private readonly SnapProbe _probe;
    private readonly DispatcherTimer _countTimer;

    private IntPtr _window;
    private IntPtr _pending;
    private List<ControlTarget> _pathTargets = new();   // 深 → 浅（最后一条是窗口本身）
    private SnapStop _stop = SnapStop.complete;
    private string _windowTitle = "", _windowClass = "";
    private ControlTarget? _target;
    private int _sameCount;   // 同名控件个数；-1 = 还没查 / 查不准
    private int _countSeq;
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

        _probe = new SnapProbe(_source);
        _countTimer = new DispatcherTimer { Interval = CountDelay };
        _countTimer.Tick += (_, _) => QuerySameCount();

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

    // ============ 悬停：窗口 → 光标那条支路 → 控件 ============

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
            _window = IntPtr.Zero;
            _pathTargets.Clear();
            _stop = SnapStop.complete;
            ClearHighlight();
            return;
        }

        if (hwnd != _window)
        {
            _window = hwnd;
            _windowTitle = WindowFinder.GetText(hwnd);
            _windowClass = WindowFinder.GetClass(hwnd);
            _pathTargets.Clear();
            _stop = SnapStop.pending;
            ClearHighlight();
        }

        // 只读缓存（微秒级）：缺哪层交给后台补，绝不在 UI 线程发跨进程调用
        SnapPath path = _probe.Resolve(hwnd, screen, _monitor, expand: false);
        ApplyPath(path);
        EnsurePath(hwnd, screen, path);
        UpdateTarget();
    }

    /// <summary>把「光标那条支路」换成控件身份列表（深 → 浅，最后一条是窗口本身）。</summary>
    private void ApplyPath(SnapPath path)
    {
        var targets = new List<ControlTarget>(path.Levels.Count);
        foreach (SnapLevel level in path.Levels)
            if (level.Info is ControlTarget target) targets.Add(target);

        _pathTargets = targets;
        _stop = path.Stop;
    }

    /// <summary>
    /// 缺层时在后台补一趟：一次只沿光标那条支路往下，缺几层补几层。
    /// 同一个窗口同时只排一趟；连续没进展的窗口由 <see cref="SnapProbe"/> 标记放弃。
    /// </summary>
    private void EnsurePath(IntPtr hwnd, DPoint screen, SnapPath path)
    {
        if (path.Complete) return;
        if (_pending == hwnd) return;             // 已经有人在补这个窗口了
        if (!_probe.CanExpand(hwnd)) return;      // 读不动的窗口，别每次移动都白排一趟

        _pending = hwnd;
        Rectangle monitor = _monitor;

        Task.Run(() => _probe.Resolve(hwnd, screen, monitor, expand: true)).ContinueWith(_ =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_pending != hwnd) return;
                _pending = IntPtr.Zero;
                if (_window != hwnd) return;

                // 光标可能已经移到别处：还在这个窗口上才接着算（否则交给下一次移动）
                if (!Win32.GetCursorPos(out var cursor)) return;
                var at = new DPoint(cursor.X, cursor.Y);
                if (ScreenSnapTargets.TopWindowAtPoint(new WindowInteropHelper(this).Handle, at) != hwnd)
                    return;

                _lastPoint = at;
                ApplyPath(_probe.Resolve(hwnd, at, monitor, expand: false));
                UpdateTarget();
            }));
        });
    }

    /// <summary>
    /// 从这条支路里挑目标：跳过窗口本身，从最深往上找第一个可交互控件；
    /// 一路都不是可交互的就退回最深那一层（点文字中心同样点得到）。
    /// </summary>
    private void UpdateTarget()
    {
        int index = ControlTargets.PickOnPath(_pathTargets);

        if (index < 0)
        {
            // 空支路 = 光标只是擦到窗口边缘（DWM 可见范围之外），不是「读不到」
            if (_pathTargets.Count == 0 && _stop == SnapStop.complete)
            {
                ClearHighlight();
                return;
            }

            switch (_stop)
            {
                case SnapStop.pending:
                    ShowPending();
                    return;

                case SnapStop.failed:
                    ShowProblem("这个窗口不暴露控件（提权 / 自绘 / 老程序）：改用「鼠标操作」点坐标，或「文字点击」按文字找");
                    return;

                case SnapStop.timeout:
                    ShowProblem("读这个窗口的控件超时：可直接「从控件树选…」，或用坐标 / 文字点击");
                    return;

                default:
                    ShowProblem("这个窗口里没有可用的控件（可能整窗是一个自绘区域）");
                    return;
            }
        }

        ControlTarget node = _pathTargets[index];
        if (_target is { } current && current.Equals(node)) return;   // 同一块别每移动一像素就重画

        _target = node;
        ShowTarget(node);
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

        UpdateWarn(node);
        RequestSameCount(node);

        PlaceCard(left, top, width, height);
    }

    // ============ 同名计数：光标停稳之后问一次 provider ============

    /// <summary>换目标就作废上一次（<c>_countSeq</c> 对不上直接丢），停稳 220 ms 才真去查。</summary>
    private void RequestSameCount(ControlTarget node)
    {
        _sameCount = -1;
        _countSeq++;

        _countTimer.Stop();
        if (ControlTargets.CanTarget(node)) _countTimer.Start();
    }

    private void QuerySameCount()
    {
        _countTimer.Stop();
        if (_target is not { } node) return;

        int seq = _countSeq;
        IntPtr hwnd = _window;

        Task.Run(() => _source.CountSame(hwnd, node)).ContinueWith(t =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq != _countSeq) return;   // 已经换了目标
                _sameCount = t.Status == TaskStatus.RanToCompletion ? t.Result ?? -1 : -1;

                if (_target is { } now && now.Equals(node)) UpdateWarn(now);
            }));
        });
    }

    private void UpdateWarn(ControlTarget node)
    {
        if (!ControlTargets.CanTarget(node))
            SetWarn("这个控件没有名称和 AutomationId，选不了：用「鼠标操作」点坐标，或「文字点击」按文字找");
        else if (_sameCount > 1)
            SetWarn($"窗口里还有 {_sameCount - 1} 个同样的控件；运行时取最先找到的那个");
        else
            SetWarn("");
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
        _target = null;
        _sameCount = -1;
        _countSeq++;
        _countTimer.Stop();

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

    /// <summary>
    /// 唯一的关闭入口。连点 / Esc 连按 / 按钮重复触发只会生效一次。
    /// </summary>
    /// <param name="swallowInput">
    /// 只有键盘出口（Esc / Enter）要吞掉按键抬起；鼠标点按是按下 + 抬起成对结束，
    /// 窗口自己已经收下了抬起，不该再让钩子跨进程去吞（吞错一次就会让某个窗口以为键还按着）。
    /// </param>
    private void Finish(bool accept, bool swallowInput)
    {
        if (_finished) return;
        _finished = true;

        if (swallowInput) Features.CapturePro.ExitInput.Swallow();
        Features.CapturePro.ExitInput.ReleaseMouseCapture();

        try { DialogResult = accept; }
        catch { Close(); }
    }

    /// <summary>左键按下：只吞掉，不关窗（抬起时才确认，按下 + 抬起成对）。</summary>
    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (OverTopBar(e.OriginalSource as DependencyObject)) return;
        if (OverCard(e.GetPosition(this))) return;
        e.Handled = true;
    }

    /// <summary>左键抬起：选中光标下的控件并关闭。</summary>
    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (OverTopBar(e.OriginalSource as DependencyObject)) return;
        if (OverCard(e.GetPosition(this))) return;
        e.Handled = true;

        if (_target is not { } node) return;
        if (!ControlTargets.CanTarget(node)) return;   // 信息卡已经写明原因，别静默关掉

        Result = new PickedControl(node.Name, node.AutomationId, node.ControlType,
            _windowClass, _windowTitle);
        Finish(true, swallowInput: false);
    }

    /// <summary>右键按下：吞掉，等抬起再取消（成对）。</summary>
    private void Window_RightDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        Finish(false, swallowInput: false);
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false, swallowInput: false);

    private void Tree_Click(object sender, RoutedEventArgs e)
    {
        if (_window == IntPtr.Zero) return;

        // 悬停只走了光标那条支路；完整的控件树在这里按需扫一次（打开对话框时才做）
        PickedControl? picked = ControlTreeDialog.Show(this, _window, _windowTitle, _windowClass);
        if (picked == null) return;

        Result = picked;
        Finish(true, swallowInput: false);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(false, swallowInput: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _target is { } node && ControlTargets.CanTarget(node))
        {
            Result = new PickedControl(node.Name, node.AutomationId, node.ControlType,
                _windowClass, _windowTitle);
            Finish(true, swallowInput: true);
            e.Handled = true;
        }
    }

    /// <summary>keydown 万一被窗口激活吃掉，keyup 再兜一次（Finish 幂等）。</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(false, swallowInput: true);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 同名计数是「停稳 220ms 再问」：窗口关了就别再排后台查询
        _countTimer.Stop();
        _countSeq++;
        base.OnClosed(e);
    }
}
