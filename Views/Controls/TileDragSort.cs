using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Config;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>面板磁贴引用（面板 / 面板管理共用）。<see cref="ActionId"/> 为 null 表示「＋」磁贴。</summary>
internal sealed class PanelTileRef
{
    public PanelGroupDto Group { get; init; } = null!;
    public int Index { get; init; }
    public string? ActionId { get; init; }
}

/// <summary>
/// 磁贴拖动排序（照 Quicker 面板）：位移超过 4px 才进入拖动；源磁贴变成虚线占位，
/// 快照幽灵居中跟随光标；光标移到哪个槽位就实时把占位插到那里，其余磁贴做 220ms 让位动画；
/// 松手提交（同组重排 / 跨组移动），Esc 取消并还原。
/// </summary>
/// <remarks>
/// 纯视觉重排：拖动期间只移动 <see cref="Panel.Children"/> 里的占位，不写模型；
/// 松手时按占位所在分组与位置调用 <see cref="PanelLibrary.MoveAction"/>，然后由调用方重建磁贴
/// （源磁贴拖动开始时就被摘掉，必须重建才能恢复）。
/// </remarks>
internal sealed class TileDragSort
{
    private const double DragThreshold = 4;
    private const double DeadZone = 10;

    private readonly Window _window;
    private readonly Canvas _layer;
    private readonly FrameworkElement _root;
    private readonly DragVisuals _visuals;

    private Button? _source;
    private PanelTileRef? _sourceRef;
    private PanelGroupDto? _sourceGroup;
    private int _sourceIndex;
    private Point _origin;
    private bool _pending;
    private bool _dragging;
    private bool _overNothing;

    private Rectangle? _hole;
    private TileGridPanel? _holePanel;

    /// <summary>是否允许开始拖动（例如面板搜索中禁止拖动）。</summary>
    public Func<bool>? AllowDrag { get; set; }

    /// <summary>
    /// 幽灵内容工厂：给磁贴生成「和原磁贴一样的内容」，幽灵直接用它而不是栅格化快照
    /// （个别环境下快照会得到空白图）。返回 null 时退回快照。
    /// </summary>
    public Func<PanelTileRef, FrameworkElement?>? GhostBody { get; set; }

    /// <summary>拖动结束（提交或取消）后调用；调用方应重建磁贴。</summary>
    public Action? Finished { get; set; }

    public bool IsDragging => _dragging;

    public bool IsPending => _pending;

    public bool IsActive => _pending || _dragging;

    public TileDragSort(Window window, Canvas layer, FrameworkElement root)
    {
        _window = window;
        _layer = layer;
        _root = root;
        _visuals = new DragVisuals(window, layer);

        window.PreviewMouseMove += OnMouseMove;
        window.PreviewMouseLeftButtonUp += OnMouseUp;
        window.PreviewKeyDown += OnKeyDown;
    }

    /// <summary>给一个可拖动磁贴挂上按下处理（不吞事件，未拖动时按钮照常触发 Click）。</summary>
    public void Attach(Button tile)
    {
        tile.PreviewMouseLeftButtonDown += OnTileMouseDown;
    }

