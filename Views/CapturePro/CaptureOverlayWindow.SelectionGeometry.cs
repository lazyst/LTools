using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「选区计算 / 缩放」部分。</summary>
public partial class CaptureOverlayWindow
{
    private void MoveOneRegion(Vector delta)
    {
        if (_originRegions == null || _dragRegion < 0 || _dragRegion >= _originRegions.Count)
        {
            _drag = DragMode.Move;      // 快照没了 = 退回整体移动，别把手势吞掉
            MoveFrame(SnapMove(MoveTo(_originRect, delta)));
            return;
        }

        var source = _originRegions[_dragRegion];
        Rect target = SnapMoveRegion(MoveTo(source.Bounds, delta), _dragRegion);
        var shift = new Vector(target.X - source.Bounds.X, target.Y - source.Bounds.Y);

        _regions.Clear();
        for (int i = 0; i < _originRegions.Count; i++)
            _regions.Add(i == _dragRegion ? source.Offset(shift) : _originRegions[i]);
        RefreshSelection();     // 外接框 = 并集：被拖的那块动了，别的块没动
    }

    /// <summary>
    /// 拖一块时吸附：窗口 / 控件的边（<see cref="_snapXs"/> / <see cref="_snapYs"/>）加上
    /// **别的选区**的四条边。Ctrl 仍然临时关闭吸附（与整块移动同口径）。
    /// </summary>
    private Rect SnapMoveRegion(Rect r, int skip)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return r;

        var (otherXs, otherYs) = SelectionSet.OtherEdges(_originRegions ?? _regions, skip);
        var xs = new List<double>(_snapXs.Count + otherXs.Length);
        xs.AddRange(_snapXs);
        xs.AddRange(otherXs);
        var ys = new List<double>(_snapYs.Count + otherYs.Length);
        ys.AddRange(_snapYs);
        ys.AddRange(otherYs);
        if (xs.Count == 0 && ys.Count == 0) return r;

