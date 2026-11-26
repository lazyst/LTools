using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 点击穿透时贴在贴图旁边的控制栏：不透明度胶囊 + 移动按钮 + 退出按钮。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>ensureClickThroughExitButton</c>：三个控件是**独立于贴图的顶层小窗**（贴图本身已经
/// <c>WS_EX_TRANSPARENT</c>，点不到），贴图旁边按 <see cref="PinClickThrough.Layout"/> 摆，
/// 贴图移动 / 缩放时跟着挪；点退出按钮才恢复可交互。
/// </para>
/// <para>
/// **一个窗口承载三个控件**：上游是三个 HWND，这里是一块 232×32 的分层窗口，控件之间那 8px
/// 间隙不画任何东西——分层窗口里全透明的像素本来就不吃鼠标，点上去等于点在桌面上，效果一致。
/// 少两个 HWND 换来的是「移动 / 隐藏 / 关窗」只需跟着一个窗口走。
/// </para>
/// <para>
/// 不抢焦点（<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> + <c>ShowActivated=false</c>），
/// 移动按钮只把「光标走了多远」交给贴图，自己不改贴图的坐标。
/// </para>
/// </remarks>
internal sealed class PinClickThroughBar : Window
{
    /// <summary>胶囊底色：主题 <c>colorBgMask</c>（上游兜底 <c>rgba(0,0,0,115)</c>）。</summary>
    private static readonly Brush MaskBrush = ThemeBrush.Frozen(Color.FromArgb(115, 0, 0, 0));

    /// <summary>圆按钮悬停 / 按下的底色（主题 primary / primaryActive）。</summary>
    private static readonly Brush HoverBrush = ThemeBrush.Frozen(Color.FromRgb(0x16, 0x77, 0xFF));

    private static readonly Brush PressedBrush = ThemeBrush.Frozen(Color.FromRgb(0x09, 0x58, 0xD9));

    /// <summary>滑条未走过的一段（白 31%）。</summary>
    private static readonly Brush RailBrush = ThemeBrush.Frozen(Color.FromArgb(80, 255, 255, 255));

    private readonly OpacitySlider _slider;
    private readonly Border _moveButton;
    private readonly Border _exitButton;

    private IntPtr _hwnd;
    private bool _locked;

    /// <summary>滑条被拖动（0–100）。</summary>
    public event Action<int>? OpacityChanged;

    /// <summary>点了退出按钮（贴图据此退出穿透态）。</summary>
    public event Action? ExitClicked;

    /// <summary>移动按钮按下（参数是光标屏幕位置，DIP）。</summary>
    public event Action<Point>? MoveStarted;

    /// <summary>移动按钮拖动中（参数是光标屏幕位置，DIP）。</summary>
    public event Action<Point>? MoveMoved;

    /// <summary>移动按钮松开。</summary>
    public event Action? MoveEnded;

