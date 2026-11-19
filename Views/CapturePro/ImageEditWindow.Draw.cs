using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的标注手势（照上游 <c>SnowCanvasWidget</c> 那一套，与截图覆盖层同源）：
/// 拖一类、点一类、擦除，「选择」工具下的点选 / 拖动 / 拉手柄 / 框选，以及放大镜镜头单独拖动。
/// </summary>
/// <remarks>
/// <para>
/// 坐标一律是**图像像素**（编辑窗里 1 图像像素 = 1 文档单位，缩放只走画布的
/// <c>RenderTransform</c>），所以 <see cref="Mark"/> 的几何与覆盖层完全一致；
/// 命中容差按 <see cref="_zoom"/> 换算，缩到一半时手感不跟着变细。
/// </para>
/// <para>
/// 每次落盘前 <see cref="ImageEditDocument.PushUndo"/> 记一帧：一次手势只占一个撤销位。
/// </para>
/// </remarks>
public partial class ImageEditWindow
{
    private enum EditDrag { None, Draw, Erase, Mark, MarkResize, MagnifierLens, Marquee }

    /// <summary>命中选择标注的宽容半径（DIP，按缩放折算）。</summary>
    private const double MarkHitSlop = 4;

    /// <summary>八个把手的命中半径（DIP，按缩放折算）。</summary>
    private const double GripHit = 8;

    /// <summary>每个工具家族一套样式（与截图覆盖层共用同一份落盘在 <c>ui.json</c> 的偏好）。</summary>
    private readonly ToolStyleSet _styles = CaptureStyleStore.Current;

    private CaptureTool _tool = CaptureTool.Select;

    /// <summary>平移工具（上游默认 <c>Mode::Pan</c>，打开就能拖画面）。</summary>
    private bool _panMode = true;

    private readonly List<int> _selected = new();
    private int _selectedIndex = -1;

    private EditDrag _drag;
    private Point _origin;
    private Point _toolStart;
    private readonly List<Point> _freehand = new();
    private readonly List<Point> _polyPoints = new();
    private bool _polyActive;

    private uint _dragSeed = ShapeInk.NextSeed();

    private List<(int Index, Mark Mark)>? _dragGroup;
    private int _dragMarkIndex = -1;

    private Mark? _resizeMark;
    private int _resizeIndex = -1;
    private Rect _resizeFrame;
    private FrameHandle _resizeHandle;

    private Mark.Magnifier? _lensMark;
    private int _lensIndex = -1;

    private Point _angleVertex;
    private Point _angleArm1;
    private bool _angleArmed;

    private Point _marqueeStart;
    private int[]? _marqueeBase;

    /// <summary>选中标注时参数行显示的那一套（临时副本；改动合并回创建样式与各条标注）。</summary>
    private ToolStyle? _selDisplay, _selBaseline;
    private string? _selFamily;
    private readonly List<int> _selPeers = new();

    /// <summary>当前工具的那一套样式（工具栏改它，标注创建读它）。</summary>
    private ToolStyle CurrentStyle => _styles.For(_tool);

    private bool HasTextInput => _textEditing;

    // ============ 工具 / 平移 ============

    private void SetTool(CaptureTool tool)
    {
        if (_tool == tool && !_panMode && _cropAxis == null) return;

        FinishPolyline();
        CommitText();
        CancelGesture();

        // 换绘图工具就退出裁剪（否则画布按下会被裁剪先接走）
        if (_cropAxis != null) CancelCropMode();

        _panMode = false;
        _tool = tool;
        CaptureToolMemory.Remember(tool);
        Toolbar.Sync(tool);
        Toolbar.SetPan(false);

        if (tool == CaptureTool.AutoFilter) EnterAutoFilter();
        else LeaveAutoFilter();

        if (tool != CaptureTool.Select) SelectMark(-1);

        UpdateCanvasCursor();
        UpdateChrome();
    }

