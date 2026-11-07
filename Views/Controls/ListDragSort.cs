using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LTools.Views.Controls;

/// <summary>
/// 列表拖动排序（照 Quicker）：超过 4px 才进入拖动；源行变成虚线占位、快照幽灵跟随光标；
/// 光标越过半行就实时把源项插到新位置，其余行做 220ms 让位动画；Esc 取消并还原顺序。
/// </summary>
/// <remarks>
/// 与模型解耦：<c>move</c> 回调负责真正移动数据并返回源项的新下标；<c>snapshot</c> / <c>restore</c>
/// 负责取消时还原（各列表的数据结构不同）。拖动期间每次插入都会调用 <c>move</c>，所以是实时预览。
/// </remarks>
internal sealed class ListDragSort
{
    private const double DragThreshold = 4;

    private readonly Window _window;
    private readonly Canvas _layer;
    private readonly ListBox _list;
    private readonly Func<int> _count;
    private readonly Func<int, bool>? _canDrag;
    private readonly Func<int, int, int> _move;   // (源下标, 插入位置) -> 源项新下标；-1 = 未变更
    private readonly Action _snapshot;
    private readonly Action _restore;
    private readonly Action? _changed;
    private readonly DragVisuals _visuals;

    private Point _origin;
    private int _srcIndex = -1;
    private int _curIndex = -1;
    private bool _pending;
    private bool _dragging;

    private Rectangle? _hole;
    private ListBoxItem? _hiddenRow;

    public bool IsDragging => _dragging;

    public bool IsActive => _pending || _dragging;

    public ListDragSort(
        Window window, Canvas layer, ListBox list,
        Func<int> count, Func<int, int, int> move,
        Action snapshot, Action restore, Action? changed = null,
        Func<int, bool>? canDrag = null)
    {
        _window = window;
        _layer = layer;
        _list = list;
        _count = count;
        _move = move;
        _snapshot = snapshot;
        _restore = restore;
        _changed = changed;
        _canDrag = canDrag;
        _visuals = new DragVisuals(window, layer);

        window.PreviewMouseMove += OnWindowMouseMove;
        window.PreviewMouseLeftButtonUp += OnWindowMouseUp;
        window.PreviewKeyDown += OnWindowKeyDown;
    }

    /// <summary>在列表行的 PreviewMouseLeftButtonDown 里调用（不吞事件，点击 / 双击照常）。</summary>
    public void Press(int index, Point windowPoint)
    {
        if (IsActive) Cancel();
        if (index < 0 || index >= _count()) return;
        if (_canDrag?.Invoke(index) == false) return;

        _srcIndex = _curIndex = index;
        _origin = windowPoint;
        _pending = true;
        _dragging = false;
    }

    /// <summary>没有拖动、只是点了下：清掉待定状态。</summary>
    public void ReleaseWithoutDrag()
    {
        if (_pending && !_dragging) ClearPending();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsActive) return;

