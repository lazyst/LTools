using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「选区形状（矩形 / 折线 / 自由）」部分。</summary>
public partial class CaptureOverlayWindow
{
    // ============ 选区形状：矩形 / 自由 / 折线 ============

    /// <summary>形状条上的一下：换「下一块的形状」（已有选区不动，加区 / 减区可以是不同形状）。</summary>
    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<SelectionMode>(tag, out var mode)) return;
        SetSelectionShape(mode);
    }

    private void SetSelectionShape(SelectionMode mode)
    {
        if (_shape == mode) return;
        _shape = mode;
        CancelSelectionPolyline();      // 正在点的折线还没成选区，跟着换形状作废
        SelectionStore.SetRegionType(mode);   // 跨会话记住（照上游 screenshot_selection/region_type）
        UpdateChrome();
    }

    /// <summary>折线选区正在逐点单击（这时还没形成选区，工具栏不该出现）。</summary>
    private bool SelectionPending => _selPolyActive;

    /// <summary>画面上要画的那条「还没闭合」的虚线：折线的已落点 + 跟手的那一段（套索由多边形代画）。</summary>
    private Point[]? PendingShape()
    {
        if (!_selPolyActive) return null;

        var list = new List<Point>(_selPoly);
        if (_selPolyCursor is Point cursor) list.Add(cursor);
        return list.Count >= 1 ? list.ToArray() : null;
    }

    /// <summary>形状条上点亮当前形状（程序化改 IsChecked 不会触发 Click）。</summary>
    private void SyncShapeButtons()
    {
        ShapeRect.IsChecked = _shape == SelectionMode.Rect;
        ShapePolyline.IsChecked = _shape == SelectionMode.Polyline;
        ShapeCurve.IsChecked = _shape == SelectionMode.Curve;
        ShapeLasso.IsChecked = _shape == SelectionMode.Lasso;
    }

    /// <summary>
    /// 折线选区：单击补一个点。双击（第二下的 <c>ClickCount == 2</c>）或点回起点附近就收尾，
    /// 与标注的折线手势同一套习惯。
    /// </summary>
    private void AddSelectionPoint(Point p, int clickCount)
    {
        if (clickCount >= 2 && _selPoly.Count >= 1)
        {
            CompleteSelectionPolyline();
            return;
        }

        if (!_selPolyActive) _selPoly.Clear();   // 这一串点属于同一块，加 / 减由按下时的修饰键定

        // 点回第一个点附近 = 闭合（截图 / 画图工具的习惯，省得去双击）
        if (_selPoly.Count >= SelectionShape.MinPoints && (p - _selPoly[0]).Length <= 6)
        {
            CompleteSelectionPolyline();
            return;
        }

        _selPoly.Add(p);
        _selPolyCursor = null;
        _selPolyActive = true;
        UpdateChrome();
    }

    private void CompleteSelectionPolyline()
    {
        bool smooth = _shape == SelectionMode.Curve;   // 曲线 = 同样的逐点单击，收尾时平滑成贝塞尔
        var points = _selPoly.ToArray();
        bool ok = SelectionShape.IsUsable(points, MinSize);

        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;

        if (ok)
        {
            Commit(SelectionRegion.FromPoints(_mode, points, smooth));
        }
        else
        {
            _replaceOnCommit = false;      // 这一笔作废，别把「整块重框」留给下一次
            RefreshSelection();
            ToastService.Show(SelectionRegionTypes.Label(_shape) +
                "选区至少要围出一小块：多点几个点再双击结束", null, 2000,
                ToastKind.info, key: "capture-shape-small");
        }

        UpdateChrome();
        if (ok) FirePendingAction();       // 快捷入口：折线 / 曲线定下来也算「选区落定」
    }

    /// <summary>丢掉正在点的折线选区（Esc / 换形状 / 翻历史）。</summary>
    private void CancelSelectionPolyline()
    {
        _replaceOnCommit = false;
        if (!_selPolyActive) return;
        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;
        Surface.InvalidateVisual();
        RefreshSelection();
    }

    /// <summary>套索松手：够大就并进选区（形状按点列裁），太小就当没框过。返回 true = 真的定下了一块。</summary>
    private bool CommitLasso()
    {
        var raw = _lasso.ToArray();
        _lasso.Clear();

        // 自由绘制：先按上游口径化简（RDP，0.125 DIP），再平滑成曲线
        var points = SelectionShape.Simplify(raw, SelectionShape.SimplifyTolerance);
        if (!SelectionShape.IsUsable(points, MinSize)) points = raw;

        if (SelectionShape.IsUsable(points, MinSize))
        {
            Commit(SelectionRegion.FromPoints(_mode, points, smooth: true));
            return true;
        }

        _replaceOnCommit = false;
        RefreshSelection();
        return false;
    }

}