    /// <summary>点工具条上的「平移」：取消工具选中，左键拖动画布。</summary>
    private void EnterPanMode()
    {
        FinishPolyline();
        CommitText();
        CancelGesture();

        if (_cropAxis != null) CancelCropMode();

        LeaveAutoFilter();

        _panMode = true;
        SelectMark(-1);
        Toolbar.SetPan(true);

        UpdateCanvasCursor();
        UpdateChrome();
    }

    // ============ 按下 / 移动 / 松手 ============

    /// <summary>画布按下（已经换算成图像像素）。返回 true = 这一下被标注手势接下了。</summary>
    private bool BeginDrawGesture(Point p, int clickCount)
    {
        _origin = p;
        _dragSeed = ShapeInk.NextSeed();

        if (_tool == CaptureTool.Select)
        {
            // 0. 选中标注的把手优先
            var handle = MarkHandleAt(p);
            if (handle is not (FrameHandle.None or FrameHandle.Move))
            {
                BeginMarkResize(p, handle);
                return true;
            }

            // 0.5 放大镜的镜头（源之外的部分）与移动手柄
            BeginMagnifierLensDrag(p);
            if (_drag == EditDrag.MagnifierLens) return true;


            int hit = _doc.Doc!.HitTest(p, Slop(MarkHitSlop));
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (hit >= 0)
            {
                if (shift)
                {
                    ToggleSelection(hit);
                    return true;
                }

                SelectMark(hit);
                BeginMarkDrag(p, hit);
                return true;
            }

            // 空白拖动 = 框选（上游选择工具的 marquee）；Shift 按住也一样，点空白后松手即清空
            BeginMarquee(p);
            return true;
        }

        // 快速选择：绘图工具下点已有同类元素 = 选中并拖它（与截图覆盖层同一份设置）
        if (clickCount == 1 && TryQuickSelect(p)) return true;

        switch (_tool)
        {
            case CaptureTool.Eraser:
                _drag = EditDrag.Erase;
                EraseAt(p);
                return true;

            case CaptureTool.RectEraser:
                _toolStart = p;
                _drag = EditDrag.Draw;
                SetPreview(new Mark.Highlight(p, p, Color.FromRgb(0x3B, 0x82, 0xF6), 0.18));
                return true;

            case CaptureTool.BrushEraser:
                _toolStart = p;
                _freehand.Clear();
                _freehand.Add(p);
                _drag = EditDrag.Draw;
                SetPreview(MarkFactory.BrushEraseTrail(_freehand, CurrentStyle));
                return true;

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
                BeginAngle(p);
                return true;

            case CaptureTool.Watermark:
                _doc.PushUndo();
                _doc.Doc!.Add(WatermarkMark);
                RefreshMarks();
                return true;

            default:
                _toolStart = p;
                _freehand.Clear();
                _freehand.Add(p);
                _drag = EditDrag.Draw;
                SetPreview(BuildDragMark(p));
                return true;
        }
    }

    /// <summary>拖动中（图像像素）。</summary>
    private void UpdateDraw(Point p)
    {
        switch (_drag)
        {
            case EditDrag.Draw:
                if (ToolCatalog.IsFreehand(_tool))
                {
                    Point last = _freehand[^1];
                    if ((p - last).Length >= 1.2) _freehand.Add(p);
                }

                SetPreview(_tool == CaptureTool.BrushEraser
                    ? MarkFactory.BrushEraseTrail(_freehand, CurrentStyle)
                    : BuildDragMark(p));
                return;

            case EditDrag.Erase:
                EraseAt(p);
                return;

            case EditDrag.Mark:
                UpdateMarkDrag(p);
                return;

            case EditDrag.MarkResize:
                UpdateMarkResize(p);
                return;

            case EditDrag.MagnifierLens:
                UpdateMagnifierLensDrag(p);
                return;

            case EditDrag.Marquee:
                UpdateMarquee(p);
                return;
        }
    }

