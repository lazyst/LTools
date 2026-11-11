using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LTools.Core;
using LTools.Native;

namespace LTools.Views;

/// <summary>窗口在统一尺寸方案里的类别。每个窗口在 XAML 里声明一次，策略集中在这里。</summary>
public enum WindowSizeMode
{
    /// <summary>没显式声明时按窗口特征推断（过渡期兜底，新窗口应显式写）。</summary>
    Auto,

    /// <summary>
    /// <c>SizeToContent</c> 对话框：按工作区限高 / 限宽 + 把窗口夹回工作区。
    /// 内容区必须走共用 <c>DialogShell</c> 的「标题栏 / 滚动内容 / 常驻按钮栏」三行结构。
    /// </summary>
    Dialog,

    /// <summary>固定尺寸主窗口：非最大化时按工作区夹尺寸与位置；<b>不设 MaxHeight</b>（否则再也最大化不到全屏）。</summary>
    Main,

    /// <summary>全屏遮罩 / 悬浮 / Toast / 鼠标提示：故意占满屏或手动定位，完全不干预。</summary>
    Overlay,
}

/// <summary>
/// 全项目统一的窗口尺寸 / 位置约束（取代旧 <c>DialogSizing</c> 的「Loaded 设一次 MaxHeight」）。
/// </summary>
/// <remarks>
/// <para>
/// 旧机制只限高、**不限位置**：<c>SizeToContent=Height</c> 的对话框在内容动态变高时，
/// WPF 保持 <see cref="Window.Top"/> 不变向下扩展，底部跑出工作区被任务栏挡住，
/// 常驻的「确定 / 取消」按钮栏也跟着看不见。这里在 <c>Loaded / SizeChanged / LocationChanged /
/// StateChanged</c> 每次都按**所在显示器工作区**同时夹尺寸与位置，按钮栏永远可见。
/// </para>
/// <para>
/// <see cref="Mode"/> 用附加属性声明：<c>Dialog</c> 设 MaxHeight/MaxWidth（持久硬约束，
/// <c>SizeToContent</c> 重算时会被夹住，内容区 <c>*</c> 行随之压缩、ScrollViewer 才会出现）；
/// <c>Main</c> 只夹 Width/Height 与位置、不设上限（保住最大化）；<c>Overlay</c> 直接跳过。
/// 没声明的按 <c>AllowsTransparency / Topmost / Manual / SizeToContent</c> 推断。
/// </para>
/// <para>
/// 工作区：<c>GetWindowRect → MonitorFromPoint → GetMonitorInfo(rcWork)</c> 全是物理像素，
/// 用 <c>GetDpiForWindow</c>（优先）/ <c>GetDpiForMonitor</c> 换成 DIP——不再依赖
/// <c>VisualTreeHelper.GetDpi</c>（Loaded 阶段 PerMonitorV2 下可能还没同步，算出的可用高度偏大）。
/// </para>
/// </remarks>
public static class WindowSizing
{
    /// <summary>四周留白，别顶到工作区 / 任务栏边缘。</summary>
    private const double Margin = 24;

    /// <summary>
    /// <c>Main</c> 窗口首次显示时的默认尺寸 = 工作区 × 此比例（0.75 ≈ 屏的 3/4）。
    /// 用户觉得设置 / 动作编辑器等写死的 980×620 偏小、内容显示不全，改按屏幕比例给默认值。
    /// </summary>
    private const double MainFillRatio = 0.75;

