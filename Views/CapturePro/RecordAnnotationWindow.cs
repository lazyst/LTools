using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录制期的标注层：一个**真的覆盖在录制区上**的透明窗口（会被一起录进成品）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不做「合成进帧」：Snow Shot 就是靠一个覆盖窗让标注出现在录像里，用户看到什么、
/// 成品里就是什么；合成层还得自己在后台线程画一遍、与鼠标手势抢状态。这里照它。
/// </para>
/// <para>
/// 窗口**不抢焦点**（<see cref="OverlayShell.MakeNonActivating"/>），鼠标事件照常收；
/// 文字 / 水印要键入，那时才临时弹一次输入框（那本来就是用户在打字的动作）。
/// </para>
/// <para>
/// 内容真源是 <see cref="CaptureDocument"/>（标注 + 撤销栈 + 序号），与截图覆盖层同一套
/// <see cref="Mark"/> / <see cref="MarkRenderer"/>；马赛克 / 滤镜取的是开录那一刻抓的底图。
/// </para>
/// </remarks>
internal sealed class RecordAnnotationWindow : Window
{
    private readonly CaptureDocument _doc;
    private readonly Surface _surface;

    private IntPtr _hwnd;
    private bool _inputActive;

    /// <summary>标注变动（加 / 删 / 撤销 / 重做）：工具条据此刷新撤销与重做按钮。</summary>
    public event Action? Changed;

    internal RecordAnnotationWindow(Int32Rect area, BitmapSource baseImage, double scale)
    {
        double s = scale > 0 ? scale : 1;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Left = area.X / s;
        Top = area.Y / s;
        Width = Math.Max(1, area.Width / s);
        Height = Math.Max(1, area.Height / s);

        _doc = new CaptureDocument(baseImage, area, s);
        _surface = new Surface(this, _doc);
        Content = _surface;
    }

    internal RecordTool Tool
    {
        get => _surface.Tool;
        set
        {
            _surface.Tool = value;
            _surface.Cursor = value switch
            {
                RecordTool.Select => Cursors.Arrow,
                RecordTool.Text => Cursors.IBeam,
                RecordTool.Eraser => Cursors.No,
                _ => Cursors.Cross,
            };
        }
    }

    /// <summary>标注颜色（工具条第二行的色卡写进来）。</summary>
    internal Color Color
    {
        get => _surface.Ink;
        set => _surface.Ink = value;
    }

    /// <summary>
    /// 有没有绘制工具选中：**有 = 收鼠标**（铺一层 alpha 2 的输入面），**没有 = 点击穿透**。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>ScreenRecordingAreaWindow::inputSurfaceColor()</c>：Windows 的分层窗口
    /// **零 alpha 的像素会让鼠标消息穿过去**，所以纯透明的标注层永远收不到点击——
    /// 上游在有工具时给选区铺 <c>QColor(0, 0, 0, 2)</c>，没工具时退回全透明 + <c>WS_EX_TRANSPARENT</c>。
    /// 这里同一套：alpha 2 肉眼看不出来，但足以让系统把点击交给这层。
    /// </remarks>
    internal bool InputActive
    {
        get => _inputActive;
        set
        {
            if (_inputActive == value) return;
            _inputActive = value;
            ApplyInputMode();
            _surface.InvalidateVisual();
        }
    }

    internal bool CanUndo => _doc.CanUndo;
    internal bool CanRedo => _doc.CanRedo;

    internal void Undo()
    {
        _doc.Undo();
        _surface.InvalidateVisual();
        Changed?.Invoke();
    }

    internal void Redo()
    {
        _doc.Redo();
        _surface.InvalidateVisual();
        Changed?.Invoke();
    }

    internal void Clear()
    {
        _doc.ClearMarks();
        _surface.InvalidateVisual();
        Changed?.Invoke();
    }

