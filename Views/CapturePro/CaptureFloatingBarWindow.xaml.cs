using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Native;
using System.Windows.Media.Media3D;

namespace LTools.Views.CapturePro;

/// <summary>
/// 悬浮工具条（照 Snow Shot 的 <c>floating_toolbar/*</c>）：常驻桌面的截图入口。
/// </summary>
/// <remarks>
/// <para>
/// 两种模式：<c>icon</c>（默认，平时只露第一个按钮、悬停展开）与 <c>toolbar</c>（一直展开）。
/// 一叠只露第一个按钮，悬停弹出二级工具条（和截图工具条同一套交互）。点按钮走
/// <see cref="ActionExecutor.RunRef"/>，也就是和面板磁贴 / 快捷键 / 悬浮按钮完全同一条路。
/// </para>
/// <para>
/// **不抢焦点**：<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> + <c>ShowActivated=False</c>，
/// 点工具条时焦点仍留在目标程序。拖动落点写进配置（<see cref="FloatingToolbarStore.SavePlacement"/>），
/// 下次打开按它复位。
/// </para>
/// </remarks>
public partial class CaptureFloatingBarWindow : Window
{
    private const double ButtonSide = 34;
    private const double IconModeSide = 38;

    private readonly FloatingToolbarSettings _settings;
    private readonly List<FrameworkElement> _triggers = new();
    private readonly List<Popup> _popups = new();
    private readonly DispatcherTimer _collapse;
    private readonly DispatcherTimer _savePlacement;
    private bool _expanded;
    private bool _restoringPlacement;

