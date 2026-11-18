using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 标注手势：拖一类（画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 马赛克 / 滤镜 / 聚光）、
/// 点一类（序号 / 文字 / 折线）、擦除，「选择」工具下的移动，以及拖手柄缩放。
/// </summary>
/// <remarks>
/// 预览一律画在 <see cref="CaptureCanvas.Preview"/>，只在松手（或结束折线）时写进文档；
/// 拖动已有标注 / 拉手柄时用 <see cref="CaptureCanvas.SkipIndex"/> 把原件藏起来、用预览代画，
/// 松手才 <c>ReplaceAt</c>——这样拖动过程不会往撤销栈里塞几十帧。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private Point _toolStart;
    private readonly List<Point> _freehand = new();
    private readonly List<Point> _polyPoints = new();
    private bool _polyActive;

    /// <summary>本次拖动的抖动种子：一次手势里每帧重画都一致（松手落盘也是同一条）。</summary>
    private uint _dragSeed = ShapeInk.NextSeed();

    private Mark? _dragMark;
    private int _dragMarkIndex = -1;

    /// <summary>按下时若是「工具手势」就吃掉这次按下（返回 true）。</summary>
    private bool BeginToolGesture(Point p, int clickCount)
    {
        switch (_tool)
        {
            case CaptureTool.Select:
                return false;

            case CaptureTool.Eraser:
                _drag = DragMode.Erase;
                EraseAt(p);
                return true;

            case CaptureTool.RectEraser:
                _toolStart = p;
                _drag = DragMode.Draw;
                Surface.Preview = new Mark.Highlight(p, p, Color.FromRgb(0x3B, 0x82, 0xF6), 0.18);
                Surface.InvalidateVisual();
                return true;

            case CaptureTool.BrushEraser:
                _toolStart = p;
                _freehand.Clear();
                _freehand.Add(p);
                _drag = DragMode.Draw;
                UpdateBrushErasePreview();
                return true;

            case CaptureTool.AutoFilter:
                return BeginAutoFilterGesture(p);

            case CaptureTool.Serial:
                AddSerialAt(p);
                return true;

            case CaptureTool.Text:
                OpenTextEntry(p);
                return true;

            case CaptureTool.Polyline:
                AddPolylinePoint(p, clickCount);
                return true;

            case CaptureTool.Angle:
                return BeginAngle(p);

            case CaptureTool.Watermark:
                _doc.Add(WatermarkMark);
                RefreshMarks();
                return true;

            default:
                _toolStart = p;
                _freehand.Clear();
                _freehand.Add(p);
                _drag = DragMode.Draw;
                _dragSeed = ShapeInk.NextSeed();
                Surface.Preview = BuildDragMark(p);
                Surface.InvalidateVisual();
                return true;
        }
    }

    /// <summary>
    /// 落一个序号：用参数行里那个「下一个序号」，落完自动 +1
    /// （上游 palette 的序号输入框就是这个语义：框里显示的是下一次要用的数字）。
    /// </summary>
    private void AddSerialAt(Point p)
    {
        int number = Math.Max(1, _style.SerialNumber);
        _doc.Add(new Mark.Serial(p, number, _style.SerialSpecOf()));
        _style.SerialNumber = number + 1;
        Toolbar.RefreshValues();
        RefreshMarks();
    }

    /// <summary>拖动中：手绘补点，形状只挪终点。</summary>
    private void UpdateToolDrag(Point p)
    {
        if (_tool == CaptureTool.AutoFilter)
        {
            UpdateAutoFilterGesture(p);
            return;
        }

        if (ToolCatalog.IsFreehand(_tool))
        {
            Point last = _freehand[^1];
            if ((p - last).Length >= 1.2) _freehand.Add(p);
        }

        if (_tool == CaptureTool.BrushEraser) UpdateBrushErasePreview();
        else Surface.Preview = BuildDragMark(p);
        Surface.InvalidateVisual();
    }

    /// <summary>松手：够大才落盘（点一下不留下 0 像素的框）。</summary>
    private void FinishToolDrag(Point p)
    {
        if (_tool == CaptureTool.AutoFilter)
        {
            FinishAutoFilterGesture(p);
            return;
        }

        if (_tool == CaptureTool.Angle)
        {
            FinishAngle(p);
            return;
        }

        if (_tool is CaptureTool.RectEraser or CaptureTool.BrushEraser)
        {
            FinishEraseGesture(p);
            return;
        }

        var mark = BuildDragMark(p);
        bool big = ToolCatalog.IsFreehand(_tool)
            ? _freehand.Count > 0
            : ToolCatalog.IsLinear(_tool)
                ? (p - _toolStart).Length >= ToolCatalog.MinimumSize(_tool)   // 直线 / 测距 / 括号 / 箭头要有长度，不要求两个方向都动
                : Math.Abs(p.X - _toolStart.X) >= ToolCatalog.MinimumSize(_tool)
                  && Math.Abs(p.Y - _toolStart.Y) >= ToolCatalog.MinimumSize(_tool);

        _drag = DragMode.None;
        Surface.Preview = null;

        if (mark != null && big) _doc.Add(mark);
        RefreshMarks();
    }

    private Mark? BuildDragMark(Point current)
    {
        var color = _style.Color;
        double width = _style.WidthFor(_tool);

        switch (_tool)
        {
            case CaptureTool.Pen:
                return new Mark.Stroke(_freehand.ToArray(), color, width, false, 1);

            case CaptureTool.Highlighter:
                return new Mark.Stroke(_freehand.ToArray(), color, width, true, _style.Opacity);

            case CaptureTool.Shape:
                return new Mark.Shape(_toolStart, current, _style.ShapeSpecOf(_dragSeed));

            case CaptureTool.Arrow:
                return new Mark.Arrow(_toolStart, current, color, width);

            case CaptureTool.Line:
                return new Mark.Line(_toolStart, current, color, width, _style.StrokeStyle,
                    _style.LineType, _style.Rendering, _dragSeed);

            case CaptureTool.Distance:
                return new Mark.Distance(_toolStart, current, color, width, _style.DistanceSpecOf());

            case CaptureTool.Bracket:
                return new Mark.Bracket(_toolStart, current, color, width);

            case CaptureTool.SpeechBubble:
                return new Mark.SpeechBubble(MarkGeometry.RectOf(_toolStart, current),
                    _style.ShapeSpecOf(_dragSeed));

            case CaptureTool.Magnifier:
                return new Mark.Magnifier(MarkGeometry.RectOf(_toolStart, current), _style.MagnifierSpecOf());

            case CaptureTool.HighlightRect:
                return new Mark.Highlight(_toolStart, current, color, _style.Opacity);

            case CaptureTool.BrushFilter:
                return new Mark.BrushFilter(_freehand.ToArray(), width, _style.FilterMode, _style.FilterStrength);

            case CaptureTool.Mosaic:
                return new Mark.Mosaic(_toolStart, current, _style.Block);

            case CaptureTool.Filter:
                return new Mark.Filter(_toolStart, current, _style.FilterMode, _style.FilterStrength);

            case CaptureTool.Spotlight:
                return new Mark.Spotlight(_toolStart, current, _style.Dim);

            case CaptureTool.Angle:
                return _angleArmed
                    ? Mark.Angle.FromArms(_angleVertex, _angleArm1, current, color, width, _style.AngleSpecOf())
                    : new Mark.Line(_angleVertex, current, color, width, false);

            default:
                return null;
        }
    }

    // ============ 测角：先一条臂，再第二条 ============

    private Point _angleVertex;
    private Point _angleArm1;
    private bool _angleArmed;

    private bool BeginAngle(Point p)
    {
        _toolStart = p;
        _freehand.Clear();
        _freehand.Add(p);
        _drag = DragMode.Draw;
        _dragSeed = ShapeInk.NextSeed();

        if (!_angleArmed) _angleVertex = p;
        Surface.Preview = BuildDragMark(p);
        Surface.InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 第一次松手只记住「顶点 + 第一条臂」，第二次松手才落成一个角。
    /// 这样手势与其它拖拽工具一致（按下 → 拖 → 松手），不需要点三下。
    /// </summary>
    private void FinishAngle(Point p)
    {
        var color = _style.Color;
        double width = _style.WidthFor(CaptureTool.Angle);

        if (!_angleArmed)
        {
            if ((p - _angleVertex).Length < 6)
            {
                _drag = DragMode.None;
                Surface.Preview = null;
                Surface.InvalidateVisual();
                return;
            }

            _angleArm1 = p;
            _angleArmed = true;
            _drag = DragMode.None;
            Surface.Preview = null;
            Surface.InvalidateVisual();
            return;
        }

        var mark = Mark.Angle.FromArms(_angleVertex, _angleArm1, p, color, width, _style.AngleSpecOf());
        _angleArmed = false;
        _drag = DragMode.None;
        Surface.Preview = null;
        _doc.Add(mark);
        RefreshMarks();
    }

    /// <summary>滚轮调当前角的角度（Shift = 1°，否则 5°）：只换张角，方位与臂长不动。</summary>
    private bool AdjustAngleByWheel(int delta)
    {
        int index = _selectedIndex;
        if (index < 0 || index >= _doc.Marks.Count || _doc.Marks[index] is not Mark.Angle angle)
        {
            // 没选中就找最近画的那个角：用户刚画完就滚轮是最常见的用法
            for (int i = _doc.Marks.Count - 1; i >= 0; i--)
                if (_doc.Marks[i] is Mark.Angle)
                {
                    index = i;
                    angle = (Mark.Angle)_doc.Marks[i];
                    break;
                }
        }

        if (index < 0 || _doc.Marks[index] is not Mark.Angle current) return false;

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;
        double degrees = Math.Clamp(Math.Round(current.Degrees + (delta > 0 ? step : -step)), 1, 359);
        if (Math.Abs(degrees - current.Degrees) < 0.1) return true;

        _doc.ReplaceAt(index, current.WithDegrees(degrees));
        SelectMark(index);
        RefreshMarks();
        return true;
    }

    // ============ 矩形橡皮 / 画笔橡皮 ============

    private void UpdateBrushErasePreview()
    {
        Surface.Preview = new Mark.Stroke(_freehand.ToArray(),
            Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), _style.WidthFor(CaptureTool.BrushEraser), false, 1);
    }

    /// <summary>
    /// 橡皮类落一条「恢复原图」块（对齐上游 <c>SnowCanvasTool::RectangleEraser</c> /
    /// <c>BrushEraser</c> 落的 <c>RestoreBackground</c> 滤镜）：把这块的像素还原成截图原样，
    /// 盖掉它下面的标注 / 滤镜 / 打码。**不是按对象删除**——那是点选橡皮
    /// （<see cref="CaptureTool.Eraser"/>）的事。恢复块本身不可选、不可删，只能 Ctrl+Z 撤销。
    /// </summary>
    private void FinishEraseGesture(Point p)
    {
        Mark? mark = null;

        if (_tool == CaptureTool.RectEraser)
        {
            var rect = MarkGeometry.RectOf(_toolStart, p);
            if (rect.Width >= 1 && rect.Height >= 1) mark = new Mark.Restore(_toolStart, p);
        }
        else if (_freehand.Count > 0)
        {
            mark = new Mark.Restore(_freehand[0], _freehand[^1], _freehand.ToArray(),
                _style.WidthFor(CaptureTool.BrushEraser));
        }

        _drag = DragMode.None;
        Surface.Preview = null;
        _freehand.Clear();
        SelectMark(-1);
        if (mark != null) _doc.Add(mark);
        RefreshMarks();
    }

    // ============ 选择：拖动已有标注 ============

    private void BeginMarkDrag(Point p, int index)
    {
        _dragMarkIndex = index;
        _dragMark = _doc.Marks[index];
        _origin = p;
        _drag = DragMode.Mark;
        SelectMark(index);
        Surface.SkipIndex = index;
        Surface.InvalidateVisual();
    }

    private void UpdateMarkDrag(Point p)
    {
        if (_dragMark == null) return;
        Surface.Preview = _dragMark.MoveBy(p - _origin);
        Surface.InvalidateVisual();
    }

    private void CommitMarkDrag(Point p)
    {
        var preview = Surface.Preview;
        Surface.Preview = null;
        Surface.SkipIndex = -1;

        if (preview != null && _dragMarkIndex >= 0 && (p - _origin).Length >= 1)
            _doc.ReplaceAt(_dragMarkIndex, preview);

        _dragMark = null;
        _dragMarkIndex = -1;
        RefreshMarks();
    }

    private void CancelMarkDrag()
    {
        Surface.Preview = null;
        Surface.SkipIndex = -1;
        _dragMark = null;
        _dragMarkIndex = -1;
        Surface.InvalidateVisual();
    }

    // ============ 选择：拖手柄改大小 ============

    private Mark? _resizeMark;
    private int _resizeMarkIndex = -1;
    private Rect _resizeFrame;               // 按下时的选中框（已含 FramePad）
    private FrameHandle _resizeHandle;

    private void BeginMarkResize(Point p, FrameHandle handle)
    {
        _resizeMarkIndex = _selectedIndex;
        _resizeMark = _doc.Marks[_selectedIndex];
        _resizeFrame = MarkGeometry.PaddedFrame(_resizeMark.Frame);
        _resizeHandle = handle;
        _origin = p;
        _drag = DragMode.MarkResize;
        Surface.SkipIndex = _resizeMarkIndex;   // 原件藏起来，由预览代画（松手才提交，不刷撤销栈）
        Surface.InvalidateVisual();
    }

    /// <summary>
    /// 把手拖到哪 → 新的选中框（收回 FramePad 那几像素）→ 让标注自己按比例变形。
    /// 最小尺寸按「收回之后还剩 4 DIP」算，别把标注拖成一条看不见的线。
    /// </summary>
    private void UpdateMarkResize(Point p)
    {
        if (_resizeMark == null) return;

        var limit = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        Rect padded = MarkGeometry.ResizeFrame(_resizeFrame, _resizeHandle, p - _origin,
            MarkGeometry.FramePad * 2 + 4, limit);
        Rect target = MarkGeometry.Inflate(padded, -MarkGeometry.FramePad);

        Surface.Preview = _resizeMark.ScaleTo(_resizeMark.Frame, target);
        Surface.InvalidateVisual();
    }

    private void CommitMarkResize(Point p)
    {
        UpdateMarkResize(p);                    // 保证预览就是松手那一下的形状

        var preview = Surface.Preview;
        Surface.Preview = null;
        Surface.SkipIndex = -1;

        // 回到原样就不记这一笔（撤销栈里不留「拖了又拖回来」的空步）
        if (preview != null && _resizeMark != null && preview.Frame != _resizeMark.Frame &&
            _resizeMarkIndex >= 0)
            _doc.ReplaceAt(_resizeMarkIndex, preview);

        _resizeMark = null;
        _resizeMarkIndex = -1;
        _resizeHandle = FrameHandle.None;
        RefreshMarks();
    }

    private void CancelMarkResize()
    {
        Surface.Preview = null;
        Surface.SkipIndex = -1;
        _resizeMark = null;
        _resizeMarkIndex = -1;
        _resizeHandle = FrameHandle.None;
        Surface.InvalidateVisual();
    }

    // ============ 选择：拖放大镜的镜头 ============

    private Mark.Magnifier? _lensMark;
    private int _lensMarkIndex = -1;

    /// <summary>
    /// 选中的是放大镜、且这一下按在**镜头本体**上（源区域之外）或镜头下方的移动手柄里：
    /// 只挪镜头，源区域留在原处。照上游 <c>hit_resolution.rs</c>：源区域先判（= 整块挪动），
    /// 镜头次之（= 只动 <c>magnified_center</c>），移动手柄（镜头下缘 +24 DIP，±9 DIP 命中）优先于一切。
    /// </summary>
    private bool BeginMagnifierLensDrag(Point p)
    {
        if (_tool != CaptureTool.Select) return false;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return false;
        if (_doc.Marks[_selectedIndex] is not Mark.Magnifier magnifier) return false;

        bool grip = MagnifierInk.HitsMoveGrip(magnifier.Lens, p);
        bool lensOnly = magnifier.Lens.Contains(p) && !magnifier.SourceRect.Contains(p);
        if (!grip && !lensOnly) return false;

        _lensMarkIndex = _selectedIndex;
        _lensMark = magnifier;
        _origin = p;
        _drag = DragMode.MagnifierLens;
        Surface.SkipIndex = _lensMarkIndex;     // 原件藏起来，由预览代画（松手才提交）
        Surface.InvalidateVisual();
        return true;
    }

    /// <summary>镜头中心跟着指针走（按按下时的偏移搬，不会「跳一下」把中心吸到指针上）。</summary>
    private void UpdateMagnifierLensDrag(Point p)
    {
        if (_lensMark == null) return;
        var limit = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        Point center = _lensMark.LensCenter + (p - _origin);
        center = new Point(Math.Clamp(center.X, limit.Left, limit.Right),
            Math.Clamp(center.Y, limit.Top, limit.Bottom));
        Surface.Preview = _lensMark.WithLensCenter(center);
        Surface.InvalidateVisual();
    }

    private void CommitMagnifierLensDrag(Point p)
    {
        UpdateMagnifierLensDrag(p);

        var preview = Surface.Preview;
        Surface.Preview = null;
        Surface.SkipIndex = -1;

        if (preview is Mark.Magnifier moved && _lensMark != null &&
            moved.LensCenter != _lensMark.LensCenter && _lensMarkIndex >= 0)
            _doc.ReplaceAt(_lensMarkIndex, moved);

        _lensMark = null;
        _lensMarkIndex = -1;
        RefreshMarks();
    }

    private void CancelMagnifierLensDrag()
    {
        Surface.Preview = null;
        Surface.SkipIndex = -1;
        _lensMark = null;
        _lensMarkIndex = -1;
        Surface.InvalidateVisual();
    }

    // ============ 橡皮 ============

    private void EraseAt(Point p)
    {
        int index = _doc.HitTest(p, 4);
        if (index < 0) return;

        _doc.RemoveAt(index);
        SelectMark(-1);
        Surface.InvalidateVisual();
        Toolbar.SetUndo(_doc.CanUndo, _doc.CanRedo);
    }

    // ============ 折线 ============

    private void AddPolylinePoint(Point p, int clickCount)
    {
        if (clickCount >= 2 && _polyPoints.Count >= 2)
        {
            CompletePolyline();
            return;
        }

        _polyPoints.Add(p);
        _polyActive = true;
        Surface.Preview = PolylineMark;
        Surface.InvalidateVisual();
    }

    private Mark PolylineMark => new Mark.Polyline(_polyPoints.ToArray(), _style.Color, _style.Width, false);

    private void CompletePolyline()
    {
        if (_polyPoints.Count >= 2) _doc.Add(PolylineMark);
        _polyPoints.Clear();
        _polyActive = false;
        RefreshMarks();
    }

    private void CancelPolyline()
    {
        _polyPoints.Clear();
        _polyActive = false;
        Surface.Preview = null;
        RefreshMarks();
    }

    /// <summary>切工具 / 开始别的动作时，把画到一半的东西丢掉。</summary>
    private void CancelGesture()
    {
        _angleArmed = false;
        if (_selPolyActive)
        {
            CancelSelectionPolyline();
            UpdateChrome();
            return;
        }

        if (_polyActive)
        {
            CancelPolyline();
            return;
        }

        if (_drag == DragMode.None) return;

        // 改框 / 套索画到一半：还原成按下前的选区（也要把已落的点丢掉）
        if (_drag is DragMode.New or DragMode.Move or DragMode.Resize or DragMode.Lasso)
        {
            CancelDrag();
            return;
        }

        if (_drag == DragMode.Mark) CancelMarkDrag();
        else
        {
            Surface.Preview = null;
            Surface.InvalidateVisual();
        }

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        UpdateCursorForTool();
    }
}
