using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏工具条收起之后留在屏幕边上的小条（照 Snow Shot 的 <c>RecordingToolbarEdgeControl</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 外观：贴边一侧是**直的**（贴住屏幕边），朝里一侧两个角圆角；里面一个朝里的折线箭头
/// （右侧的条朝左、左侧的条朝右）。悬停变浅蓝底 + 主色描边与箭头，按下变灰底。
/// 度量照上游 <c>paintEvent</c>：圆角 <c>min(6 × scale, 高 / 2)</c>、箭头半宽 2 × scale、
/// 半高 4 × scale、描边 1.7 × scale（圆头）。
/// </para>
/// <para>
/// 三条硬约定与录屏条一致：**不抢焦点**（点它不该把前台从被录的程序抢走）、**不入镜**
/// （否则录制里永远挂着一个小条）、点击立即唤出工具条。窗口是**不透明**的，圆角靠
/// <c>SetWindowRgn</c> 把朝外的两角补成方的、朝里两角抠圆 —— 分层窗口的排除捕获在个别系统上
/// 不生效，红边会进成品（录制区域边框踩过）。
/// </para>
/// </remarks>
internal sealed class RecordAutoHideTab : Window
{
    private const int RgnOr = 2;

    private readonly TabSurface _surface;
    private RecordToolbarSide _side = RecordToolbarSide.Right;
    private IntPtr _hwnd;
    private double _scale = 1;

    public RecordAutoHideTab()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.White;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;
        ToolTip = "显示录屏工具条（悬停也能唤出）";

        _surface = new TabSurface
        {
            ToolTip = ToolTip,
        };
        Content = _surface;

