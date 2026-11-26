using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「拖动收尾 / 光标 / 形状条命中」部分。</summary>
public partial class CaptureOverlayWindow
{
    private void UpdateNewDrag(Point p)
    {
        // 拖出来的边角吸附到附近窗口 / 控件的边（Ctrl 临时关闭）；还没有选区时 Shift 锁正方形。
        // 锁了比例时**不走吸附**（上游同口径：锁定几何自己按比例推，被边吸一下就变形）。
        double ratio = DragRatio(marquee: true);
        if (ratio > 0)
        {
            _liveRect = AspectLock.Marquee(_origin, p,
                new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), ratio);
        }
        else
        {
            Point end = SnapPoint(p);
            if (_squareDrag) end = SquareOf(_origin, end);
            _liveRect = RectFrom(_origin, end);
        }
        RefreshSelection();
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_scrollMode) return;

        // 比例下拉：按下只记一笔，抬起才真正打开（否则这一下 MouseUp 会把它关掉）
        if (_pendingAspectMenu)
        {
            _pendingAspectMenu = false;
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenAspectMenu();
                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        Point p = Clamp(e.GetPosition(Layer));
        DragMode mode = _drag;

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }

        bool fresh = false;      // 这一下真的定下了一块新选区（快捷入口要据此执行）

        switch (mode)
        {
            case DragMode.New:
            {
                var box = _liveRect ?? Rect.Empty;
                _liveRect = null;

                // 几乎没移动 = 单击：选中光标下的窗口 / 控件（整块换掉）；拖动了就是框选
                if (!_pressMoved && _pressTarget is SnapBox target)
                {
                    SetSingleRect(ToDipRect(target.Bounds));
                    fresh = true;
                }
                else if (!CommitBox(box)) RefreshSelection();   // 太小 / 挖空 / 点空白：保留原选区
                else fresh = true;
                FinalizeAspect();                                // 锁着但还没定比例：取这一块的比例
                break;
            }

            case DragMode.Lasso:
                fresh = CommitLasso();
                break;

            case DragMode.Move:
            case DragMode.MoveRegion:
                // 从压暗的阴影上按下去、几乎没移动 = 单击：在阴影上点一下什么都别做
                // （以前这条路径走「拖出去整块重框」里的单击分支，那里也是保留原选区）。
                // 没有这句的话，按下时抖的那一两像素会留在选区上。
                if (!_pressMoved && _moveFromShadow) MoveFrame(_originRect);
                break;

            case DragMode.Resize:
                FinalizeAspect();       // 预设与结果对不上就降级成自定义比例
                break;

            case DragMode.Draw:
                FinishToolDrag(p);
                break;

            case DragMode.Mark:
                CommitMarkDrag(p);
                break;

            case DragMode.MarkResize:
                CommitMarkResize(p);
                break;

            case DragMode.MagnifierLens:
                CommitMagnifierLensDrag(p);
                break;

            case DragMode.Marquee:
                CommitMarkMarquee(p);
                break;

            case DragMode.SmartPress:
            {
                // 松手还在预选里 = 确认这一块；否则重新命中（上游 handleIntelligentSelectionRelease）
                Rect pressed = _smart.TakePressSelection();
                Rect target = SmartSelectionTransition.HasSelection(pressed)
                    ? pressed
                    : _smart.CurrentSelection;

                if (SmartSelectionTransition.HasSelection(target) && target.Contains(p))
                {
                    ConfirmSmartSelection(target);
                    fresh = true;
                }
                else
                    UpdateHover(p);
                break;
            }
        }

        _pressTarget = null;
        _moveFromShadow = false;
        _originRegions = null;
        _replaceOnCommit = false;
        _dragRegion = -1;
        _moveEntire = false;
        if (_tool == CaptureTool.AutoFilter) SyncAutoFilter(detect: true);   // 选区变了就重新识别
        RefreshCursor(p);
        UpdateChrome();
        e.Handled = true;

        // 快捷入口：选区一落定就执行它的默认动作（普通截图 HasPending = false，不受影响）
        if (fresh)
        {
            RememberRecentSelection();
            FirePendingAction();
        }
    }

    /// <summary>放弃当前手势：画到一半的丢掉，改框 / 移动就还原成按下前的选区。</summary>
    private void CancelDrag()
    {
        bool wasFrame = _drag is DragMode.New or DragMode.Move or DragMode.MoveRegion or DragMode.Resize
            or DragMode.Lasso;

        if (_drag == DragMode.Mark) CancelMarkDrag();
        else if (_drag == DragMode.MarkResize) CancelMarkResize();
        else if (_drag == DragMode.MagnifierLens) CancelMagnifierLensDrag();
        else if (_drag == DragMode.Marquee) CancelMarkMarquee();
        else if (_drag is DragMode.Draw or DragMode.Erase) Surface.Preview = null;
        else if (_drag == DragMode.Lasso) _lasso.Clear();
        else if (_drag == DragMode.SmartPress) _smart.ClearPress();

        if (_tool == CaptureTool.AutoFilter) CancelAutoFilterGesture();

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        _pressTarget = null;

        if (wasFrame) RestoreRegions();

        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        UpdateCursorForTool();
        UpdateChrome();
    }

    /// <summary>光标形状：选择工具看边框 / 标注，文字是 I 形，其余是十字。</summary>
    private void RefreshCursor(Point p)
    {
        if (_textEditing) return;

        // 用户按了「显示 / 隐藏光标」：这一层就别再按工具改回箭头 / 十字
        if (_cursorHidden)
        {
            Cursor = Cursors.None;
            return;
        }

        // 智能选区预选态：光标就是「十字 + 单击确认」，不该出现缩放 / 移动的箭头
        if (SmartPreviewActive)
        {
            Cursor = Cursors.Cross;
            return;
        }

        FrameHandle hit = HitTest(p);
        FrameHandle markHit = MarkHandleAt(p);

        // 选中标注的把手比框选的边更「当前」：先把光标摆对，不然看不出能拉
        if (markHit is not (FrameHandle.None or FrameHandle.Move))
        {
            Cursor = FrameCursors.For(markHit, Cursors.Cross, Cursors.SizeAll);
            return;
        }

        // 放大镜的镜头 / 移动手柄也是抓手（源区域 = 整块挪动，由下面的通用分支给 SizeAll）
        if (_tool == CaptureTool.Select && _selectedIndex >= 0 && _selectedIndex < _doc.Marks.Count &&
            _doc.Marks[_selectedIndex] is Mark.Magnifier lensMag &&
            (MagnifierInk.HitsMoveGrip(lensMag.Lens, p) ||
             (lensMag.Lens.Contains(p) && !lensMag.SourceRect.Contains(p))))
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        // **任何工具**下边框都能抓（按下就改框），光标也就跟着变——否则拿着画笔
        // 走到边上还是十字，看不出那里能拉伸。关掉「快速选择修改」时非「选择」工具不给抓。
        if (CanDragFrameHandle(hit))
        {
            Cursor = FrameCursors.For(hit, Cursors.Cross, Cursors.SizeAll);
            return;
        }

        Cursor = _tool switch
        {
            CaptureTool.Select when hit == FrameHandle.Move => Cursors.SizeAll,
            // 单块矩形 + 选择工具：外面那圈阴影也是「挪整块」的抓手（见 SelectionSet.MovesOnOutsideDrag），
            // 光标跟着说清楚——否则用户只会看到箭头，不知道那里能拖
            CaptureTool.Select when SelectionSet.MovesOnOutsideDrag(_regions, true, false, false) => Cursors.SizeAll,
            CaptureTool.Select when !IsValid(_sel) => Cursors.Cross,
            CaptureTool.Select => _doc.HitTest(p, MarkHitSlop) >= 0 ? Cursors.SizeAll : Cursors.Arrow,
            CaptureTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    /// <summary>事件源是否来自「选区形状」那条（点它不能当成开始框选）。</summary>
    private bool IsOnShapeBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ShapeBar)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

}
