using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 桌面上的一个悬浮项：一个动作（一个按钮）或一个分组（一排按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 每个按钮都是**正方形**：图标 + 底下名称（照 Quicker，名称超宽截断并挂 ToolTip）。
/// 名称每次 <see cref="Rebuild"/> 现查，改动作名后自动跟随。
/// </para>
/// <para>
/// 置顶、无边框、不进任务栏、**不抢焦点**（<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> +
/// <c>ShowActivated=false</c>）。不抢焦点是硬要求：点击后就要往别的窗口注入键鼠，
/// 一旦悬浮窗自己变成前台，动作会作用在自己身上。
/// </para>
/// <para>
/// 交互与 Quicker 一致：单击运行、拖动移动（松手吸附 / 拖到别的悬浮项上成组）、
/// 右键菜单、中键关闭（可关）。点击与拖动靠 4px 阈值区分，不用双击。
/// </para>
/// </remarks>
public partial class FloatWindow : Window
{
    /// <summary>窗口四周留给阴影的边距（DIP）。</summary>
    internal const double Shadow = 6;

    /// <summary>卡片内边距（DIP），与 XAML 里 <c>MemberHost</c> 的 Margin 一致。</summary>
    internal const double Pad = 3;

    /// <summary>卡片边框宽度（DIP），与 XAML 里 <c>Root</c> 的 BorderThickness 一致。</summary>
    internal const double BorderWidth = 1;

    /// <summary>
    /// 除内容之外的固定占位（DIP）：阴影 6×2 + 内边距 3×2 + 边框 1×2 = 20。
    /// **边框那 2px 别漏**：漏了右边框和下边框正好被窗口裁掉一条。
    /// </summary>
    internal const double Chrome = (Shadow + Pad) * 2 + BorderWidth * 2;

    /// <summary>按钮间距（DIP）。</summary>
    internal const double Gap = 2;

    /// <summary>按钮里名称那一行的高度（DIP）。</summary>
    internal const double LabelHeight = 18;

    /// <summary>名称字号（DIP），与渲染一致（名称按按钮宽度截断，不再参与量宽）。</summary>
    internal const double NameFontSize = 11;

    private const double DragThreshold = 4;

    private readonly FloatingItemDto _item;

    private ContextMenu? _menu;
    private bool _pressed;
    private bool _dragging;
    private bool _rootInteractionAttached;
    private Point _downPoint;
    private string? _downActionId;

    internal FloatWindow(FloatingItemDto item)
    {
        _item = item;
        InitializeComponent();

        Left = item.X;
        Top = item.Y;

        ApplyGeometry();
        Rebuild();
        SourceInitialized += (_, _) => MakeNonActivating();
        IsVisibleChanged += OnVisibilityChanged;
    }

    /// <summary>这个窗口对应的悬浮项 Id。</summary>
    public string ItemId => _item.Id;

    /// <summary>这个窗口对应的悬浮项（只读，改动走 <see cref="FloatingLibrary"/>）。</summary>
    public FloatingItemDto Item => _item;

    // —— 尺寸计算（管理器建窗口 / 定位时也用它，别在别处再算一份） ——

    /// <summary>
    /// 一个按钮的边长（DIP）：**正方形**——图标区 <c>size × size</c> + 底下名称行。
    /// 名称不再参与量宽（否则短名按钮变成竖长方形、长名变宽，一排下来参差不齐），
    /// 超宽就按这个边长截断并挂 ToolTip。
    /// </summary>
    internal static double MemberSide(double size)
    {
        size = Math.Clamp(size, 28, 96);
        return size + LabelHeight;
    }

    /// <summary>
    /// 整个悬浮窗的宽高（DIP）：内容 + 阴影 + 卡片内边距 + 边框。
    /// 横向排一排 / 竖向排一列由 <see cref="FloatingItemDto.Vertical"/> 决定。
    /// </summary>
    internal static (double Width, double Height) MeasureItem(FloatingItemDto item)
    {
        double side = MemberSide(item.Size);
        int count = Math.Max(1, item.Members.Count);
        double span = count * side + (count - 1) * Gap;

        return item.Vertical
            ? (Chrome + side, Chrome + span)
            : (Chrome + span, Chrome + side);
    }

    /// <summary>只有一个按钮的占位尺寸（新建项的默认落点与屏幕夹取）：正方形。</summary>
    internal static (double Width, double Height) Footprint(double size)
    {
        double side = MemberSide(size);
        return (Chrome + side, Chrome + side);
    }