    internal CaptureFloatingBarWindow(FloatingToolbarSettings settings, FloatingToolbarLayout layout)
    {
        InitializeComponent();

        _settings = settings;

        _collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _collapse.Tick += (_, _) => { _collapse.Stop(); SetExpanded(false); };

        _savePlacement = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _savePlacement.Tick += (_, _) =>
        {
            _savePlacement.Stop();
            if (!IsLoaded) return;
            FloatingToolbarStore.SavePlacement(Left, Top);
        };

        Build(layout);
        ApplyChrome();

        SourceInitialized += (_, _) => MakeNonActivating();
        // 模态窗口（动作编辑器 / 确认框）会把同进程其它窗口禁掉：悬浮工具条得自己救回来
        StayClickable.Attach(this);
        Loaded += (_, _) => RestorePlacement();
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded || _restoringPlacement) return;
            _savePlacement.Stop();
            _savePlacement.Start();
        };

        MouseEnter += (_, _) =>
        {
            _collapse.Stop();
            Chrome.Opacity = 1.0;
            if (_settings.IsIconMode) SetExpanded(true);
        };
        MouseLeave += (_, _) =>
        {
            Chrome.Opacity = _settings.Opacity / 100.0;
            if (_settings.IsIconMode) { _collapse.Stop(); _collapse.Start(); }
        };

        Chrome.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };
        Chrome.Drop += Chrome_Drop;
    }

    /// <summary>当前这一叠的顺序（冒烟断言用）。</summary>
    internal IReadOnlyList<string> TriggerIds => _triggers.Select(t => (string)t.Tag!).ToList();

    private void Build(FloatingToolbarLayout layout)
    {
        foreach (var stack in layout.Positions)
        {
            if (stack.Count == 0) continue;

            var trigger = MakeButton(stack[0]);
            _triggers.Add(trigger);
            Row.Children.Add(trigger);

            if (stack.Count == 1) continue;

            var rest = stack.Skip(1).Select(MakeButton).ToList();
            var popup = MakeStackPopup(trigger, rest);
            _popups.Add(popup);

            trigger.MouseEnter += (_, _) => popup.IsOpen = true;
            trigger.MouseLeave += (_, _) =>
            {
                // 指针还在弹层里就不收（弹层是独立窗口，IsMouseOver 覆盖不到）
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!popup.IsMouseOver) popup.IsOpen = false;
                };
                timer.Start();
            };
        }

        SetExpanded(!_settings.IsIconMode);
    }

    private Button MakeButton(string reference)
    {
        var glyph = new TextBlock
        {
            Text = IconCatalog.GetGlyph(BindingRef.Icon(reference)),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextBrush"),
        };

        var button = new Button
        {
            Style = (Style)FindResource("BarBtn"),
            Width = ButtonSide,
            Height = ButtonSide,
            Margin = new Thickness(0, 0, 3, 0),
            Content = glyph,
            ToolTip = BindingRef.Name(reference),
            Tag = reference,
        };
        button.Click += (_, _) => Run(reference);
        return button;
    }

    private Popup MakeStackPopup(FrameworkElement trigger, IReadOnlyList<Button> buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 3, 0);
            row.Children.Add(button);
        }

        var border = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Colors.Black,
            },
        };

        var popup = new Popup
        {
            PlacementTarget = trigger,
            Placement = PlacementMode.Custom,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
            IsOpen = false,
        };

        // 与一级图标水平居中、优先在上方（同截图工具条的二级工具条）
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            const double gap = 8;
            double x = (targetSize.Width - popupSize.Width) / 2;
            return new[]
            {
                new CustomPopupPlacement(new Point(x, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(x, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
            };
        };

        foreach (var button in buttons)
            button.Click += (_, _) => popup.IsOpen = false;

        return popup;
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        for (int i = 0; i < _triggers.Count; i++)
            _triggers[i].Visibility = expanded || i == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!expanded) foreach (var popup in _popups) popup.IsOpen = false;

        // 图标模式收起时收紧成一个方块（只留第一个按钮）
        Chrome.Padding = expanded ? new Thickness(5) : new Thickness(4);
        if (!expanded && _settings.IsIconMode)
        {
            Width = IconModeSide;
            Height = IconModeSide;
        }
        else
        {
            Width = double.NaN;
            Height = double.NaN;
        }
    }

    private void ApplyChrome()
    {
        Chrome.Opacity = _settings.Opacity / 100.0;
        Topmost = true;
    }

    private void Run(string reference)
    {
        int x = 0, y = 0;
        if (Win32.GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
        }

        ActionExecutor.RunRef(reference, x, y, RunSource.Floating);
    }

    private void RestorePlacement()
    {
        _restoringPlacement = true;
        try
        {
            var placement = FloatingToolbarStore.Placement;
            var work = WorkArea();

            double left = placement?.Left ?? work.Left + (work.Width - ActualWidth) / 2;
            double top = placement?.Top ?? work.Bottom - ActualHeight - 48;

            Left = Clamp(left, work.Left, work.Right - ActualWidth);
            Top = Clamp(top, work.Top, work.Bottom - ActualHeight);
        }
        finally { _restoringPlacement = false; }
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Min(Math.Max(value, min), max);

    private static Rect WorkArea()
    {
        // 主显示器工作区（DIP）：SystemParameters 给的已经是 DIP，和窗口的 Left/Top 同一套坐标
        return SystemParameters.WorkArea;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开（DragMove 要求左键按着），忽略
        }

        // 拖完夹回工作区并记落点
        var work = WorkArea();
        Left = Clamp(Left, work.Left, work.Right - ActualWidth);
        Top = Clamp(Top, work.Top, work.Bottom - ActualHeight);
        FloatingToolbarStore.SavePlacement(Left, Top);
    }

    private void Chrome_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        var files = dropped.Where(PinFiles.IsSupported).ToList();
        if (files.Count == 0) return;

        var images = files.Where(PinFiles.IsImage).ToList();

        switch (FloatingToolbarStore.DropBehaviorOf(_settings.DropBehavior))
        {
            case FloatingToolbarSettings.DropStitch:
                if (images.Count > 0) CollageSession.OpenFiles(images);
                break;

            case FloatingToolbarSettings.DropSplit:
                if (images.Count > 0) SplitSession.OpenFiles(images);
                break;

            case FloatingToolbarSettings.DropEdit:
                if (images.Count > 0) ImageEditSession.OpenFiles(images);
                break;

            default:
                PinFiles.PinPaths(files);
                break;
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>不抢焦点、不进 Alt+Tab。</summary>
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
        catch { /* 加不上也还能显示，只是可能抢焦点 */ }
    }
}