        // 拖动中左键已松开但还没收到 MouseUp：等 MouseUp（窗口持有捕获，事件一定会到）。
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (!_dragging) ClearPending();
            return;
        }

        var p = e.GetPosition(_window);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _origin.X) < DragThreshold && Math.Abs(p.Y - _origin.Y) < DragThreshold)
                return;
            if (!Start(p)) return;
        }

        _visuals.MoveGhost(p);
        UpdateInsertion(p);
        e.Handled = true;
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_dragging)
        {
            End();
            _changed?.Invoke();
            e.Handled = true;
        }
        else
        {
            ClearPending();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsActive) return;
        Cancel();
        e.Handled = true;
    }

    // ============ 拖动生命周期 ============

    private bool Start(Point p)
    {
        var row = ContainerAt(_curIndex);
        if (row == null) { ClearPending(); return false; }

        _snapshot();
        _visuals.ShowGhost(row, p);

        _hole = DragFx.CreateHole(6);
        _layer.Children.Add(_hole);
        HideRow(row);
        DragFx.PlaceOver(_layer, _hole, row, _window);

        _pending = false;
        _dragging = true;
        _window.CaptureMouse();
        return true;
    }

    private void UpdateInsertion(Point p)
    {
        var listPos = _window.TranslatePoint(p, _list);
        if (listPos.X < 0 || listPos.Y < 0 || listPos.X > _list.ActualWidth || listPos.Y > _list.ActualHeight)
            return;   // 光标在列表外：保持最后一个落点

        var hovered = RowAt(listPos);
        if (hovered == null) return;
        int hover = _list.ItemContainerGenerator.IndexFromContainer(hovered);
        if (hover < 0) return;

        bool after = p.Y - hovered.TransformToAncestor(_window).Transform(new Point(0, 0)).Y
                     >= hovered.ActualHeight / 2;
        int insertAt = after ? hover + 1 : hover;

        // 还在源行自身（或紧邻位置）：不重排，避免抖动
        if (insertAt == _curIndex || insertAt == _curIndex + 1) return;

        int next = _move(_curIndex, insertAt);
        if (next < 0) return;
        _curIndex = next;

        _list.UpdateLayout();
        MoveHoleOverCurrent();
    }

    private void MoveHoleOverCurrent()
    {
        var row = ContainerAt(_curIndex);
        if (row == null || _hole == null) return;
        HideRow(row);
        DragFx.PlaceOver(_layer, _hole, row, _window);
    }

    private void HideRow(ListBoxItem row)
    {
        if (ReferenceEquals(_hiddenRow, row)) return;
        if (_hiddenRow != null) _hiddenRow.Opacity = 1;
        _hiddenRow = row;
        row.Opacity = 0;
    }

    /// <summary>取消（Esc）：还原数据并清掉视觉。</summary>
    public void Cancel()
    {
        if (!IsActive || _finishing) return;
        _finishing = true;
        try
        {
            bool wasDragging = _dragging;
            // 先落状态：ReleaseMouseCapture 会同步重入鼠标事件
            _pending = false;
            _dragging = false;
            _srcIndex = _curIndex = -1;

            _visuals.End();
            RemoveHole();
            _window.ReleaseMouseCapture();

            if (wasDragging) _restore();
            ResetRowOpacities();
            _hiddenRow = null;
        }
        finally { _finishing = false; }
    }

    private bool _finishing;

    private void End()
    {
        _finishing = true;
        try
        {
            // 先落状态：ReleaseMouseCapture 会同步重入鼠标事件
            _pending = false;
            _dragging = false;
            _srcIndex = _curIndex = -1;

            _visuals.End();
            RemoveHole();
            _window.ReleaseMouseCapture();

            // 数据已实时移动过，列表可能已重建 / 容器被回收：
            // 逐行恢复不透明，避免某个被复用的容器残留 Opacity=0 变成隐形行。
            ResetRowOpacities();
            _hiddenRow = null;
        }
        finally { _finishing = false; }
    }

    /// <summary>把所有已生成的行的 Opacity 复位（容器会被 ListBox 回收复用，不能只记一个引用）。</summary>
    private void ResetRowOpacities()
    {
        for (int i = 0; i < _list.Items.Count; i++)
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c)
                c.Opacity = 1;
    }

    private void ClearPending()
    {
        _pending = false;
        _srcIndex = _curIndex = -1;
    }

    private void RemoveHole()
    {
        if (_hole != null)
        {
            _layer.Children.Remove(_hole);
            _hole = null;
        }
    }

    // ============ 命中 ============

    private ListBoxItem? ContainerAt(int index)
        => index >= 0 ? _list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem : null;

    private ListBoxItem? RowAt(Point listPos)
    {
        if (_list.InputHitTest(listPos) is not DependencyObject d) return null;
        return ItemsControl.ContainerFromElement(_list, d) as ListBoxItem;
    }
}