        MouseEnter += (_, _) => _surface.SetHover(true);
        MouseLeave += (_, _) =>
        {
            _surface.SetHover(false);
            _surface.SetPressed(false);
        };

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            _surface.SetPressed(true);
            e.Handled = true;
        };

        PreviewMouseLeftButtonUp += (_, e) =>
        {
            _surface.SetPressed(false);
            if (!IsMouseOver) return;
            e.Handled = true;
            Clicked?.Invoke();
        };

        SizeChanged += (_, _) => ApplyRegion();
    }

    /// <summary>点了小条（工具条该立刻展开）。</summary>
    public event Action? Clicked;

    /// <summary>小条贴在哪一边（决定圆角与箭头朝向）。</summary>
    internal RecordToolbarSide Side => _side;

    /// <summary>现在摆在哪个物理矩形上（收起期间由控制器比对，几何没变就不重摆）。</summary>
    internal Int32Rect PhysicalBounds { get; private set; }

    /// <summary>按物理矩形摆好并显示（<paramref name="scale"/> 只用来算圆角半径）。</summary>
    internal void ShowAt(Int32Rect physical, RecordToolbarSide side, double scale)
    {
        if (physical.Width <= 0 || physical.Height <= 0) return;

        _scale = scale > 0 ? scale : 1;
        _side = side;
        _surface.Side = side;

        bool wasVisible = IsVisible;
        if (!wasVisible)
        {
            _surface.SetHover(false);
            _surface.SetPressed(false);
            Show();
        }

        PhysicalBounds = physical;

        // 先 Show 再落几何：Show 之前窗口还不一定有自己的 DPI 上下文
        Left = physical.X / _scale;
        Top = physical.Y / _scale;
        Width = physical.Width / _scale;
        Height = physical.Height / _scale;

        UpdateLayout();
        ApplyRegion();

        if (_hwnd != IntPtr.Zero) RaiseToTop();
    }

    /// <summary>收起小条（工具条展开 / 整个面板关掉）。</summary>
    internal void HideTab()
    {
        _surface.SetHover(false);
        _surface.SetPressed(false);
        PhysicalBounds = default;
        if (IsVisible) Hide();
    }

    /// <summary>顶回最上面（不改位置尺寸、不抢焦点）。</summary>
    internal void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "置顶录屏收起条失败：" + ex.Message);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
        ApplyRegion();
    }

    /// <summary>窗口 DPI（物理 ÷ DIP）；拿不到就按摆位时给的比例。</summary>
    private double ActualScale()
    {
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;
        return scale > 0 ? scale : _scale;
    }

    /// <summary>
    /// 把窗口区域抠成「贴边方角 + 朝里圆角」：先建一个四角都圆的区域，再用一块盖住贴边半边的
    /// 矩形并上去，那一侧的两角就方了。**不透明窗口 + 区域**才不会被个别系统漏进录制。
    /// </summary>
    private void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;

        try
        {
            double scale = ActualScale();
            int width = Math.Max(1, (int)Math.Round(ActualWidth * scale));
            int height = Math.Max(1, (int)Math.Round(ActualHeight * scale));

            double radiusDip = Math.Min(6, ActualHeight / 2);
            int diameter = Math.Max(1, (int)Math.Round(radiusDip * 2 * scale));

            IntPtr round = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            int half = width / 2;
            IntPtr square = _side == RecordToolbarSide.Left
                ? CreateRectRgn(0, 0, width - half + 1, height + 1)   // 左侧贴边：左边方
                : CreateRectRgn(half, 0, width + 1, height + 1);      // 右侧贴边：右边方

            IntPtr combined = CreateRectRgn(0, 0, 0, 0);
            try
            {
                CombineRgn(combined, round, square, RgnOr);
                SetWindowRgn(_hwnd, combined, true);
                combined = IntPtr.Zero;   // 区域交给系统接管，别自己删
            }
            finally
            {
                DeleteObject(round);
                DeleteObject(square);
                if (combined != IntPtr.Zero) DeleteObject(combined);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "设置录屏收起条形状失败：" + ex.Message);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    /// <summary>
    /// 小条本体：一个圆角面 + 一个朝里的折线箭头（照上游 <c>paintEvent</c> 的路径与颜色）。
    /// </summary>
    private sealed class TabSurface : FrameworkElement
    {
        private static readonly Brush Surface = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush HoverSurface = Freeze(Color.FromRgb(0xE6, 0xF4, 0xFF));
        private static readonly Brush PressedSurface = Freeze(Color.FromRgb(0xF5, 0xF5, 0xF5));
        private static readonly Brush Primary = Freeze(Color.FromRgb(0x16, 0x77, 0xFF));
        private static readonly Brush Border = Freeze(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush Ink = Freeze(Color.FromArgb(0xA6, 0x00, 0x00, 0x00));

        private RecordToolbarSide _side = RecordToolbarSide.Right;
        private bool _hover;
        private bool _pressed;

        public RecordToolbarSide Side
        {
            get => _side;
            set
            {
                if (_side == value) return;
                _side = value;
                InvalidateVisual();
            }
        }

        public void SetHover(bool value)
        {
            if (_hover == value) return;
            _hover = value;
            InvalidateVisual();
        }

        public void SetPressed(bool value)
        {
            if (_pressed == value) return;
            _pressed = value;
            InvalidateVisual();
        }

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            bool left = _side == RecordToolbarSide.Left;
            double scale = width / RecordAutoHide.TabWidth;
            double radius = Math.Min(6 * scale, height / 2);

            var body = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                // 贴边那侧是直边，朝里那侧两个角是圆的
                if (left)
                {
                    ctx.BeginFigure(new Point(body.Left, body.Top), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(body.Right - radius, body.Top), true, false);
                    ctx.QuadraticBezierTo(new Point(body.Right, body.Top),
                        new Point(body.Right, body.Top + radius), true, false);
                    ctx.LineTo(new Point(body.Right, body.Bottom - radius), true, false);
                    ctx.QuadraticBezierTo(new Point(body.Right, body.Bottom),
                        new Point(body.Right - radius, body.Bottom), true, false);
                    ctx.LineTo(new Point(body.Left, body.Bottom), true, false);
                }
                else
                {
                    ctx.BeginFigure(new Point(body.Right, body.Top), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(body.Left + radius, body.Top), true, false);
                    ctx.QuadraticBezierTo(new Point(body.Left, body.Top),
                        new Point(body.Left, body.Top + radius), true, false);
                    ctx.LineTo(new Point(body.Left, body.Bottom - radius), true, false);
                    ctx.QuadraticBezierTo(new Point(body.Left, body.Bottom),
                        new Point(body.Left + radius, body.Bottom), true, false);
                    ctx.LineTo(new Point(body.Right, body.Bottom), true, false);
                }
            }

            geometry.Freeze();

            var fill = _pressed ? PressedSurface : _hover ? HoverSurface : Surface;
            var pen = new Pen(_hover ? Primary : Border, 1);
            pen.Freeze();
            dc.DrawGeometry(fill, pen, geometry);

            // 箭头朝里：左侧的条指向右，右侧的条指向左
            double direction = left ? 1.0 : -1.0;
            double cx = width / 2, cy = height / 2;

            var chevron = new StreamGeometry();
            using (var ctx = chevron.Open())
            {
                ctx.BeginFigure(new Point(cx - direction * 2 * scale, cy - 4 * scale), false, false);
                ctx.LineTo(new Point(cx + direction * 2 * scale, cy), true, false);
                ctx.LineTo(new Point(cx - direction * 2 * scale, cy + 4 * scale), true, false);
            }

            chevron.Freeze();

            var chevronPen = new Pen(_hover ? Primary : Ink, 1.7 * scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            chevronPen.Freeze();

            dc.DrawGeometry(null, chevronPen, chevron);
        }
    }
}