        double dx = NearestDelta(new[] { r.Left, r.Right }, xs);
        double dy = NearestDelta(new[] { r.Top, r.Bottom }, ys);
        return new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
    }

    /// <summary>
    /// 拖动中按住 / 松开「移动整个选区」（默认 <c>Space</c>）：换口径重来一次（上游 <c>rebaseMoveDrag</c>）。
    /// 起点改到当前指针、快照改成当前几何，所以画面不跳；松开时优先接着拖刚才那一块。
    /// </summary>
    private void RebaseMoveIfScopeChanged(Point p)
    {
        bool entire = CaptureShortcutTable.Held(CaptureShortcutScope.Screenshot, "move_entire_selection");
        if (entire == _moveEntire) return;

        int target = _dragRegion;
        if (!entire && (target < 0 || target >= _regions.Count))
            target = SelectionSet.TopmostAt(_regions, _origin, _selStyle.CornerRadius);
        if (!entire && target < 0) return;      // 松开 Space 时指针已不在任何一块上：继续整体拖

        _moveEntire = entire;
        _dragRegion = entire ? -1 : target;
        _drag = entire ? DragMode.Move : DragMode.MoveRegion;

        _originRegions = new List<SelectionRegion>(_regions);
        _originRect = SelectionSet.Bounds(_originRegions);
        _sel = _originRect;
        _origin = p;
    }

    /// <summary>按外接框的变形缩放每一块选区（与标注缩放同一口径）。</summary>
    private void ResizeFrame(Rect target) => ApplyFrame(target, region => region.Scale(_originRect, target));

    private void ApplyFrame(Rect target, Func<SelectionRegion, SelectionRegion> transform)
    {
        _regions.Clear();
        if (_originRegions != null)
            foreach (var region in _originRegions) _regions.Add(transform(region));
        _sel = target;      // 目标框就是新的外接框（再按几何算一次只会差浮点尾数）
    }

    // ============ 选区计算 ============

    private bool IsValid(Rect? r) => r is Rect s && s.Width >= MinSize && s.Height >= MinSize;

    /// <summary>
    /// 把窗口 DIP 选区同步成「屏幕物理像素 + 图像像素」写进文档（导出按它裁）。
    /// 一整块矩形只写 <see cref="CaptureDocument.Area"/>（位图本身就是它，不用裁）；
    /// 多块 / 自由 / 折线 / 挖洞写 <see cref="CaptureDocument.Regions"/>，外接框取**加区**的并集。
    /// 外接框与几何必须是**同一次换算**的产物，否则裁出来的边会差一两个像素。
    /// </summary>
    private void SyncArea()
    {
        if (_regions.Count == 0) return;

        // 一整块矩形：老路（包含两端像素 +1，导出不裁）
        if (_regions.Count == 1 && _regions[0] is { Mode: RegionMode.Add, IsPolygon: false } only)
        {
            var s = only.Box;
            var (x1, y1) = ToImage(s.TopLeft);
            var (x2, y2) = ToImage(s.BottomRight);
            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            int w = Math.Min(Math.Abs(x2 - x1) + 1, _doc.Base.PixelWidth - x);
            int h = Math.Min(Math.Abs(y2 - y1) + 1, _doc.Base.PixelHeight - y);
            if (w < 2 || h < 2) return;

            _doc.Regions = null;
            _doc.Area = new Int32Rect(_doc.Monitor.X + x, _doc.Monitor.Y + y, w, h);
            return;
        }

        int width = _doc.Base.PixelWidth, height = _doc.Base.PixelHeight;
        var regions = new SelectionRegion[_regions.Count];
        Rect box = Rect.Empty;
        for (int i = 0; i < _regions.Count; i++)
        {
            var image = _regions[i].ToImage(_doc.Origin, _doc.Scale).Clamp(width, height);
            regions[i] = image;
            if (image.Mode == RegionMode.Add) box.Union(image.Bounds);
        }

        if (box.IsEmpty) return;
        int bw = Math.Max(2, (int)Math.Round(box.Width));
        int bh = Math.Max(2, (int)Math.Round(box.Height));
        int bx = Math.Min((int)Math.Round(box.X), Math.Max(0, width - bw));
        int by = Math.Min((int)Math.Round(box.Y), Math.Max(0, height - bh));

        _doc.Regions = regions;
        _doc.Area = new Int32Rect(_doc.Monitor.X + bx, _doc.Monitor.Y + by, bw, bh);
    }

    /// <summary>
    /// 选区边框命中：**整条边都能抓**（照 Snow Shot 的边带判定，见 <see cref="MarkGeometry.HitFrameEdge"/>），
    /// 不再只有八个「看不见的把手」周围那点方块。
    /// </summary>
    /// <remarks>
    /// 减区挖掉的洞、外接框里的空角不是选区（那里按下等于「框外」：加区 / 减区 / 重框），
    /// 所以命中点落在洞里的边框一律当没命中，别拿它去改框。
    /// </remarks>
    private FrameHandle HitTest(Point p)
    {
        if (_sel is not Rect s) return FrameHandle.None;

        FrameHandle hit = MarkGeometry.HitFrameEdge(s, p, GripHit);
        if (hit is not (FrameHandle.None or FrameHandle.Move) &&
            s.Contains(p) && !SelectionSet.Contains(_regions, p, _selStyle.CornerRadius))
            return FrameHandle.None;

        return hit;
    }

    /// <summary>
    /// 选中标注的八个把手（只在「选择」工具下、且这一条能缩放时才给）。
    /// 框内返回 <see cref="FrameHandle.Move"/>，由调用方决定是拖动还是交给框选。
    /// </summary>
    private FrameHandle MarkHandleAt(Point p)
    {
        if (_tool != CaptureTool.Select) return FrameHandle.None;
        if (_selected.Count != 1) return FrameHandle.None;      // 多选不给把手（整体缩放还没做）
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return FrameHandle.None;

        var mark = _doc.Marks[_selectedIndex];
        if (!mark.CanScale) return FrameHandle.None;

        return MarkGeometry.HitFrame(MarkGeometry.PaddedFrame(mark.Frame), p, GripHit);
    }

    private static Cursor CursorFor(FrameHandle h) => h switch
    {
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        FrameHandle.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private Rect MoveTo(Rect r, Vector delta)
    {
        double x = Math.Clamp(r.X + delta.X, 0, Math.Max(0, ActualWidth - r.Width));
        double y = Math.Clamp(r.Y + delta.Y, 0, Math.Max(0, ActualHeight - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>选区改框：纯几何在 <see cref="MarkGeometry.ResizeFrame"/>，这里只补「别跑出窗口」。</summary>
    private Rect ResizeFrom(Rect r, FrameHandle h, Vector d) =>
        MarkGeometry.ResizeFrame(r, h, d, MinSize, new Rect(0, 0, ActualWidth, ActualHeight));

    /// <summary>
    /// 这个把手现在能不能抓（改选区大小）：八个方向把手在「选择」工具下永远能抓，
    /// 其它工具要设置里开着「快速选择修改」（照上游 <c>dragModeForVirtualPosition</c>：
    /// <c>activeTool != Move &amp;&amp; !quickSelectionModification</c> = 完全不给拖）。
    /// </summary>
    internal static bool CanDragFrameHandle(FrameHandle handle, CaptureTool tool, bool quickModification) =>
        handle is not (FrameHandle.None or FrameHandle.Move) &&
        (tool == CaptureTool.Select || quickModification);

    private bool CanDragFrameHandle(FrameHandle handle) =>
        CanDragFrameHandle(handle, _tool, ScreenshotBehavior.QuickSelectionModification);

    /// <summary>
    /// 起拖时把被抓的那条边贴到指针上（照上游 <c>grabAdjustedScreenshotSelectionRect</c>，
    /// <c>selection_resize_mode = follow_mouse_position</c>）。
    /// </summary>
    /// <remarks>
    /// 指针先按像素取整，边界按「越过锚定边就 +1」算（<c>draggedEdgeBoundary</c>）——
    /// 所以拖动时抓到的那条边正好落在指针所在的那一像素格的外沿，不会差半格。
    /// 之后把框翻正、夹进 <paramref name="bounds"/>、补齐最小尺寸。
    /// </remarks>
    internal static Rect GrabAdjusted(Rect selection, FrameHandle handle, Point position, Rect bounds,
        double minimum)
    {
        // 上游用 std::round（半数远离零），不是 C# 默认的银行家舍入
        double cx = Math.Round(position.X, MidpointRounding.AwayFromZero);
        double cy = Math.Round(position.Y, MidpointRounding.AwayFromZero);

        static double Edge(double cell, double anchored) => cell >= anchored ? cell + 1 : cell;

        double left = selection.Left, top = selection.Top;
        double right = selection.Right, bottom = selection.Bottom;

        switch (handle)
        {
            case FrameHandle.TopLeft:
                left = Edge(cx, right); top = Edge(cy, bottom);
                break;
            case FrameHandle.Top:
                top = Edge(cy, bottom);
                break;
            case FrameHandle.TopRight:
                right = Edge(cx, left); top = Edge(cy, bottom);
                break;
            case FrameHandle.Right:
                right = Edge(cx, left);
                break;
            case FrameHandle.BottomRight:
                right = Edge(cx, left); bottom = Edge(cy, top);
                break;
            case FrameHandle.Bottom:
                bottom = Edge(cy, top);
                break;
            case FrameHandle.BottomLeft:
                left = Edge(cx, right); bottom = Edge(cy, top);
                break;
            case FrameHandle.Left:
                left = Edge(cx, right);
                break;
            default:
                return selection;
        }

        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        left = Math.Clamp(left, bounds.Left, bounds.Right);
        right = Math.Clamp(right, bounds.Left, bounds.Right);
        top = Math.Clamp(top, bounds.Top, bounds.Bottom);
        bottom = Math.Clamp(bottom, bounds.Top, bounds.Bottom);

        double width = Math.Max(minimum, right - left);
        double height = Math.Max(minimum, bottom - top);
        return new Rect(left, top, width, height);
    }


    private static Rect RectFrom(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>按住 Shift 时把拖拽终点修正成正方形（保持拖拽方向）。</summary>
    private static Point SquareOf(Point origin, Point end)
    {
        double dx = end.X - origin.X;
        double dy = end.Y - origin.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (side < 1) return end;
        return new Point(origin.X + (dx < 0 ? -side : side), origin.Y + (dy < 0 ? -side : side));
    }
}