    /// <summary>夹取过程会改 Top/Left/Width/Height，触发 SizeChanged/LocationChanged，需防重入。</summary>
    private static bool _busy;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(WindowSizeMode), typeof(WindowSizing),
        new PropertyMetadata(WindowSizeMode.Auto));

    public static WindowSizeMode GetMode(Window window) => (WindowSizeMode)window.GetValue(ModeProperty);
    public static void SetMode(Window window, WindowSizeMode value) => window.SetValue(ModeProperty, value);

    /// <summary>实例级标记：默认尺寸只在窗口首次显示时给一次，之后用户拖大拖小都不再被重置。</summary>
    private static readonly DependencyProperty SizeAppliedProperty = DependencyProperty.RegisterAttached(
        "SizeApplied", typeof(bool), typeof(WindowSizing), new PropertyMetadata(false));

    private static bool GetSizeApplied(Window w) => (bool)w.GetValue(SizeAppliedProperty);
    private static void SetSizeApplied(Window w, bool v) => w.SetValue(SizeAppliedProperty, v);

    /// <summary>在 <c>App.OnStartup</c> 里调用一次，之后新建的窗口自动生效。</summary>
    public static void Attach()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent,
            new RoutedEventHandler(OnSizeChanged));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        window.LocationChanged += (_, _) => Clamp(window);
        window.StateChanged += (_, _) => Clamp(window);
        Clamp(window);
    }

    private static void OnSizeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) Clamp(window);
    }

    /// <summary>
    /// 按窗口所在显示器工作区夹取尺寸与位置。动态内容变高 / 切显示器 / 改缩放后会自动重算，
    /// 也可由调用方主动调一次（如切换触发方式、展开 Expander）。
    /// </summary>
    public static void Clamp(Window window)
    {
        if (_busy) return;
        WindowSizeMode mode = Resolve(window);
        if (mode == WindowSizeMode.Overlay) return;
        if (window.WindowState is WindowState.Maximized or WindowState.Minimized) return;

        Rect work = WorkArea(window);
        if (work.Width < 240 || work.Height < 200) return;   // 拿不到 / 极小屏，别乱动

        _busy = true;
        try
        {
            double maxW = Math.Max(240, work.Width - Margin * 2);
            double maxH = Math.Max(200, work.Height - Margin * 2);

            // Main 首次显示：按工作区比例给一个更大的默认尺寸（见 ApplyDefaultSize）。
            // 放在 Clamp 内可复用 _busy 防重入与工作区计算；SizeApplied 保证只跑一次。
            if (mode == WindowSizeMode.Main)
                ApplyDefaultSize(window, work, maxW, maxH);

            if (mode == WindowSizeMode.Dialog)
            {
                // 持久硬约束：SizeToContent 每次重算窗口高度都会被它夹住，
                // 内容区 * 行被压缩、ScrollViewer 出现、按钮栏留在可视区。
                if (double.IsNaN(window.MaxWidth) || double.IsPositiveInfinity(window.MaxWidth) || window.MaxWidth > maxW)
                    window.MaxWidth = maxW;
                if (double.IsNaN(window.MaxHeight) || double.IsPositiveInfinity(window.MaxHeight) || window.MaxHeight > maxH)
                    window.MaxHeight = maxH;
            }
            else
            {
                // Main：绝不设 MaxHeight/MaxWidth（否则最大化不到全屏、关闭按钮被挤）。
                if (window.Width > maxW) window.Width = maxW;
                if (window.Height > maxH) window.Height = maxH;
            }

            double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            if (double.IsNaN(h) || h <= 0) h = Math.Min(400, maxH);
            if (double.IsNaN(w) || w <= 0) w = Math.Min(400, maxW);

            double minTop = work.Top + Margin;
            double maxTop = Math.Max(minTop, work.Bottom - h - Margin);
            double minLeft = work.Left + Margin;
            double maxLeft = Math.Max(minLeft, work.Right - w - Margin);

            double top = Math.Clamp(window.Top, minTop, maxTop);
            double left = Math.Clamp(window.Left, minLeft, maxLeft);
            if (Math.Abs(top - window.Top) > 0.5) window.Top = top;
            if (Math.Abs(left - window.Left) > 0.5) window.Left = left;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// <c>Main</c> 窗口首次显示时按工作区比例给一个更大的默认尺寸（用户嫌 980×620 偏小）。
    /// 只在首次生效（<see cref="SizeAppliedProperty"/>）：之后用户手动改大小 / 位置不再被重置；
    /// 「不记住」= 不落盘，每个新窗口实例按当前屏幕重新算。
    /// </summary>
    /// <remarks>
    /// <para><c>Dialog</c> / <c>Overlay</c> 不走这里；<c>WindowStartupLocation=Manual</c> 表示已有代码
    /// 显式定位（<c>TextWindowService</c> 记住的位置尺寸、悬浮 / 面板的手动定位），必须尊重、不覆盖。</para>
    /// <para>只放大不缩小：取「工作区 × 比例」与 XAML 声明值、<c>MinWidth/MinHeight</c> 的较大者，
    /// 再夹到工作区上限。改尺寸后 WPF 的 <c>CenterScreen/CenterOwner</c> 不会重新居中，这里主动对齐工作区中心。</para>
    /// </remarks>
    private static void ApplyDefaultSize(Window window, Rect work, double maxW, double maxH)
    {
        if (GetSizeApplied(window)) return;
        SetSizeApplied(window, true);
        if (window.WindowStartupLocation == WindowStartupLocation.Manual) return;

        double Num(double v) => double.IsNaN(v) ? 0 : v;
        double w = Math.Max(work.Width * MainFillRatio,
            Math.Max(Num(window.Width), Num(window.MinWidth)));
        double h = Math.Max(work.Height * MainFillRatio,
            Math.Max(Num(window.Height), Num(window.MinHeight)));
        w = Math.Min(w, maxW);
        h = Math.Min(h, maxH);
        if (w <= 0 || h <= 0) return;

        window.Width = Math.Round(w);
        window.Height = Math.Round(h);

        // CenterOwner 相对宿主窗口居中；CenterScreen 相对工作区居中。两者都夹回工作区，
        // 避免宿主贴近屏幕边缘时新尺寸越界（LocationChanged 会再触发一次 Clamp 兜底）。
        double cx, cy;
        var owner = window.WindowStartupLocation == WindowStartupLocation.CenterOwner
            ? window.Owner : null;
        if (owner != null && owner.ActualWidth > 0 && owner.ActualHeight > 0)
        {
            cx = owner.Left + owner.ActualWidth / 2;
            cy = owner.Top + owner.ActualHeight / 2;
        }
        else
        {
            cx = work.Left + work.Width / 2;
            cy = work.Top + work.Height / 2;
        }
        double left = Math.Clamp(cx - window.Width / 2, work.Left + Margin, work.Right - window.Width - Margin);
        double top = Math.Clamp(cy - window.Height / 2, work.Top + Margin, work.Bottom - window.Height - Margin);
        window.Left = left;
        window.Top = top;
    }

    private static WindowSizeMode Resolve(Window window)
    {
        WindowSizeMode mode = GetMode(window);
        if (mode != WindowSizeMode.Auto) return mode;
        if (window.AllowsTransparency || window.Topmost
            || window.WindowStartupLocation == WindowStartupLocation.Manual)
            return WindowSizeMode.Overlay;
        return window.SizeToContent != SizeToContent.Manual
            ? WindowSizeMode.Dialog : WindowSizeMode.Main;
    }

    /// <summary>窗口所在显示器工作区（DIP）；拿不到才退回主屏。</summary>
    private static Rect WorkArea(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Win32.GetWindowRect(hwnd, out var rect))
            {
                var center = new Win32.Point
                {
                    X = (rect.Left + rect.Right) / 2,
                    Y = (rect.Top + rect.Bottom) / 2,
                };
                IntPtr monitor = Win32.MonitorFromPoint(center, Win32.MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero)
                {
                    var info = new Win32.MonitorInfo { cbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
                    if (Win32.GetMonitorInfo(monitor, ref info))
                    {
                        double scale = 1.0;
                        uint dpi = Win32.GetDpiForWindow(hwnd);
                        if (dpi > 0) scale = dpi / 96.0;
                        else if (Win32.GetDpiForMonitor(monitor, Win32.MdtEffectiveDpi,
                                     out uint _, out uint dy) == 0 && dy > 0)
                            scale = dy / 96.0;
                        if (scale <= 0) scale = 1.0;

                        var r = info.rcWork;
                        return new Rect(r.Left / scale, r.Top / scale,
                            (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("WindowSizing", "取显示器工作区失败，退回主屏：" + ex.Message);
        }
        return SystemParameters.WorkArea;
    }
}