    public PinClickThroughBar()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsTabStop = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        Width = PinClickThrough.TotalWidth;
        Height = PinClickThrough.ControlHeight;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            // 用**程序集限定**的 pack URI：这块控件栏也会被仓库外的无头冒烟
            // （另一个 exe 反射加载 LTools.dll）造出来，相对 URI 会按宿主的程序集找、找不到。
            Source = new Uri("pack://application:,,,/LTools;component/Views/CapturePro/CaptureIcons.xaml"),
        });

        var root = new Canvas
        {
            Width = PinClickThrough.TotalWidth,
            Height = PinClickThrough.ControlHeight,
            Background = null,
        };
        Content = root;

        // 不透明度胶囊：152×32 圆角胶囊，12 / 24 图标 / 8 / 96 滑条 / 12
        var capsule = new Border
        {
            Width = PinClickThrough.OpacityEditorWidth,
            Height = PinClickThrough.ControlHeight,
            CornerRadius = new CornerRadius(PinClickThrough.ControlHeight / 2),
            Background = MaskBrush,
            ToolTip = "不透明度",
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(OpacityIcon());
        _slider = new OpacitySlider
        {
            Margin = new Thickness(8, 0, 0, 0),
        };
        _slider.ValueChanged += percent => OpacityChanged?.Invoke(percent);
        row.Children.Add(_slider);
        capsule.Child = row;
        Canvas.SetLeft(capsule, 0);
        Canvas.SetTop(capsule, 0);
        root.Children.Add(capsule);

        // 移动 / 退出：32×32 圆形按钮，图标 18 白色
        _moveButton = RoundButton("IconMove", "移动窗口", Cursors.SizeAll);
        Canvas.SetLeft(_moveButton, PinClickThrough.MoveLeft);
        Canvas.SetTop(_moveButton, 0);
        root.Children.Add(_moveButton);

        _exitButton = RoundButton("IconMouse", "退出点击穿透", Cursors.Hand);
        Canvas.SetLeft(_exitButton, PinClickThrough.ExitLeft);
        Canvas.SetTop(_exitButton, 0);
        root.Children.Add(_exitButton);

        _exitButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ExitClicked?.Invoke();
        };

        _moveButton.MouseLeftButtonDown += MoveDown;
        _moveButton.MouseMove += MoveMove;
        _moveButton.MouseLeftButtonUp += MoveUp;

        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: false);
        };
    }

    /// <summary>当前滑条值（0–100）。</summary>
    public int Percent => _slider.Value;

    /// <summary>只改显示，不触发 <see cref="OpacityChanged"/>（恢复记录 / 贴图同步用）。</summary>
    public void SetOpacity(int percent) => _slider.SetPercent(PinClickThrough.NormalizeOpacity(percent), silent: true);

    /// <summary>锁定态：移动按钮变成普通箭头（照上游 <c>m_lockedMode ? ArrowCursor : SizeAllCursor</c>）。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        _moveButton.Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    /// <summary>把控制栏摆到屏幕上的 <paramref name="origin"/>（DIP）并提到最前，不抢焦点。</summary>
    public void Place(Point origin)
    {
        Left = origin.X;
        Top = origin.Y;
        RaiseToTop();
    }

    /// <summary>置顶跟着贴图走（贴图切换「置顶」时同步）。</summary>
    public void SetTopmost(bool on)
    {
        Topmost = on;
        RaiseToTop();
    }

    /// <summary>提到同层最前面（贴图 / 面板激活之后调，别让贴图盖住自己的按钮）。</summary>
    public void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        Win32.SetWindowPos(_hwnd, Topmost ? Win32.HwndTopmost : IntPtr.Zero,
            0, 0, 0, 0, Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }

    // ============ 圆按钮 ============

    private Border RoundButton(string icon, string tip, Cursor cursor)
    {
        var button = new Border
        {
            Width = PinClickThrough.ControlHeight,
            Height = PinClickThrough.ControlHeight,
            CornerRadius = new CornerRadius(PinClickThrough.ControlHeight / 2),
            Background = MaskBrush,
            Cursor = cursor,
            ToolTip = tip,
            Child = new Grid { Children = { MakeIcon(icon, 18) } },
        };

        button.MouseEnter += (_, _) => button.Background = HoverBrush;
        button.MouseLeave += (_, _) => button.Background = MaskBrush;
        button.MouseLeftButtonDown += (_, _) => button.Background = PressedBrush;
        button.MouseLeftButtonUp += (_, _) => button.Background = button.IsMouseOver ? HoverBrush : MaskBrush;
        return button;
    }

    // ============ 移动按钮 ============

    private void MoveDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _moveButton.CaptureMouse();
        MoveStarted?.Invoke(ScreenDip());
    }

    private void MoveMove(object sender, MouseEventArgs e)
    {
        if (!_moveButton.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

        e.Handled = true;
        MoveMoved?.Invoke(ScreenDip());
    }

    private void MoveUp(object sender, MouseButtonEventArgs e)
    {
        if (!_moveButton.IsMouseCaptured) return;

        e.Handled = true;
        _moveButton.ReleaseMouseCapture();
        MoveEnded?.Invoke();
    }

    /// <summary>光标位置换成屏幕 DIP（与贴图 <c>Left</c> / <c>Top</c> 同一套）。</summary>
    private Point ScreenDip()
    {
        if (!Win32.GetCursorPos(out var pt)) return new Point(Left, Top);

        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            if (tfd.M11 > 0) sx = tfd.M11;
            if (tfd.M22 > 0) sy = tfd.M22;
        }

        return new Point(pt.X * sx, pt.Y * sy);
    }

    // ============ 图标 ============

    /// <summary>不透明度图标：水滴轮廓（描边）+ 下半截（实心）两块拼起来，照上游 SVG 的画法。</summary>
    private FrameworkElement OpacityIcon()
    {
        // 上游 {24, 18, 1.0}：24 的图标槽里放 18 的图标
        var host = new Grid { Width = 24, Height = 24 };
        host.Children.Add(MakeIcon("IconOpacity", 18, stroke: 80.64));
        host.Children.Add(MakeIcon("IconOpacityFill", 18));
        return host;
    }

    /// <summary>
    /// 画一个图标：几何是 1024 的 viewBox，整个 viewBox 等比装进 <paramref name="size"/> 的方框
    /// （= 上游 <c>IconFit::Contain</c>）。外面套 <see cref="Viewbox"/> 而不是给 Path 设
    /// <c>Stretch=Uniform</c>：后者按**几何自身的包围盒**撑满，图标会偏大、跑到格子角上。
    /// </summary>
    /// <param name="stroke">大于 0 = 只描边（宽度用 viewBox 单位，照上游 <c>stroke-width</c>）。</param>
    private FrameworkElement MakeIcon(string key, double size, double stroke = 0)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(key),
            Stretch = Stretch.None,
            Width = 1024,
            Height = 1024,
            IsHitTestVisible = false,
        };

        if (stroke > 0)
        {
            path.Stroke = Brushes.White;
            path.StrokeThickness = stroke;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }
        else
        {
            path.Fill = Brushes.White;
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = path,
        };
    }

    // ============ 不透明度滑条 ============

    /// <summary>
    /// 96×24 的白滑条：4px 轨道（未走过的一段白 31%）+ 白色填充 + 12px 白色滑块。
    /// </summary>
    /// <remarks>
    /// 上游用的是 <c>AdSlider</c> 换了一套白色 token；这里自绘，行为一致：
    /// 在轨道上按下即跳到该处、拖动连续跟手、值 0–100、悬停提示显示百分比。
    /// </remarks>
    private sealed class OpacitySlider : Grid
    {
        private const double ThumbSize = 12;
        private const double RailHeight = 4;

        private readonly Border _fill;
        private readonly Ellipse _thumb;
        private int _value = PinClickThrough.DefaultOpacity;

        public event Action<int>? ValueChanged;

        public OpacitySlider()
        {
            Width = 96;
            Height = 24;
            Background = Brushes.Transparent;

            Children.Add(new Border
            {
                Height = RailHeight,
                CornerRadius = new CornerRadius(RailHeight / 2),
                Background = RailBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            _fill = new Border
            {
                Height = RailHeight,
                CornerRadius = new CornerRadius(RailHeight / 2),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Children.Add(_fill);

            _thumb = new Ellipse
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Children.Add(_thumb);

            MouseLeftButtonDown += (_, e) =>
            {
                CaptureMouse();
                e.Handled = true;
                SetFromPointer(e);
            };
            MouseMove += (_, e) =>
            {
                if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                e.Handled = true;
                SetFromPointer(e);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!IsMouseCaptured) return;
                e.Handled = true;
                SetFromPointer(e);
                ReleaseMouseCapture();
            };

            SizeChanged += (_, _) => Apply();
            Apply();
        }

        public int Value => _value;

        public void SetPercent(int percent, bool silent)
        {
            int next = PinClickThrough.NormalizeOpacity(percent);
            if (next == _value) return;

            _value = next;
            Apply();
            if (!silent) ValueChanged?.Invoke(_value);
        }

        private void SetFromPointer(MouseEventArgs e)
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double usable = Math.Max(1, width - ThumbSize);
            double ratio = Math.Clamp((e.GetPosition(this).X - ThumbSize / 2) / usable, 0, 1);
            SetPercent((int)Math.Round(ratio * 100), silent: false);
        }

        private void Apply()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double usable = Math.Max(1, width - ThumbSize);
            double ratio = _value / 100.0;

            _fill.Width = ThumbSize / 2 + usable * ratio;
            _thumb.Margin = new Thickness(usable * ratio, 0, 0, 0);
            ToolTip = _value + "%";
        }
    }
}
