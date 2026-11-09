using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 从工具箱把单元拖进步骤列表：落到行间 / 空分支 / 末尾，松手按默认参数直接插入。
/// </summary>
/// <remarks>
/// 与步骤拖动共用同一套落点计算（<see cref="TryResolveDrop"/>）和插入线，所以「拖到哪」在两处含义一致；
/// 拖动阈值同样是 4px，Esc 取消，靠近列表上下边缘自动滚动。
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>按下工具箱里的单元：先记着，移动超过 4px 才算拖动（点击仍然走「填参数」）。</summary>
    private void BeginUnitPress(FrameworkElement source, UnitSpec spec, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _unitSpec = spec;
        _unitSource = source;
        _unitOrigin = e.GetPosition(this);
        _unitPoint = _unitOrigin;
        _unitPending = true;
    }

    private void OnUnitDragMove(object sender, MouseEventArgs e)
    {
        if (!_unitPending && !_unitDragging) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelUnitDrag();
            return;
        }

        Point p = e.GetPosition(this);
        _unitPoint = p;

        if (!_unitDragging)
        {
            if (Math.Abs(p.X - _unitOrigin.X) < 4 && Math.Abs(p.Y - _unitOrigin.Y) < 4) return;
            if (_unitSpec == null || _unitSource == null) { _unitPending = false; return; }

            _dragGhost.ShowGhost(_unitSource, p);
            _unitPending = false;
            _unitDragging = true;

            // 按钮在按下时捕获了鼠标；窗口层把 mouseup 吞掉后它永远等不到释放，
            // 于是**下一次点击**会被它当成自己的松手，补一个 Click（弹出该单元的参数表单）。
            // 进入拖动就交还捕获，并让这次按下不再算点击。
            _suppressUnitClick = true;
            Mouse.Capture(null);

            StartUnitAutoScroll();
        }

        _dragGhost.MoveGhost(p);
        UpdateUnitDrop(p);
        e.Handled = true;
    }

    private void OnUnitDragUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (!_unitDragging)
        {
            _unitPending = false;
            _unitSpec = null;
            _unitSource = null;
            return;
        }

        UnitSpec? spec = _unitSpec;
        DropSpot? spot = _unitDrop;

        // 这次 mouseup 之后 Button 还会补一个 Click：拖完就别再加一步了。
        _suppressUnitClick = true;
        Dispatcher.BeginInvoke(new Action(() => _suppressUnitClick = false),
            DispatcherPriority.Background);
        EndUnitDragVisuals();

        if (spec != null && spot is { } target)
            AddStepAt(spec.Key, target.List, target.Index, direct: true);

        _unitSpec = null;
        _unitSource = null;
        e.Handled = true;
    }

    private void CancelUnitDrag()
    {
        EndUnitDragVisuals();
        _suppressUnitClick = false;   // Esc 取消：别让下一次正常点击被吞掉
        _unitSpec = null;
        _unitSource = null;
    }

    private void EndUnitDragVisuals()
    {
        _dragGhost.End();
        HideInsertLine();
        _unitDrop = null;
        _unitDragging = false;
        _unitPending = false;
        StopUnitAutoScroll();
    }

    private void UpdateUnitDrop(Point windowPoint)
    {
        if (!TryResolveDrop(windowPoint, moving: null, out DropSpot spot))
        {
            _unitDrop = null;
            HideInsertLine();
            return;
        }

        _unitDrop = spot;
        ShowInsertLine(spot);
    }

    // ============ 拖到边缘自动滚动 ============

    private void StartUnitAutoScroll()
    {
        _unitScroll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _unitScroll.Tick -= UnitScroll_Tick;
        _unitScroll.Tick += UnitScroll_Tick;
        _unitScroll.Start();
    }

    private void StopUnitAutoScroll() => _unitScroll?.Stop();

    private void UnitScroll_Tick(object? sender, EventArgs e)
    {
        if (!_unitDragging) { StopUnitAutoScroll(); return; }
        if (Mouse.LeftButton != MouseButtonState.Pressed) { CancelUnitDrag(); return; }

        ScrollViewer? scroll = Descendants<ScrollViewer>(StepList).FirstOrDefault();
        if (scroll == null || StepList.ActualHeight <= 0) return;

        Point p = Mouse.GetPosition(StepList);
        double delta = p.Y < 30 ? -18 : p.Y > StepList.ActualHeight - 30 ? 18 : 0;
        if (delta == 0) return;

        double before = scroll.VerticalOffset;
        scroll.ScrollToVerticalOffset(before + delta);
        if (Math.Abs(scroll.VerticalOffset - before) > 0.5) UpdateUnitDrop(_unitPoint);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
