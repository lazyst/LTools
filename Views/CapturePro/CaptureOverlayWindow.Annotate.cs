using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 标注手势：拖一类（画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 马赛克 / 聚光）、
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

            case CaptureTool.Serial:
                _doc.Add(new Mark.Serial(p, _doc.NextSerial(), _style.Color, false));
                RefreshMarks();
                return true;

            case CaptureTool.Text:
                OpenTextEntry(p);
                return true;

            case CaptureTool.Polyline:
                AddPolylinePoint(p, clickCount);
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
        if (_tool is CaptureTool.Pen or CaptureTool.Highlighter)
        {
            Point last = _freehand[^1];
            if ((p - last).Length >= 1.2) _freehand.Add(p);
        }

        Surface.Preview = BuildDragMark(p);
        Surface.InvalidateVisual();
    }

    /// <summary>松手：够大才落盘（点一下不留下 0 像素的框）。</summary>
    private void FinishToolDrag(Point p)
    {
        var mark = BuildDragMark(p);
        bool big = _tool is CaptureTool.Pen or CaptureTool.Highlighter
            ? _freehand.Count > 0
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

            case CaptureTool.Mosaic:
                return new Mark.Mosaic(_toolStart, current, _style.Block);

            case CaptureTool.Spotlight:
                return new Mark.Spotlight(_toolStart, current, _style.Dim);

            default:
                return null;
        }
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
        if (_polyActive)
        {
            CancelPolyline();
            return;
        }

        if (_drag == DragMode.None) return;

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