    // ============ 输入 ============

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not Button tile || tile.Tag is not PanelTileRef { ActionId: not null } r) return;
        if (AllowDrag?.Invoke() == false) return;

        _source = tile;
        _sourceRef = r;
        _sourceGroup = r.Group;
        _sourceIndex = r.Index;
        _origin = e.GetPosition(_window);
        _pending = true;
        _dragging = false;
        // 不设 Handled：按住没动时交给 Button 走 Click
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsActive) return;

        // 拖动中左键已松开但还没收到 MouseUp：什么都不做，等 MouseUp。
        // （窗口持有鼠标捕获，松手事件一定会送到；在这里抢着提交会因为
        //   ReleaseMouseCapture 派发的挂起 MouseMove 而重入，提交两次。）
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
            if (!StartDrag(p)) return;
        }

        _visuals.MoveGhost(p);
        UpdateHole(p);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_dragging)
        {
            Commit();
            e.Handled = true;
        }
        else
        {
            ClearPending();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsActive) return;
        Cancel();
        e.Handled = true;
    }

    // ============ 拖动 ============

    private bool StartDrag(Point p)
    {
        if (_source == null || _sourceRef == null || _sourceGroup == null) return false;
        var panel = FindAncestor<TileGridPanel>(_source);
        if (panel == null) { ClearPending(); return false; }

        // 快照必须在源磁贴还在可视树里、且未被摘掉之前做
        _visuals.ShowGhost(_source, p, GhostBody?.Invoke(_sourceRef));

        _hole = DragFx.CreateHole();
        _hole.Width = Math.Max(1, _source.ActualWidth);
        _hole.Height = Math.Max(1, _source.ActualHeight);

        int childIndex = panel.Children.IndexOf(_source);
        if (childIndex < 0) { _visuals.End(); ClearPending(); return false; }
        panel.Children.Remove(_source);
        panel.Children.Insert(childIndex, _hole);

        _holePanel = panel;
        _overNothing = false;
        _pending = false;
        _dragging = true;
        _window.CaptureMouse();
        _window.UpdateLayout();
        return true;
    }

    private void UpdateHole(Point p)
    {
        if (_hole == null || _holePanel == null) return;
        if (IsInDeadZone(p)) return;

        var target = FindPanel(p);
        if (target == null)
        {
            _overNothing = IsOutsideRoot(p);
            return;
        }

        _overNothing = false;
        int index = InsertIndexAt(target, p);
        MoveHole(target, index);
    }

    private void MoveHole(TileGridPanel target, int index)
    {
        if (_hole == null || _holePanel == null) return;
        if (ReferenceEquals(target, _holePanel) && index == _holePanel.Children.IndexOf(_hole)) return;

        var affected = new HashSet<TileGridPanel> { _holePanel, target };
        var old = DragFx.CapturePositions(affected.SelectMany(ActionTiles), _window);

        _holePanel.Children.Remove(_hole);
        index = Math.Clamp(index, 0, target.Children.Count);
        target.Children.Insert(index, _hole);

        var sizeTile = ActionTiles(target).FirstOrDefault();
        if (sizeTile != null)
        {
            _hole.Width = Math.Max(1, sizeTile.ActualWidth);
            _hole.Height = Math.Max(1, sizeTile.ActualHeight);
        }

        _holePanel = target;
        _window.UpdateLayout();
        DragFx.AnimateFrom(old, _window);
    }

    private bool IsInDeadZone(Point p)
    {
        if (_hole == null) return false;
        var rect = DragFx.BoundsIn(_hole, _layer, _window);
        if (rect is not { } r) return false;
        // 光标还在源槽位附近 ±10px：不重排，避免抖动
        return p.X >= r.X - DeadZone && p.X <= r.Right + DeadZone
               && p.Y >= r.Y - DeadZone && p.Y <= r.Bottom + DeadZone;
    }

    private bool _finishing;

    private void Commit()
    {
        // 收敛保护：Cleanup 里的 ReleaseMouseCapture() 会同步派发挂起的 MouseMove，
        // 重入时不能再提交一次（否则会把后面那个磁贴也挪走）。
        if (_finishing) return;
        _finishing = true;
        try
        {
            if (!_overNothing && _hole != null && _holePanel?.Tag is PanelGroupDto targetGroup)
            {
                int k = _holePanel.Children.IndexOf(_hole);   // 源磁贴已被摘掉，k = 去掉源后的插入位置
                if (k >= 0 && (!ReferenceEquals(targetGroup, _sourceGroup) || k != _sourceIndex))
                {
                    int to = ReferenceEquals(targetGroup, _sourceGroup)
                        ? (k >= _sourceIndex ? k + 1 : k)    // 换回「源还在列表里」的插入语义
                        : k;
                    PanelLibrary.MoveAction(_sourceGroup!, _sourceIndex, targetGroup, to);
                }
            }

            Cleanup();
            Finished?.Invoke();   // 源磁贴已被摘掉，任何一次拖动结束都要重建
        }
        finally
        {
            _finishing = false;
        }
    }

    /// <summary>取消拖动（Esc）：不写模型，重建后源磁贴回到原位。</summary>
    public void Cancel()
    {
        if (!IsActive || _finishing) return;
        Cleanup();
        Finished?.Invoke();
    }

    private void ClearPending()
    {
        _pending = false;
        _source = null;
        _sourceRef = null;
    }

    private void Cleanup()
    {
        // 先落状态再放鼠标捕获：释放捕获会同步重入鼠标事件，那时 IsActive 必须已是 false
        _pending = false;
        _dragging = false;
        _source = null;
        _sourceRef = null;
        _holePanel = null;
        _overNothing = false;

        _window.ReleaseMouseCapture();
        if (_hole != null)
        {
            (_hole.Parent as Panel)?.Children.Remove(_hole);
            _hole = null;
        }
        _visuals.End();
    }

    // ============ 命中 / 槽位 ============

    private TileGridPanel? FindPanel(Point windowPoint)
    {
        TileGridPanel? best = null;
        double bestDist = double.MaxValue;
        foreach (var panel in Panels())
        {
            var rect = PanelRect(panel);
            if (rect is not { } r) continue;

            // 四周放宽一点，分组卡片边缘也能命中
            var hit = new Rect(r.X - 16, r.Y - 6, r.Width + 32, r.Height + 12);
            if (hit.Contains(windowPoint)) return panel;

            double dist = Math.Abs(windowPoint.Y - (r.Y + r.Height / 2));
            if (dist < bestDist) { bestDist = dist; best = panel; }
        }

        // 邻近分组（间距内）也算命中
        if (best != null && bestDist < 24)
        {
            var r = PanelRect(best);
            if (r is { } rr
                && windowPoint.X >= rr.Left - 16 && windowPoint.X <= rr.Right + 16
                && windowPoint.Y >= rr.Top - 18 && windowPoint.Y <= rr.Bottom + 18) return best;
        }
        return null;
    }

    private bool IsOutsideRoot(Point p)
    {
        try
        {
            var topLeft = _root.TransformToAncestor(_window).Transform(new Point(0, 0));
            var r = new Rect(topLeft, _root.RenderSize);
            r.Inflate(12, 12);
            return !r.Contains(p);
        }
        catch (InvalidOperationException) { return true; }
    }

    private Rect? PanelRect(TileGridPanel panel)
    {
        if (!panel.IsVisible || panel.ActualWidth <= 0) return null;
        try
        {
            var topLeft = panel.TransformToAncestor(_window).Transform(new Point(0, 0));
            return new Rect(topLeft, panel.RenderSize);
        }
        catch (InvalidOperationException) { return null; }
    }

    private IEnumerable<TileGridPanel> Panels()
    {
        foreach (var p in Descendants<TileGridPanel>(_root)) yield return p;
    }

    private static IEnumerable<Button> ActionTiles(TileGridPanel panel)
        => panel.Children.OfType<Button>().Where(b => b.Tag is PanelTileRef { ActionId: not null });

    /// <summary>
    /// 光标在目标网格里的插入位置（0..动作磁贴数）。行优先：先按 Y 找行，再按 X 与各磁贴中心比较。
    /// </summary>
    private int InsertIndexAt(TileGridPanel panel, Point windowPoint)
    {
        var tiles = ActionTiles(panel).ToList();
        if (tiles.Count == 0) return 0;

        var pt = _window.TranslatePoint(windowPoint, panel);
        var rects = new List<Rect>(tiles.Count);
        foreach (var t in tiles)
            rects.Add(t.TransformToAncestor(panel).TransformBounds(new Rect(t.RenderSize)));

        // 按行分组（行距 72 + gap，容差 8）
        var rowStarts = new List<int> { 0 };
        for (int i = 1; i < rects.Count; i++)
            if (Math.Abs(rects[i].Top - rects[rowStarts[^1]].Top) > 8)
                rowStarts.Add(i);

        int row = rowStarts.Count - 1;
        for (int s = 0; s < rowStarts.Count; s++)
        {
            int start = rowStarts[s];
            int end = s + 1 < rowStarts.Count ? rowStarts[s + 1] : rects.Count;
            double bottom = start;
            for (int i = start; i < end; i++) bottom = Math.Max(bottom, rects[i].Bottom);

            if (pt.Y < rects[start].Top) { row = s; break; }
            if (pt.Y <= bottom) { row = s; break; }
            row = s;
        }

        int rs = rowStarts[row];
        int re = row + 1 < rowStarts.Count ? rowStarts[row + 1] : rects.Count;
        for (int i = rs; i < re; i++)
        {
            double centerX = rects[i].Left + rects[i].Width / 2;
            if (pt.X < centerX) return i;
        }
        return re;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var x in Descendants<T>(child)) yield return x;
        }
    }
}