    /// <summary>松手（图像像素）。</summary>
    private void FinishDraw(Point p)
    {
        switch (_drag)
        {
            case EditDrag.Draw:
                FinishToolDrag(p);
                break;

            case EditDrag.Erase:
                _drag = EditDrag.None;
                break;

            case EditDrag.Mark:
                CommitMarkDrag(p);
                break;

            case EditDrag.MarkResize:
                CommitMarkResize(p);
                break;

            case EditDrag.MagnifierLens:
                CommitMagnifierLensDrag(p);
                break;

            case EditDrag.Marquee:
                FinishMarquee();
                break;
        }

        _drag = EditDrag.None;
        Canvas.SkipIndex = -1;
        Canvas.Previews = null;
        Canvas.MarkMarquee = Rect.Empty;
        Canvas.MarqueeHits = Array.Empty<int>();
        SetPreview(null);
    }

    /// <summary>切工具 / Esc / 关窗时把画到一半的东西丢掉。</summary>
    private void CancelGesture()
    {
        _angleArmed = false;
        _polyPoints.Clear();
        _polyActive = false;

        _dragGroup = null;
        _resizeMark = null;
        _resizeIndex = -1;
        _lensMark = null;
        _lensIndex = -1;
        _marqueeBase = null;

        _drag = EditDrag.None;
        Canvas.SkipIndex = -1;
        Canvas.Previews = null;
        Canvas.MarkMarquee = Rect.Empty;
        Canvas.MarqueeHits = Array.Empty<int>();
        SetPreview(null);

        try { Surface.ReleaseMouseCapture(); } catch { /* 没捕获 */ }
    }

    // ============ 拖动类 ============

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
        bool big = MarkFactory.BigEnough(_tool, _toolStart, p, _freehand.Count);

        SetPreview(null);

        if (mark != null && big)
        {
            _doc.PushUndo();
            _doc.Doc!.Add(mark);
        }

