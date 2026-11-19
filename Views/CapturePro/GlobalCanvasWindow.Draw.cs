using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的标注手势（照图片编辑窗 / 截图覆盖层那一套）：拖一类、点一类、擦除，
/// 「选择」工具下的点选 / 拖动 / 拉手柄 / 框选，折线逐点单击。
/// </summary>
/// <remarks>
/// <para>
/// 坐标一律是**画布坐标**（相机只管显示，标注不改）；命中容差按 <see cref="_zoom"/> 折算，
/// 缩到一半手感也不跟着变细。画布没有边界（无限大），所以拖手柄 / 框选用的「limit」是一个
/// 足够大的矩形，不做夹取。
/// </para>
/// <para>
/// 撤销走 <see cref="CaptureDocument"/> 自己的栈：<c>Add</c> / <c>RemoveAt</c> / <c>EditBatch</c>
/// 内部就会记一帧，**不要**在调用前再调 <c>PushUndo</c>（那会让一次操作占两格）。
/// </para>
/// </remarks>
public partial class GlobalCanvasWindow
{
    private enum EditDrag { None, Draw, Erase, Mark, MarkResize, Marquee }

    /// <summary>命中选择标注的宽容半径（DIP，按缩放折算）。</summary>
    private const double MarkHitSlop = 4;

    /// <summary>八个把手的命中半径（DIP，按缩放折算）。</summary>
    private const double GripHit = 8;

    private EditDrag _drag;
    private Point _origin;
    private Point _toolStart;
    private readonly List<Point> _freehand = new();
    private readonly List<Point> _polyPoints = new();
    private bool _polyActive;

    private uint _dragSeed = ShapeInk.NextSeed();

    private List<(int Index, Mark Mark)>? _dragGroup;

    private Mark? _resizeMark;
    private int _resizeIndex = -1;
    private Rect _resizeFrame;
    private FrameHandle _resizeHandle;

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

    // ============ 按下 / 移动 / 松手 ============

    /// <summary>画布按下（画布坐标）。返回 true = 这一下被标注手势接下了。</summary>
    private bool BeginDrawGesture(Point p, int clickCount)
    {
        _origin = p;
        _dragSeed = ShapeInk.NextSeed();

        if (_tool == CaptureTool.Picker)
        {
            if (_pickerSampling) CommitPick(p);
            else BeginPicker();
            return true;
        }

        if (_tool == CaptureTool.Select)
        {
            // 0. 选中标注的把手优先
            var handle = MarkHandleAt(p);
            if (handle is not (FrameHandle.None or FrameHandle.Move))
            {
                BeginMarkResize(p, handle);
                return true;
            }

            int hit = _doc.HitTest(p, Slop(MarkHitSlop));
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

            // 空白拖动 = 框选（上游选择工具的 marquee）
            BeginMarquee(p);
            return true;
        }

        switch (_tool)
        {
            case CaptureTool.Eraser:
                _drag = EditDrag.Erase;
                EraseAt(p);
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
                _doc.Add(WatermarkMark);
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

    /// <summary>拖动中（画布坐标）。</summary>
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

                SetPreview(BuildDragMark(p));
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

            case EditDrag.Marquee:
                UpdateMarquee(p);
                return;
        }
    }

    /// <summary>松手（画布坐标）。</summary>
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

        var mark = BuildDragMark(p);
        bool big = MarkFactory.BigEnough(_tool, _toolStart, p, _freehand.Count);

        SetPreview(null);

        if (mark != null && big) _doc.Add(mark);

        RefreshMarks();
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
        _doc.Add(new Mark.Serial(p, number, CurrentStyle.SerialSpecOf()));
        CurrentStyle.SerialNumber = number + 1;
        Toolbar.RefreshValues();
        RefreshMarks();
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

