using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「多块选区」部分。</summary>
public partial class CaptureOverlayWindow
{
    // ============ 多区域选区：加区 / 减区 ============

    /// <summary>
    /// 画面 / 导出该按哪几块算：已确定的选区 + 正在拖的那一块（矩形 / 自由圈）。
    /// 折线还在逐点单击时不算（它画的是 <see cref="PendingShape"/> 那条虚线）；
    /// 「整块重框」的手势里先不带上旧选区，松手才换。
    /// </summary>
    private List<SelectionRegion> LiveRegions()
    {
        var list = new List<SelectionRegion>(_regions.Count + 1);

        if (!_replaceOnCommit) list.AddRange(_regions);

        if (_drag == DragMode.New && _liveRect is Rect rect && rect.Width >= 0.5 && rect.Height >= 0.5)
            list.Add(SelectionRegion.FromRect(_mode, rect));

        if (_drag == DragMode.Lasso && _lasso.Count >= SelectionShape.MinPoints)
            list.Add(SelectionRegion.FromPoints(_mode, _lasso, smooth: true));

        return list;
    }

    /// <summary>
    /// 选区合起来的几何（窗口 DIP，含正在拖的那块）：加区并、减区挖。
    /// 减区把选区挖空 = null（画面不再挖洞，导出也拿不到内容）。
    /// </summary>
    private Geometry? SelectionGeometry() => SelectionSet.Combine(LiveRegions(), _selStyle.CornerRadius);

    /// <summary>按选区重算外接框（把手 / 尺寸条都挂在它上面）。</summary>
    private void RefreshSelection()
    {
        Rect box = SelectionSet.Bounds(LiveRegions());
        _sel = box.IsEmpty || box.Width < 0.5 || box.Height < 0.5 ? null : box;
    }

    /// <summary>把选区退回按下那一刻的样子（取消拖动 / 换工具 / 翻历史）。</summary>
    private void RestoreRegions()
    {
        if (_originRegions != null)
        {
            _regions.Clear();
            _regions.AddRange(_originRegions);
        }

        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = null;
        _lasso.Clear();
        _dragRegion = -1;
        _moveEntire = false;
        RefreshSelection();
    }

    /// <summary>
    /// 从采集历史重新裁选：把当时那几块选区摆回来（窗口 DIP）。
    /// 交接前还没有底图上的任何标注，所以只还原几何、不动 <c>_doc</c>。
    /// </summary>
    internal void LoadSelection(IReadOnlyList<SelectionRegion> regions)
    {
        _regions.Clear();
        _regions.AddRange(regions);

        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = null;
        _lasso.Clear();
        _dragRegion = -1;
        _moveEntire = false;
        _drag = DragMode.None;
        _handle = FrameHandle.None;

        RefreshSelection();
        UpdateChrome();
    }

    /// <summary>
    /// 把一块并进选区。<paramref name="region"/> 是减区且会把选区挖空时**不并、返回 false**——
    /// 别留下「有选区、却什么都导不出来」的状态。
    /// </summary>
    private bool Commit(SelectionRegion region)
    {
        if (region.Mode == RegionMode.Subtract && WouldEmpty(region))
        {
            ToastService.Show("减区把选区挖空了，这一块没并进去", null, 2200, ToastKind.warning,
                key: "capture-region-empty");
            return false;
        }

        if (_replaceOnCommit) _regions.Clear();     // 无修饰键拖到选区外 = 整块重框
        _replaceOnCommit = false;
        _regions.Add(region);
        RefreshSelection();
        return true;
    }

    /// <summary>落一块矩形选区：太小 / 减区挖空都不落。</summary>
    private bool CommitBox(Rect box) => IsValid(box) && Commit(SelectionRegion.FromRect(_mode, box));

    /// <summary>这一块减区会不会把选区整个挖空。</summary>
    private bool WouldEmpty(SelectionRegion region)
    {
        var probe = new List<SelectionRegion>(_regions.Count + 1);
        probe.AddRange(_regions);
        probe.Add(region);
        return SelectionSet.Combine(probe) == null;
    }

    /// <summary>
    /// 全屏：选区换成整块画面（Ctrl+A 与工具条「全屏」共用）。
    /// 折线还没收尾的先取消，否则下一段点会接到旧折线上。
    /// </summary>
    private void SelectWholeScreen()
    {
        _drag = DragMode.None;
        CancelSelectionPolyline();
        EndSmartPreviewKeepingSelection();     // 全屏 = 明确的选区，智能选区暂停
        SetSingleRect(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        UpdateChrome();
    }

    /// <summary>整块换成一块矩形选区（单击选中窗口 / 控件、Ctrl+A 全屏、退出历史都用它）。</summary>
    private void SetSingleRect(Rect rect)
    {
        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = null;
        _lasso.Clear();
        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;

        _regions.Clear();
        _regions.Add(SelectionRegion.FromRect(RegionMode.Add, rect));
        RefreshSelection();
    }

    /// <summary>把每一块选区整体平移到目标框（按下那一刻的框 → 目标框）。</summary>
    private void MoveFrame(Rect target)
    {
        var delta = new Vector(target.X - _originRect.X, target.Y - _originRect.Y);
        ApplyFrame(target, region => region.Offset(delta));
    }

    /// <summary>
    /// 只挪命中的那一块（多块选区里默认的手势）：别的块原地不动，外接框按并集重算。
    /// </summary>
    /// <remarks>
    /// 位移按**这一块自己的**外接框算（不是整个选区的并集）：并集可能横跨整个屏幕，
    /// 拿它钳「别跑出窗口」会让小块明明还有空间却动不了。
    /// 吸附除了窗口 / 控件边，还吸**别的选区**的四条边（上游 <c>beginRegionMove</c> 的 references）。
    /// </remarks>
}
