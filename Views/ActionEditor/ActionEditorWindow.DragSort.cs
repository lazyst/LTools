using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Migration;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 拖动排序
/// </summary>
public partial class ActionEditorWindow
{
    // ============ 拖动排序 ============

    /// <summary>
    /// 按住某一步就开始「可能的拖动」；超过 4px 才真正进入拖动（照全项目统一的拖动手感）。
    /// 只移动被拖的那一步（子步骤跟着走），落在别的分支里就是「移入该分支」。
    /// </summary>
    private void StepList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<Button>((DependencyObject)e.OriginalSource) != null) return;

        StepRow? row = RowAt(e.OriginalSource as DependencyObject);
        if (row?.Step == null || row.Owner == null) return;   // 标记行 / 空分支占位行不拖

        _dragRow = row;
        _dragRowIndex = _rows.IndexOf(row);
        _dragOrigin = e.GetPosition(this);
        _dragPending = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending && !_dragging) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        Point p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragOrigin.X) < 4 && Math.Abs(p.Y - _dragOrigin.Y) < 4) return;
            if (!StartDrag(p)) return;
        }

        _dragGhost.MoveGhost(p);
        UpdateDropTarget(p);
        e.Handled = true;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (!_dragging)
        {
            _dragPending = false;
            _dragRow = null;
            return;
        }

        StepRow? row = _dragRow;
        var target = _dropTarget;
        EndDragVisuals();

        if (row?.Step != null && row.Owner != null && target is { } t)
            MoveStepTo(row, t.List, t.Index);

        _dragRow = null;
        e.Handled = true;
    }

    private void OnDragKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_unitPending || _unitDragging)
        {
            CancelUnitDrag();
            e.Handled = true;
            return;
        }

        if (!_dragPending && !_dragging) return;
        CancelDrag();
        e.Handled = true;
    }

    private bool StartDrag(Point p)
    {
        if (_dragRow == null) return false;
        if (StepList.ItemContainerGenerator.ContainerFromIndex(_dragRowIndex) is not ListBoxItem row)
        {
            _dragPending = false;
            return false;
        }

        _dragGhost.ShowGhost(row, p);
        row.Opacity = 0.35;
        StepList.SelectedItem = _dragRow;
        _dragPending = false;
        _dragging = true;
        return true;
    }

    private void UpdateDropTarget(Point windowPoint)
    {
        if (!TryResolveDrop(windowPoint, _dragRow?.Step, out DropSpot spot))
        {
            _dropTarget = null;
            HideInsertLine();
            return;
        }

        _dropTarget = (spot.List, spot.Index);
        ShowInsertLine(spot);
    }

    /// <summary>落点：某个列表的第 index 项之前，行 / 缩进 / 插入线画在哪。</summary>
    private readonly record struct DropSpot(List<Step> List, int Index, int Depth, ListBoxItem? Container, bool After);

    /// <summary>
    /// 由窗口坐标算出落点（拖步骤与从工具箱拖单元共用）。<paramref name="moving"/> 是正在被拖的步骤，
    /// 用来拦住「把一步拖进它自己的分支」；从工具箱拖时为 null。
    /// </summary>
    private bool TryResolveDrop(Point windowPoint, Step? moving, out DropSpot spot)
    {
        spot = default;
        Point listPos = TranslatePoint(windowPoint, StepList);
        if (listPos.X < 0 || listPos.Y < 0
            || listPos.X > StepList.ActualWidth || listPos.Y > StepList.ActualHeight) return false;

        ListBoxItem? container = null;
        if (StepList.InputHitTest(listPos) is DependencyObject hit
            && ItemsControl.ContainerFromElement(StepList, hit) is ListBoxItem found)
            container = found;

        if (container == null)
        {
            // 行下面的空白处 = 放到动作末尾（空动作也走这里）。
            spot = new DropSpot(_program.Steps, _program.Steps.Count, 0, null, After: true);
            return true;
        }

        int hover = StepList.ItemContainerGenerator.IndexFromContainer(container);
        if (hover < 0 || hover >= _rows.Count) return false;

        StepRow target = _rows[hover];
        List<Step> list;
        int index;
        bool after;

        if (target.Step != null && target.Owner != null)
        {
            after = listPos.Y
                    >= container.TranslatePoint(new Point(0, 0), StepList).Y + container.ActualHeight / 2;
            list = target.Owner;
            index = target.Index + (after ? 1 : 0);
        }
        else if (target.IsPlaceholder && target.Owner != null)
        {
            after = false;   // 空分支：放到分支里
            list = target.Owner;
            index = 0;
        }
        else if (target.IsMarker && target.Owner != null)
        {
            after = false;   // 否则 / 结束如果 / 结束循环：落到它标记的那个位置
            list = target.Owner;
            index = target.Index;
        }
        else
        {
            return false;    // 没有归属的标记行不是落点
        }

        // 不能把一步拖进它自己的分支里
        if (moving != null && ContainsList(moving, list)) return false;

        spot = new DropSpot(list, index, target.Depth, container, after);
        return true;
    }

    private void ShowInsertLine(DropSpot spot)
    {
        _insertLine ??= new Border
        {
            Height = 2,
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(1),
        };
        if (!GhostLayer.Children.Contains(_insertLine)) GhostLayer.Children.Add(_insertLine);

        if (spot.Container is { } container)
        {
            double indent = spot.Depth * 18;
            Point origin = container.TranslatePoint(new Point(0, 0), GhostLayer);
            _insertLine.Width = Math.Max(12, container.ActualWidth - indent);
            Canvas.SetLeft(_insertLine, origin.X + indent);
            Canvas.SetTop(_insertLine, origin.Y + (spot.After ? container.ActualHeight : 0) - 1);
            return;
        }

        // 空动作 / 行下面的空白：贴列表顶（空）或底（末尾）
        Point listOrigin = StepList.TranslatePoint(new Point(0, 0), GhostLayer);
        _insertLine.Width = Math.Max(12, StepList.ActualWidth - 12);
        Canvas.SetLeft(_insertLine, listOrigin.X + 6);
        Canvas.SetTop(_insertLine, listOrigin.Y
            + (_rows.Count == 0 ? 4 : Math.Max(0, StepList.ActualHeight - 2)));
    }

    private void HideInsertLine()
    {
        if (_insertLine == null) return;
        GhostLayer.Children.Remove(_insertLine);
        _insertLine = null;
    }

    private void EndDragVisuals()
    {
        _dragGhost.End();
        HideInsertLine();
        _dropTarget = null;
        _dragging = false;
        _dragPending = false;

        // 容器会被回收复用，逐行复位不透明度
        for (int i = 0; i < _rows.Count; i++)
            if (StepList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem row)
                row.Opacity = 1;
    }

    private void CancelDrag()
    {
        EndDragVisuals();
        _dragRow = null;
    }

    private void MoveStepTo(StepRow row, List<Step> list, int index)
    {
        Step step = row.Step!;
        List<Step> from = row.Owner!;
        int fromIndex = row.Index;

        if (ContainsList(step, list)) return;   // 不能把一步挪进它自己的分支

        int insert = index;
        if (ReferenceEquals(from, list) && insert > fromIndex) insert--;
        if (ReferenceEquals(from, list) && insert == fromIndex) return;

        PushUndo("拖动步骤");
        from.RemoveAt(fromIndex);
        list.Insert(Math.Clamp(insert, 0, list.Count), step);
        ReloadRows(step);
    }

    /// <summary>该步（或它的任意后代）的分支列表是不是 <paramref name="list"/>。</summary>
    private static bool ContainsList(Step step, List<Step> list)
    {
        if (ReferenceEquals(step.Then, list) || ReferenceEquals(step.Else, list)
            || ReferenceEquals(step.Body, list)) return true;

        foreach (var child in step.Then) if (ContainsList(child, list)) return true;
        foreach (var child in step.Else) if (ContainsList(child, list)) return true;
        foreach (var child in step.Body) if (ContainsList(child, list)) return true;

        return false;
    }

    private StepRow? RowAt(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem)
        {
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        if (source is not ListBoxItem item) return null;
        int index = StepList.ItemContainerGenerator.IndexFromContainer(item);
        return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }
}
