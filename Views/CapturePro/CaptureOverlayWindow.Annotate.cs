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

            case CaptureTool.Serial:
                _doc.Add(new Mark.Serial(p, _doc.NextSerial(), _style.Color, _style.Shape, _style.Numbering));
                RefreshMarks();
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
                Surface.Preview = BuildDragMark(p);
                Surface.InvalidateVisual();
                return true;
        }
    }

    /// <summary>拖动中：手绘补点，形状只挪终点。</summary>
    private void UpdateToolDrag(Point p)
    {
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

            case CaptureTool.Rect:
                return new Mark.Shape(_toolStart, current, color, width, false, ellipse: false);

            case CaptureTool.Ellipse:
                return new Mark.Shape(_toolStart, current, color, width, false, ellipse: true);

            case CaptureTool.Arrow:
                return new Mark.Arrow(_toolStart, current, color, width);

            case CaptureTool.Line:
                return new Mark.Line(_toolStart, current, color, width, false);

            case CaptureTool.Distance:
                return new Mark.Distance(_toolStart, current, color, width);

            case CaptureTool.Bracket:
                return new Mark.Bracket(_toolStart, current, color, width);

            case CaptureTool.SpeechBubble:
                return new Mark.SpeechBubble(MarkGeometry.RectOf(_toolStart, current), color, width);

            case CaptureTool.Magnifier:
                return new Mark.Magnifier(MarkGeometry.RectOf(_toolStart, current), _style.Factor, color, width);

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
                    ? Mark.Angle.FromArms(_angleVertex, _angleArm1, current, color, width)
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

        var mark = Mark.Angle.FromArms(_angleVertex, _angleArm1, p, color, width);
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
    /// 橡皮类按「对象」删除：与矩形相交 / 被笔迹碰到的标注整条去掉。
    /// 上游是像素级智能擦除（擦掉一部分并重建底图），这一层留到 P1-b 再对齐；
    /// 现在的语义与「点选橡皮」一致，只是命中范围更大。
    /// </summary>
    private void FinishEraseGesture(Point p)
    {
        int removed = 0;

        if (_tool == CaptureTool.RectEraser)
        {
            var rect = MarkGeometry.RectOf(_toolStart, p);
            if (rect.Width < 1 || rect.Height < 1)
            {
                _drag = DragMode.None;
                Surface.Preview = null;
                Surface.InvalidateVisual();
                return;
            }

            for (int i = _doc.Marks.Count - 1; i >= 0; i--)
                if (MarkGeometry.Inflate(_doc.Marks[i].Bounds, 2).IntersectsWith(rect) && _doc.RemoveAt(i))
                    removed++;
        }
        else
        {
            for (int i = _doc.Marks.Count - 1; i >= 0; i--)
                if (HitByBrush(_doc.Marks[i], _freehand, _style.WidthFor(CaptureTool.BrushEraser) / 2 + 2) &&
                    _doc.RemoveAt(i))
                    removed++;
        }

        _drag = DragMode.None;
        Surface.Preview = null;
        _freehand.Clear();
        if (removed > 0) SelectMark(-1);
        RefreshMarks();
    }

    /// <summary>笔迹路径碰到这条标注（点到线段距离）就算命中。</summary>
    private static bool HitByBrush(Mark mark, IReadOnlyList<Point> path, double radius)
    {
        if (path.Count == 0) return false;
        if (path.Count == 1) return mark.HitTest(path[0], radius);

        foreach (var point in path)
            if (mark.HitTest(point, radius)) return true;

        // 步子大时逐个采样点可能跨过细标注：再按中点补几刀
        for (int i = 1; i < path.Count; i++)
        {
            var mid = new Point((path[i - 1].X + path[i].X) / 2, (path[i - 1].Y + path[i].Y) / 2);
            if (mark.HitTest(mid, radius)) return true;
        }

        return false;
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