        RefreshMarks();
        if (mark != null && big) NoteCreated();
    }

    private Mark? BuildDragMark(Point current) =>
        MarkFactory.Drag(_tool, _toolStart, current, _freehand, CurrentStyle, _dragSeed,
            _tool == CaptureTool.Angle
                ? new AngleArms(_angleVertex, _angleArm1, _angleArmed)
                : AngleArms.None);

    /// <summary>序号：用参数行里那个「下一个序号」，落完自动 +1（照上游）。</summary>
    private void AddSerialAt(Point p)
    {
        int number = Math.Max(1, CurrentStyle.SerialNumber);
        _doc.PushUndo();
        _doc.Doc!.Add(new Mark.Serial(p, number, CurrentStyle.SerialSpecOf()));
        CurrentStyle.SerialNumber = number + 1;
        Toolbar.RefreshValues();
        RefreshMarks();
        NoteCreated();
    }

    private void SetPreview(Mark? mark)
    {
        Canvas.Preview = mark;
        Canvas.InvalidateVisual();
    }

    // ============ 测角 ============

    private void BeginAngle(Point p)
    {
        _toolStart = p;
        _freehand.Clear();
        _freehand.Add(p);
        _drag = EditDrag.Draw;

        if (!_angleArmed) _angleVertex = p;
        SetPreview(BuildDragMark(p));
    }

    private void FinishAngle(Point p)
    {
        if (!_angleArmed)
        {
            if ((p - _angleVertex).Length < 6)
            {
                SetPreview(null);
                _drag = EditDrag.None;
                return;
            }

            _angleArm1 = p;
            _angleArmed = true;
            SetPreview(null);
            _drag = EditDrag.None;
            return;
        }

        var mark = Mark.Angle.FromArms(_angleVertex, _angleArm1, p, CurrentStyle.Color,
            CurrentStyle.WidthFor(CaptureTool.Angle), CurrentStyle.AngleSpecOf());

        _angleArmed = false;
        _drag = EditDrag.None;
        SetPreview(null);

        _doc.PushUndo();
        _doc.Doc!.Add(mark);
        RefreshMarks();
        NoteCreated();
    }

    // ============ 橡皮（恢复原图块） ============

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
                CurrentStyle.WidthFor(CaptureTool.BrushEraser));
        }

        SetPreview(null);
        _freehand.Clear();
        SelectMark(-1);

        if (mark != null)
        {
            _doc.PushUndo();
            _doc.Doc!.Add(mark);
        }

        RefreshMarks();
    }

    // ============ 选择：点选 / 拖动 / 改大小 / 框选 ============

    /// <summary>新画的元素自动选中（与截图覆盖层共用同一份设置）。</summary>
    private void NoteCreated()
    {
        if (!DrawingSettings.AutoSelectNew) return;
        if (DrawingSettings.IsAutoSelectExcluded(_tool)) return;

        var doc = _doc.Doc;
        if (doc == null || doc.Marks.Count == 0) return;
        int index = doc.Marks.Count - 1;
        if (!MarkStyler.TryFamily(doc.Marks[index], out _, out _)) return;
        SelectMark(index);
    }

    /// <summary>快速选择：绘图工具下左键点已有同类元素 = 选中并拖动它。</summary>
    private bool TryQuickSelect(Point p)
    {
        if (_tool is CaptureTool.Select or CaptureTool.Picker or CaptureTool.AutoFilter
            or CaptureTool.Eraser or CaptureTool.RectEraser or CaptureTool.BrushEraser)
            return false;

        if (DrawingSettings.IsQuickSelectionDisabled(_tool)) return false;

        var doc = _doc.Doc;
        if (doc == null) return false;

        string family = StyleFamilies.Of(_tool);
        int hit = doc.HitTestWhere(p, Slop(MarkHitSlop),
            mark => MarkStyler.TryFamily(mark, out var markFamily, out _) && markFamily == family);
        if (hit < 0) return false;

        BeginMarkDrag(p, hit);
        return true;
    }

    private void BeginMarkDrag(Point p, int index)
    {
        if (!IsMarkSelected(index)) SelectMark(index);

        var group = IsMarkSelected(index) && _selected.Count > 1
            ? _selected.Where(i => i >= 0 && i < _doc.Doc!.Marks.Count).ToList()
            : new List<int> { index };

        _dragGroup = group.Select(i => (i, _doc.Doc!.Marks[i])).ToList();
        _dragMarkIndex = index;
        _origin = p;
        _drag = EditDrag.Mark;

        Canvas.SkipIndex = group.Count == 1 ? index : -1;
        Canvas.Previews = group.Count > 1 ? _dragGroup.Select(entry => entry.Mark).ToArray() : null;
        Canvas.InvalidateVisual();
    }

    private void UpdateMarkDrag(Point p)
    {
        if (_dragGroup is not { Count: > 0 }) return;

        var delta = p - _origin;
        if (_dragGroup.Count == 1) SetPreview(_dragGroup[0].Mark.MoveBy(delta));
        else Canvas.Previews = _dragGroup.Select(entry => entry.Mark.MoveBy(delta)).ToArray();
        Canvas.InvalidateVisual();
    }

    private void CommitMarkDrag(Point p)
    {
        var delta = p - _origin;
        var group = _dragGroup;

        SetPreview(null);
        Canvas.Previews = null;
        Canvas.SkipIndex = -1;
        _dragGroup = null;
        _dragMarkIndex = -1;

        var doc = _doc.Doc;
        if (group is { Count: > 0 } && delta.Length >= 1 && doc != null)
        {
            _doc.PushUndo();
            var marks = doc.Marks.ToArray();
            doc.EditBatch(list =>
            {
                foreach (var (index, mark) in group)
                    if (index >= 0 && index < list.Count) list[index] = mark.MoveBy(delta);
            });
        }

        RefreshMarks();
    }

    private void BeginMarkResize(Point p, FrameHandle handle)
    {
        _resizeIndex = _selectedIndex;
        _resizeMark = _doc.Doc!.Marks[_selectedIndex];
        _resizeFrame = MarkGeometry.PaddedFrame(_resizeMark.Frame);
        _resizeHandle = handle;
        _origin = p;
        _drag = EditDrag.MarkResize;
        Canvas.SkipIndex = _resizeIndex;
        Canvas.InvalidateVisual();
    }

    private void UpdateMarkResize(Point p)
    {
        if (_resizeMark == null) return;

        var limit = new Rect(0, 0, Math.Max(1, _doc.Width), Math.Max(1, _doc.Height));
        Rect padded = MarkGeometry.ResizeFrame(_resizeFrame, _resizeHandle, p - _origin,
            MarkGeometry.FramePad * 2 + 4, limit);
        Rect target = MarkGeometry.Inflate(padded, -MarkGeometry.FramePad);

        SetPreview(_resizeMark.ScaleTo(_resizeMark.Frame, target));
    }

    private void CommitMarkResize(Point p)
    {
        UpdateMarkResize(p);

        var preview = Canvas.Preview;
        SetPreview(null);
        Canvas.SkipIndex = -1;

        if (preview != null && _resizeMark != null && preview.Frame != _resizeMark.Frame &&
            _resizeIndex >= 0)
        {
            _doc.PushUndo();
            _doc.Doc!.ReplaceAt(_resizeIndex, preview);
        }

        _resizeMark = null;
        _resizeIndex = -1;
        _resizeHandle = FrameHandle.None;
        RefreshMarks();
    }

    private void BeginMagnifierLensDrag(Point p)
    {
        if (_tool != CaptureTool.Select) return;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Doc!.Marks.Count) return;
        if (_doc.Doc!.Marks[_selectedIndex] is not Mark.Magnifier magnifier) return;

        bool grip = MagnifierInk.HitsMoveGrip(magnifier.Lens, p);
        bool lensOnly = magnifier.Lens.Contains(p) && !magnifier.SourceRect.Contains(p);
        if (!grip && !lensOnly) return;

        _lensIndex = _selectedIndex;
        _lensMark = magnifier;
        _origin = p;
        _drag = EditDrag.MagnifierLens;
        Canvas.SkipIndex = _lensIndex;
        Canvas.InvalidateVisual();
    }

    private void UpdateMagnifierLensDrag(Point p)
    {
        if (_lensMark == null) return;

        var limit = new Rect(0, 0, Math.Max(1, _doc.Width), Math.Max(1, _doc.Height));
        Point center = _lensMark.LensCenter + (p - _origin);
        center = new Point(Math.Clamp(center.X, limit.Left, limit.Right),
            Math.Clamp(center.Y, limit.Top, limit.Bottom));

        SetPreview(_lensMark.WithLensCenter(center));
    }

    private void CommitMagnifierLensDrag(Point p)
    {
        UpdateMagnifierLensDrag(p);

        var preview = Canvas.Preview;
        SetPreview(null);
        Canvas.SkipIndex = -1;

        if (preview is Mark.Magnifier moved && _lensMark != null &&
            moved.LensCenter != _lensMark.LensCenter && _lensIndex >= 0)
        {
            _doc.PushUndo();
            _doc.Doc!.ReplaceAt(_lensIndex, moved);
        }

        _lensMark = null;
        _lensIndex = -1;
        RefreshMarks();
    }

    private void BeginMarquee(Point p)
    {
        _marqueeBase = _selected.ToArray();
        _marqueeStart = p;
        _drag = EditDrag.Marquee;
        Canvas.MarkMarquee = Rect.Empty;
        Canvas.MarqueeHits = Array.Empty<int>();
        Canvas.InvalidateVisual();
    }

    private void UpdateMarquee(Point p)
    {
        var box = MarkGeometry.RectOf(_marqueeStart, p);
        var doc = _doc.Doc!;

        var hits = new List<int>();
        for (int i = 0; i < doc.Marks.Count; i++)
        {
            var frame = doc.Marks[i].Frame;
            if (!frame.IsEmpty && box.IntersectsWith(frame)) hits.Add(i);
        }

        Canvas.MarkMarquee = box;
        Canvas.MarqueeHits = hits;
        Canvas.InvalidateVisual();
    }

    private void FinishMarquee()
    {
        var box = Canvas.MarkMarquee;
        var hits = Canvas.MarqueeHits;

        Canvas.MarkMarquee = Rect.Empty;
        Canvas.MarqueeHits = Array.Empty<int>();

        if (box.Width < 1 && box.Height < 1)
        {
            SelectMark(-1);      // 空白单击 = 取消选中
            return;
        }

        var merged = new List<int>(_marqueeBase ?? Array.Empty<int>());
        foreach (int index in hits)
            if (!merged.Contains(index)) merged.Add(index);

        SelectMarks(merged);
    }

    private void EraseAt(Point p)
    {
        int index = _doc.Doc!.HitTest(p, Slop(MarkHitSlop));
        if (index < 0) return;

        _doc.PushUndo();
        _doc.Doc!.RemoveAt(index);
        SelectMark(-1);
        RefreshMarks();
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
        SetPreview(PolylineMark);
    }

    private Mark PolylineMark =>
        new Mark.Polyline(_polyPoints.ToArray(), CurrentStyle.Color, CurrentStyle.Width, false);

    private void CompletePolyline()
    {
        if (_polyPoints.Count >= 2)
        {
            _doc.PushUndo();
            _doc.Doc!.Add(PolylineMark);
        }

        _polyPoints.Clear();
        _polyActive = false;
        SetPreview(null);
        RefreshMarks();
        NoteCreated();
    }

    /// <summary>工具栏上的「完成折线」（点别的工具 / 关窗时也先落下来）。</summary>
    private void FinishPolyline()
    {
        if (_polyActive) CompletePolyline();
    }

    // ============ 选中状态 ============

    private bool IsMarkSelected(int index) => _selected.Contains(index);

    private void SelectMark(int index) => SelectMarks(index >= 0 ? new[] { index } : Array.Empty<int>());

    private void SelectMarks(IReadOnlyList<int> indices)
    {
        _selected.Clear();
        foreach (int index in indices)
            if (index >= 0 && _doc.Doc != null && index < _doc.Doc.Marks.Count && !_selected.Contains(index))
                _selected.Add(index);

        _selectedIndex = _selected.Count > 0 ? _selected[^1] : -1;

        Canvas.SelectedIndices = _selected.ToArray();
        Canvas.SelectedIndex = _selectedIndex;
        Canvas.ShowHandles = _tool == CaptureTool.Select && _selected.Count == 1;
        Canvas.InvalidateVisual();

        SyncSelectionStyle();
    }

    private void ToggleSelection(int index)
    {
        var next = new List<int>(_selected);
        if (!next.Remove(index)) next.Add(index);
        SelectMarks(next);
    }

    /// <summary>删掉选中的标注（Delete / Backspace）：一次手势一个撤销位。</summary>
    private void DeleteSelection()
    {
        if (_selected.Count == 0) return;

        _doc.PushUndo();
        var doc = _doc.Doc!;
        foreach (int index in _selected.OrderByDescending(i => i))
            if (index >= 0 && index < doc.Marks.Count) doc.RemoveAt(index);

        SelectMark(-1);
        RefreshMarks();
    }

    // ============ 命中 ============

    private FrameHandle MarkHandleAt(Point p)
    {
        if (_tool != CaptureTool.Select) return FrameHandle.None;
        if (_selected.Count != 1) return FrameHandle.None;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Doc!.Marks.Count) return FrameHandle.None;

        var mark = _doc.Doc!.Marks[_selectedIndex];
        if (!mark.CanScale) return FrameHandle.None;

        return MarkGeometry.HitFrame(MarkGeometry.PaddedFrame(mark.Frame), p, Slop(GripHit));
    }

    private static Cursor CursorFor(FrameHandle handle) => handle switch
    {
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    /// <summary>命中容差：屏幕上的手感保持不变（除以缩放）。</summary>
    private double Slop(double dips) => dips / Math.Max(0.05, _zoom);

    private void UpdateCanvasCursor()
    {
        Surface.Cursor = _panMode ? Cursors.Hand
            : _cropAxis != null ? Cursors.Cross
            : _tool == CaptureTool.Text ? Cursors.IBeam
            : ToolCatalog.WantsCrossCursor(_tool) ? Cursors.Cross
            : Cursors.Arrow;
    }

    private void UpdateHoverCursor(Point view)
    {
        if (!_doc.HasImage) return;

        if (_panMode) { Surface.Cursor = Cursors.Hand; return; }
        if (_cropAxis != null) { Surface.Cursor = Cursors.Cross; return; }

        if (_tool != CaptureTool.Select)
        {
            UpdateCanvasCursor();
            return;
        }

        var point = ToImage(view);
        var handle = MarkHandleAt(point);
        if (handle is not (FrameHandle.None or FrameHandle.Move))
        {
            Surface.Cursor = CursorFor(handle);
            return;
        }

        Surface.Cursor = _doc.Doc!.HitTest(point, Slop(MarkHitSlop)) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
    }

    // ============ 选中标注的样式回写（与覆盖层同一套：只合并变过的字段） ============

    private void SyncSelectionStyle()
    {
        _selDisplay = null;
        _selBaseline = null;
        _selFamily = null;
        _selPeers.Clear();

        var doc = _doc.Doc;
        if (doc != null && _selected.Count > 0 &&
            MarkStyler.TrySelectionFamily(doc.Marks, _selected, _selectedIndex,
                out string family, out CaptureTool tool, out int representative))
        {
            var display = _styles.ForFamily(family).Clone();
            MarkStyler.Capture(doc.Marks[representative], display);

            _selDisplay = display;
            _selBaseline = display.Clone();
            _selFamily = family;

            var peers = new List<ToolStyle>();
            foreach (int index in _selected)
            {
                if (index < 0 || index >= doc.Marks.Count) continue;
                if (!MarkStyler.InFamily(doc.Marks[index], family)) continue;
                var peer = _styles.ForFamily(family).Clone();
                MarkStyler.Capture(doc.Marks[index], peer);
                peers.Add(peer);
                _selPeers.Add(index);
            }

            Toolbar.SyncSelection(tool, display, peers);
            return;
        }

        Toolbar.ClearSelectionStyle();
    }

    private void ApplyStyleToSelection()
    {
        var doc = _doc.Doc;
        if (doc == null || _selDisplay == null || _selBaseline == null || _selFamily == null) return;
        if (_selected.Count == 0) return;

        ToolStyle display = _selDisplay;
        string family = _selFamily;
        bool colorChanged = display.Color != _selBaseline.Color;

        var targets = _selected.Where(index => index >= 0 && index < doc.Marks.Count &&
                                               MarkStyler.InFamily(doc.Marks[index], family)).ToArray();

        if (targets.Length > 0)
        {
            _doc.PushUndo();
            doc.EditBatch(list =>
            {
                foreach (int index in targets)
                {
                    if (index >= list.Count) continue;
                    list[index] = MarkStyler.Apply(list[index], display);
                }
            });
        }

        display.CopyChangedTo(_styles.ForFamily(family), _selBaseline);
        if (colorChanged) _styles.SetColor(family, display.Color);

        Canvas.InvalidateVisual();
        UpdateChrome();
    }
}