    /// <summary>按当前配置重建按钮（动作改名 / 改图标 / 分组变化后调用）。</summary>
    public void Rebuild()
    {
        CloseMenu();
        MemberHost.Children.Clear();
        ApplyGeometry();

        double size = Math.Clamp(_item.Size, 28, 96);
        double side = MemberSide(size);
        bool tooltip = FloatingLibrary.Prefs.ShowTooltip;
        MemberHost.Orientation = _item.Vertical ? Orientation.Vertical : Orientation.Horizontal;
        int index = 0;

        foreach (var member in _item.Members)
        {
            var action = ActionRegistry.FindById(member.ActionId);
            string name = action?.Name ?? "动作已删除";

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(new TextBlock
            {
                Text = action != null ? IconCatalog.GetGlyph(action.Icon) : "\uE783",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = Math.Round(size * 0.38),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource(action != null ? "TextBrush" : "DangerBrush"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = NameFontSize,
                Height = LabelHeight,
                LineHeight = LabelHeight,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(1, side - 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(action != null ? "SecondaryTextBrush" : "DangerBrush"),
            });

            var btn = new Button
            {
                Width = side,
                Height = side,
                Margin = _item.Vertical
                    ? new Thickness(0, index == 0 ? 0 : Gap, 0, 0)
                    : new Thickness(index == 0 ? 0 : Gap, 0, 0, 0),
                Style = (Style)FindResource("FloatBtn"),
                Tag = member.ActionId,
                Content = stack,
                ToolTip = tooltip ? name : null,
            };

            AttachInteraction(btn, member.ActionId, preview: true);
            MemberHost.Children.Add(btn);
            index++;
        }

        Root.CornerRadius = new CornerRadius(_item.Members.Count > 1 ? 14 : 12);
        Root.PreviewMouseRightButtonUp -= Root_RightButtonUp;
        Root.PreviewMouseRightButtonUp += Root_RightButtonUp;
        Root.MouseDown -= Root_MouseDown;
        Root.MouseDown += Root_MouseDown;

        // 卡片内边距也可拖动；只挂一次（Rebuild 会反复调用）
        if (!_rootInteractionAttached)
        {
            _rootInteractionAttached = true;
            AttachInteraction(Root, null, preview: false);
        }
    }

    /// <summary>把窗口位置同步回配置（拖动结束 / 恢复布局时调用）。</summary>
    public void SyncPosition()
    {
        _item.X = Left;
        _item.Y = Top;
    }

    /// <summary>当前窗口矩形（DIP）。</summary>
    public Rect Bounds => new(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

    // —— 交互 ——

    /// <summary>
    /// 单击 = 运行、拖动 = 移动，靠 4px 阈值区分。
    /// 按钮用 <c>Preview*</c>（要在 ButtonBase 自己处理之前接管），卡片底板用冒泡事件
    /// （按钮会把事件标成 Handled，底板就不会跟着抢）。
    /// </summary>
    private void AttachInteraction(UIElement el, string? actionId, bool preview)
    {
        void OnDown(object s, MouseButtonEventArgs e)
        {
            // 兜底（钩子没装 / 被停用时也能关）：点悬浮窗自己就把右键菜单收掉
            CloseMenu();
            _pressed = true;
            _dragging = false;
            _downActionId = actionId;
            _downPoint = e.GetPosition(this);
            ((UIElement)s).CaptureMouse();
            e.Handled = true;
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!_pressed || _dragging) return;
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _downPoint.X) < DragThreshold &&
                Math.Abs(p.Y - _downPoint.Y) < DragThreshold) return;
            BeginDrag();
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            try { ((UIElement)s).ReleaseMouseCapture(); } catch { /* 已没有捕获 */ }
            e.Handled = true;
            if (_dragging) return;
            RunAction(_downActionId);
        }

        if (preview)
        {
            el.PreviewMouseLeftButtonDown += OnDown;
            el.PreviewMouseMove += OnMove;
            el.PreviewMouseLeftButtonUp += OnUp;
        }
        else
        {
            el.MouseLeftButtonDown += OnDown;
            el.MouseMove += OnMove;
            el.MouseLeftButtonUp += OnUp;
        }
    }

    /// <summary>
    /// 开始拖动。**先把 <c>_dragging</c> 置位再释放捕获**：释放捕获会同步派发挂起的
    /// MouseMove（见 AGENTS.md 拖动排序那条坑），否则会重复进入这里。
    /// </summary>
    private void BeginDrag()
    {
        _dragging = true;
        _pressed = false;
        try { Mouse.Capture(null); } catch { /* 没有捕获 */ }

        if (_item.PositionLocked)
        {
            ToastService.Show("位置已锁定，右键可解锁");
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标已松开（极快的一次拖动） */ }

        FloatingManager.OnDragFinished(this);
    }

    // —— 右键菜单 ——