        _doc.Add(mark);
        RefreshMarks();
    }

    // ============ 选择：点选 / 拖动 / 改大小 / 框选 ============

    private void BeginMarkDrag(Point p, int index)
    {
        if (!IsMarkSelected(index)) SelectMark(index);

        var group = IsMarkSelected(index) && _selected.Count > 1
            ? _selected.Where(i => i >= 0 && i < _doc.Marks.Count).ToList()
            : new List<int> { index };

        _dragGroup = group.Select(i => (i, _doc.Marks[i])).ToList();
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

        if (group is { Count: > 0 } && delta.Length >= 1)
        {
            _doc.EditBatch(list =>
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
        _resizeMark = _doc.Marks[_selectedIndex];
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

        Rect padded = MarkGeometry.ResizeFrame(_resizeFrame, _resizeHandle, p - _origin,
            MarkGeometry.FramePad * 2 + 4, EditLimit);
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
            _doc.ReplaceAt(_resizeIndex, preview);
        }

        _resizeMark = null;
        _resizeIndex = -1;
        _resizeHandle = FrameHandle.None;
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

        var hits = new List<int>();
        for (int i = 0; i < _doc.Marks.Count; i++)
        {
            var frame = _doc.Marks[i].Frame;
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
        int index = _doc.HitTest(p, Slop(MarkHitSlop));
        if (index < 0) return;

        _doc.RemoveAt(index);
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
        if (_polyPoints.Count >= 2) _doc.Add(PolylineMark);

        _polyPoints.Clear();
        _polyActive = false;
        SetPreview(null);
        RefreshMarks();
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
            if (index >= 0 && index < _doc.Marks.Count && !_selected.Contains(index))
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

        var targets = _selected.Where(i => i >= 0 && i < _doc.Marks.Count).ToArray();
        if (targets.Length == 0) return;

        var doc = _doc;
        _doc.EditBatch(list =>
        {
            foreach (int index in targets.OrderByDescending(i => i))
                if (index >= 0 && index < list.Count) list.RemoveAt(index);
        });

        SelectMark(-1);
        RefreshMarks();
    }

    // ============ 命中 ============

    private FrameHandle MarkHandleAt(Point p)
    {
        if (_tool != CaptureTool.Select) return FrameHandle.None;
        if (_selected.Count != 1) return FrameHandle.None;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return FrameHandle.None;

        var mark = _doc.Marks[_selectedIndex];
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
        Surface.Cursor = _tool == CaptureTool.Text ? Cursors.IBeam
            : _tool is CaptureTool.Select ? Cursors.Arrow
            : ToolCatalog.WantsCrossCursor(_tool) ? Cursors.Cross
            : Cursors.Arrow;
    }

    private void UpdateHoverCursor(Point view)
    {
        if (_panning) { Surface.Cursor = Cursors.SizeAll; return; }

        if (_tool != CaptureTool.Select)
        {
            UpdateCanvasCursor();
            return;
        }

        var point = ToCanvas(view);
        var handle = MarkHandleAt(point);
        if (handle is not (FrameHandle.None or FrameHandle.Move))
        {
            Surface.Cursor = CursorFor(handle);
            return;
        }

        Surface.Cursor = _doc.HitTest(point, Slop(MarkHitSlop)) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
    }

    // ============ 选中标注的样式回写（与覆盖层同一套：只合并变过的字段） ============

    private void SyncSelectionStyle()
    {
        _selDisplay = null;
        _selBaseline = null;
        _selFamily = null;
        _selPeers.Clear();

        if (_selected.Count > 0 &&
            MarkStyler.TrySelectionFamily(_doc.Marks, _selected, _selectedIndex,
                out string family, out CaptureTool tool, out int representative))
        {
            var display = _styles.ForFamily(family).Clone();
            MarkStyler.Capture(_doc.Marks[representative], display);

            _selDisplay = display;
            _selBaseline = display.Clone();
            _selFamily = family;

            var peers = new List<ToolStyle>();
            foreach (int index in _selected)
            {
                if (index < 0 || index >= _doc.Marks.Count) continue;
                if (!MarkStyler.InFamily(_doc.Marks[index], family)) continue;
                var peer = _styles.ForFamily(family).Clone();
                MarkStyler.Capture(_doc.Marks[index], peer);
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
        if (_selDisplay == null || _selBaseline == null || _selFamily == null) return;
        if (_selected.Count == 0) return;

        ToolStyle display = _selDisplay;
        string family = _selFamily;
        bool colorChanged = display.Color != _selBaseline.Color;

        var targets = _selected.Where(index => index >= 0 && index < _doc.Marks.Count &&
                                               MarkStyler.InFamily(_doc.Marks[index], family)).ToArray();

        if (targets.Length > 0)
        {
            _doc.EditBatch(list =>
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