    internal void CloseQuietly()
    {
        try { Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关标注层失败：" + ex.Message); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: false);
        ApplyInputMode();
    }

    /// <summary>按 <see cref="InputActive"/> 切「收鼠标 / 点击穿透」（<c>WS_EX_TRANSPARENT</c>）。</summary>
    private void ApplyInputMode()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
            long next = _inputActive
                ? ex & ~Win32.WsExTransparent
                : ex | Win32.WsExTransparent;

            if (next == ex) return;

            Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, (IntPtr)next);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNozorder | Win32.SwpNoactivate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "切换标注层输入模式失败：" + ex.Message);
        }
    }

    /// <summary>文字 / 水印要键入：弹一次输入框，确认了才落一条标注。</summary>
    private void AskText(Point at, bool watermark)
    {
        var (ok, value) = InputDialog.Show(this,
            watermark ? "水印文字" : "文字标注",
            watermark ? "要压在录制画面上的水印文字" : "要写进录制的文字",
            watermark ? "LTools" : "", topmost: true);

        if (!ok || string.IsNullOrWhiteSpace(value)) return;

        var mark = watermark
            ? new Mark.Text(at, value.Trim(), new TextSpec("", 34, false, false, TextAlign.Left,
                Color.FromArgb(110, _surface.Ink.R, _surface.Ink.G, _surface.Ink.B),
                0, Colors.White, Colors.Transparent, FillKind.Solid, 0))
            : new Mark.Text(at, value, new TextSpec("", 22, false, false, TextAlign.Left,
                _surface.Ink, 4, Colors.White, Colors.Transparent, FillKind.Solid, 4));

        _doc.Add(mark);
        _surface.InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>画布：只画标注（底图是透明的），手势与截图覆盖层同一套规则。</summary>
    private sealed class Surface : FrameworkElement
    {
        /// <summary>
        /// 输入面：alpha 2 的纯黑（照上游 <c>QColor(0, 0, 0, 2)</c>）。肉眼不可见，
        /// 但分层窗口只有「像素 alpha &gt; 0」的地方才收得到鼠标，所以这一层是画布能落笔的前提。
        /// </summary>
        private static readonly Brush InputSurface = CreateInputSurface();

        private static Brush CreateInputSurface()
        {
            var brush = new SolidColorBrush(Color.FromArgb(2, 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        private readonly RecordAnnotationWindow _owner;
        private readonly CaptureDocument _doc;
        private readonly List<Point> _points = new();

        private Point _start;
        private Mark? _preview;
        private bool _drawing;
        private Mark? _dragMark;
        private int _dragIndex = -1;
        private Point _origin;

        public Surface(RecordAnnotationWindow owner, CaptureDocument doc)
        {
            _owner = owner;
            _doc = doc;

            SnapsToDevicePixels = true;
            Focusable = false;
            Cursor = Cursors.Arrow;
        }

        public RecordTool Tool { get; set; } = RecordTool.Select;

        /// <summary>标注墨色。</summary>
        public Color Ink { get; set; } = Color.FromRgb(0xF5, 0x22, 0x2D);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            // 有绘制工具时先铺输入面（收鼠标）；没工具时整层全透明、点击穿到被录的程序
            if (_owner.InputActive) dc.DrawRectangle(InputSurface, null, new Rect(RenderSize));

            MarkRenderer.DrawAll(dc, _doc.Marks, _doc.Base, _doc.Canvas, _doc.Scale);
            if (_preview != null) MarkRenderer.Draw(dc, _preview, _doc.Base, _doc.Canvas, _doc.Scale);
        }

        // ============ 手势 ============

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            var p = e.GetPosition(this);
            _start = p;

            switch (Tool)
            {
                case RecordTool.Eraser:
                    Erase(p);
                    return;

                case RecordTool.Serial:
                    _doc.Add(new Mark.Serial(p, _doc.NextSerial(),
                        SerialSpec.Default with { Stroke = Ink, Fill = ToolStyle.DefaultFillColor }));
                    Done();
                    return;

                case RecordTool.Text:
                case RecordTool.Watermark:
                    _owner.AskText(p, Tool == RecordTool.Watermark);
                    return;

                case RecordTool.Select:
                    _dragIndex = _doc.HitTest(p, 4);
                    if (_dragIndex < 0) return;
                    _dragMark = _doc.Marks[_dragIndex];
                    _origin = p;
                    _preview = _dragMark;
                    CaptureMouse();
                    return;
            }

            _points.Clear();
            _points.Add(p);
            _drawing = true;
            _preview = Build(p);
            CaptureMouse();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var p = e.GetPosition(this);

            if (_dragMark != null)
            {
                _preview = _dragMark.MoveBy(p - _origin);
                InvalidateVisual();
                return;
            }

            if (!_drawing) return;

            if (Tool is RecordTool.Pen or RecordTool.Highlighter)
            {
                if ((p - _points[^1]).Length >= 1.2) _points.Add(p);
            }

            _preview = Build(p);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            var p = e.GetPosition(this);

            if (_dragMark != null)
            {
                if (_preview != null && _dragIndex >= 0 && (p - _origin).Length >= 1)
                    _doc.ReplaceAt(_dragIndex, _preview);

                _dragMark = null;
                _dragIndex = -1;
                _preview = null;
                ReleaseMouse();
                Done();
                return;
            }

            if (!_drawing) return;

            var mark = Build(p);
            bool big = Tool is RecordTool.Pen or RecordTool.Highlighter
                       || Math.Abs(p.X - _start.X) >= RecordTools.MinimumSize
                       && Math.Abs(p.Y - _start.Y) >= RecordTools.MinimumSize;

            _drawing = false;
            _preview = null;
            ReleaseMouse();

            if (mark != null && big) _doc.Add(mark);
            Done();
        }

        private Mark? Build(Point current)
        {
            double width = Tool == RecordTool.Highlighter ? 14 : 4;

            return Tool switch
            {
                RecordTool.Pen => new Mark.Stroke(_points.ToArray(), Ink, width, false, 1),
                RecordTool.Highlighter => new Mark.Stroke(_points.ToArray(), Ink, width, true, 0.35),
                RecordTool.Rect => new Mark.Shape(_start, current, Ink, width, false, ellipse: false),
                RecordTool.Arrow => new Mark.Arrow(
                    ArrowGeometry.Points(_start, current, ArrowType.Straight),
                    ArrowSpec.Default with
                    {
                        Stroke = Ink, Width = width, Type = ArrowType.Straight,
                        Seed = ShapeInk.NextSeed(),
                    }),
                RecordTool.Filter => new Mark.Filter(_start, current, FilterKind.Blur, 55),
                _ => null,
            };
        }

        private void Erase(Point p)
        {
            int index = _doc.HitTest(p, 4);
            if (index < 0) return;

            _doc.RemoveAt(index);
            Done();
        }

        private void Done()
        {
            ReleaseMouse();
            InvalidateVisual();
            _owner.Changed?.Invoke();
        }

        private void ReleaseMouse()
        {
            try { if (IsMouseCaptured) ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        }
    }
}