    private void Root_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenMenu();
        e.Handled = true;
    }

    /// <summary>
    /// 打开右键菜单。**每次现建**，勾选状态永远和配置一致；旧的先关掉，
    /// 避免连点右键堆出多个 popup（每个都挂着自己的 capture，点哪个都没反应）。
    /// </summary>
    private void OpenMenu()
    {
        CloseMenu();

        var menu = FloatingMenus.Build(_item);
        menu.PlacementTarget = Root;
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };

        _menu = menu;
        menu.IsOpen = true;
        AppLog.Info("Floating", $"打开悬浮菜单：{_item.Id}");
    }

    private void CloseMenu()
    {
        var menu = _menu;
        _menu = null;
        if (menu == null) return;
        try { menu.IsOpen = false; } catch { /* 已关闭 */ }
    }

    /// <summary>右键菜单现在是否开着（鼠标钩子的「点外关闭」要用）。</summary>
    internal bool IsMenuOpen => _menu is { IsOpen: true };

    /// <summary>
    /// 把窗口重新顶到 topmost 带的最前面（**不抢焦点**）。
    /// </summary>
    /// <remarks>
    /// 悬浮窗和超级面板都是 <c>Topmost</c>，同一层里**谁最后 <c>SetWindowPos</c> 谁在前面**——
    /// 面板一显示 / 一激活就把悬浮按钮盖住了，而 Quicker 的悬浮按钮永远显示在面板前面。
    /// 所以面板显示、激活之后统一调 <c>FloatingManager.RaiseAllAbove()</c> 把悬浮项顶回去。
    /// </remarks>
    internal void RaiseToTop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Win32.SetWindowPos(hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
        }
        catch { /* 窗口正在关闭 */ }
    }

    /// <summary>
    /// 这个点是否落在开着的菜单里（**物理屏幕像素**，直接跟钩子给的坐标比）。
    /// 用 popup 的窗口矩形而不是 DIP 换算：钩子拿到的是设备像素。
    /// </summary>
    /// <remarks>
    /// **子菜单是另一个 popup 窗口**：只查根菜单矩形的话，点「按钮大小 / 方向 / 拆分为…」的
    /// 子项会被当成「点菜单外面」——钩子先把菜单关掉，这一下点击就丢了，
    /// 表现就是「子菜单里的项点了没反应」。所以要把所有**已展开**的子菜单矩形一起算进来。
    /// </remarks>
    internal bool MenuContains(int screenX, int screenY)
    {
        var menu = _menu;
        if (menu == null || !menu.IsOpen) return false;
        try
        {
            if (ContainsScreenPoint(menu, screenX, screenY)) return true;

            foreach (var item in Descendants(menu.Items))
            {
                if (!item.IsSubmenuOpen) continue;
                if (item.Template?.FindName("PART_Popup", item) is Popup { Child: Visual child }
                    && ContainsScreenPoint(child, screenX, screenY))
                {
                    return true;
                }
            }
        }
        catch { /* 拿不到矩形就当不在菜单里，宁可关掉 */ }
        return false;
    }

    private static IEnumerable<MenuItem> Descendants(ItemCollection items)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in Descendants(item.Items)) yield return child;
        }
    }

    private static bool ContainsScreenPoint(Visual visual, int screenX, int screenY)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source || source.Handle == IntPtr.Zero)
            return false;
        if (!Win32.GetWindowRect(source.Handle, out Win32.Rect rect)) return false;
        return screenX >= rect.Left && screenX < rect.Right
            && screenY >= rect.Top && screenY < rect.Bottom;
    }

    /// <summary>由鼠标钩子调用：点外部时关掉菜单。</summary>
    internal void CloseMenuNow() => CloseMenu();

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && FloatingLibrary.Prefs.MiddleClickClose)
        {
            e.Handled = true;
            FloatingManager.Close(_item.Id);
        }
    }

    private void RunAction(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        var action = ActionRegistry.FindById(actionId);
        if (action == null)
        {
            ToastService.Show("动作不存在：" + actionId);
            FloatingManager.RefreshAll();
            return;
        }

        AppLog.Info("Floating", $"点击悬浮按钮：{action.Name}");
        // 运行只投递到后台（ActionExecutor 自己 spawn），这里是钩子/UI 线程，不能阻塞
        ActionExecutor.Run(action);
    }

    // —— 外观 ——

    private void ApplyGeometry()
    {
        var (width, height) = MeasureItem(_item);
        Width = width;
        Height = height;
        Opacity = Math.Clamp(_item.Opacity, 0.3, 1);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseMenu();
        FloatingManager.ForgetWindow(this);
        base.OnClosed(e);
    }

    /// <summary>窗口被隐藏时菜单必须一起关：popup 留在桌面上会变成点不掉的「孤儿」。</summary>
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) CloseMenu();
    }

    /// <summary>不抢焦点、不进 Alt+Tab：点击悬浮按钮时焦点仍留在目标程序。</summary>
    private void MakeNonActivating()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
            Win32.SetWindowLongPtr(hwnd, Win32.GwlExstyle,
                (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));
        }
        catch { /* 样式加不上也还能显示，只是可能抢焦点 */ }
    }
}
