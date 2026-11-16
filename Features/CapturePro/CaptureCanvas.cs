using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 覆盖层的画面：底图 + 标注 + 压暗 + 选区框。
/// </summary>
/// <remarks>
/// <para>
/// 画面一律**从 <see cref="CaptureDocument"/> 画**（不是把截屏贴上去再盖元素）：
/// 标注、压暗、选区框都在一次 <see cref="OnRender"/> 里画完，导出与显示共用
/// <see cref="MarkRenderer"/>，所以「所见」与「所得」不会分叉。
/// </para>
/// <para>
/// 坐标是窗口 DIP。位图本身的像素尺寸在 <see cref="CaptureDocument.Base"/>，
/// 只有马赛克取底图那一步按 <see cref="CaptureDocument.Scale"/> 换成物理像素。
/// </para>
/// </remarks>
internal sealed class CaptureCanvas : FrameworkElement
{
    private static readonly SolidColorBrush DimBrush = Frozen(Color.FromArgb(0x66, 0, 0, 0));
    private static readonly Pen FrameShadow = FrozenPen(Color.FromArgb(0x99, 0, 0, 0), 3);
    private static readonly Pen FramePen = FrozenPen(Colors.White, 1.5);

    /// <summary>选中标注的虚线框（和工具栏的选中色一致）。</summary>
    private static readonly Pen SelectPen = FrozenDashPen(Color.FromRgb(0x3B, 0x82, 0xF6), 1.2);

    /// <summary>标注缩放手柄的填充 / 描边（与选区的八个把手同款）。</summary>
    private static readonly SolidColorBrush HandleFill = Frozen(Colors.White);
    private static readonly Pen HandleEdge = FrozenPen(Color.FromRgb(0x18, 0x18, 0x1B), 1);

    /// <summary>正在圈 / 正在点的选区：白色虚线 + 深色底描边（浅底上也看得见）。</summary>
    private static readonly Pen PendingPen = FrozenDashPen(Colors.White, 1.5);
    private static readonly Pen PendingShadow = FrozenDashPen(Color.FromArgb(0xAA, 0, 0, 0), 3);

    /// <summary>内容真源（底图 + 标注列表）。</summary>
    public CaptureDocument? Document { get; set; }

    /// <summary>选区（窗口 DIP）；空 = 还没框选。</summary>
    public Rect Selection { get; set; } = Rect.Empty;

    /// <summary>选区是多边形时它的顶点（窗口 DIP，隐含首尾闭合）；空 = 矩形选区。</summary>
    public Point[]? SelectionPolygon { get; set; }

    /// <summary>正在圈（套索）/ 正在点（折线）的选区，还没闭合；空 = 没有。</summary>
    public Point[]? PendingPolygon { get; set; }

    /// <summary>悬停命中的窗口 / 控件（窗口 DIP）；只在还没选区时显示。</summary>
    public Rect Hover { get; set; } = Rect.Empty;

    /// <summary>正在画的那一笔（还没进文档）；空 = 没有。</summary>
    public Mark? Preview { get; set; }

    /// <summary>拖动中的那条（由 <see cref="Preview"/> 代画），按列表下标跳过，避免同一笔画两遍。</summary>
    public int SkipIndex { get; set; } = -1;

    /// <summary>被选中的标注下标（-1 = 没有）；画一圈虚线框，告诉用户 Delete 会删哪一条。</summary>
    public int SelectedIndex { get; set; } = -1;

    /// <summary>
    /// 选中的标注要不要画八个缩放手柄：只有「选择」工具下、且这一条能缩放（序号徽标是固定尺寸的）。
    /// 判定用 <see cref="MarkGeometry.PaddedFrame"/>，拖手柄也按它算，看到的就是按得到的。
    /// </summary>
    public bool ShowHandles { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var doc = Document;
        if (doc == null) return;

        // 底图：位图物理像素，按文档换算成 DIP 整块铺满窗口
        var canvas = doc.Canvas;
        dc.DrawImage(doc.Base, canvas);

        // 标注：裁到选区，显示与导出看到的是同一块内容（自由 / 折线选区按多边形裁）
        bool hasSelection = Selection.Width >= 0.5 && Selection.Height >= 0.5;
        Geometry? selectionGeo = hasSelection ? SelectionGeometry() : null;
        if (selectionGeo != null) dc.PushClip(selectionGeo);
        for (int i = 0; i < doc.Marks.Count; i++)
        {
            if (i == SkipIndex) continue;
            MarkRenderer.Draw(dc, doc.Marks[i], doc.Base, canvas, doc.Scale);
        }
        if (Preview != null) MarkRenderer.Draw(dc, Preview, doc.Base, canvas, doc.Scale);

        // 选中的那条：一圈虚线 + 八个把手，不参与导出（导出只看标注本身）
        if (SelectedIndex >= 0 && SelectedIndex < doc.Marks.Count && SelectedIndex != SkipIndex)
        {
            var mark = doc.Marks[SelectedIndex];
            var box = MarkGeometry.PaddedFrame(mark.Frame);
            if (!box.IsEmpty)
            {
                dc.DrawRectangle(null, SelectPen, box);
                if (ShowHandles && mark.CanScale) DrawHandles(dc, box);
            }
        }

        if (selectionGeo != null) dc.Pop();

        // 压暗：EvenOdd 挖洞，选区（或悬停命中的元素）保持原亮度
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        Geometry? hole = hasSelection
            ? selectionGeo
            : Hover.Width >= 0.5 && Hover.Height >= 0.5
                ? new RectangleGeometry(Hover)
                : null;
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        if (hole != null) geo.Children.Add(hole);
        geo.Freeze();
        dc.DrawGeometry(DimBrush, null, geo);

        // 选区框：外面一层深色描边保证浅底上也看得清；多边形按点列画，矩形还是画矩形
        if (hasSelection && selectionGeo != null)
        {
            if (selectionGeo is RectangleGeometry rect)
            {
                dc.DrawRectangle(null, FrameShadow, rect.Rect);
                dc.DrawRectangle(null, FramePen, rect.Rect);
            }
            else
            {
                dc.DrawGeometry(null, FrameShadow, selectionGeo);
                dc.DrawGeometry(null, FramePen, selectionGeo);
            }
        }

        // 正在圈 / 正在点的选区：白色虚线跟手（深色底描边先画）；
        // 折线刚点下第一个点时还没有线段，画个小圆圈给个落点
        if (PendingPolygon is { Length: > 0 } pending)
        {
            if (pending.Length == 1)
            {
                dc.DrawEllipse(null, PendingShadow, pending[0], 4, 4);
                dc.DrawEllipse(null, PendingPen, pending[0], 4, 4);
            }
            else
            {
                var line = SelectionShape.Geometry(pending, closed: false);
                dc.DrawGeometry(null, PendingShadow, line);
                dc.DrawGeometry(null, PendingPen, line);
            }
        }
    }

    /// <summary>选区的几何：多边形时按点列，矩形时按 <see cref="Selection"/>。</summary>
    private Geometry SelectionGeometry() => SelectionPolygon is { Length: >= SelectionShape.MinPoints } poly
        ? SelectionShape.Geometry(poly, closed: true)
        : new RectangleGeometry(Selection);


    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>标注的缩放手柄：白底深边的小方块（与选区那八个把手同尺寸、同一套位置）。</summary>
    private static void DrawHandles(DrawingContext dc, Rect box)
    {
        const double size = MarkGeometry.HandleSize;
        foreach (var handle in MarkGeometry.Handles)
        {
            var at = MarkGeometry.GripPoint(box, handle);
            dc.DrawRectangle(HandleFill, HandleEdge,
                new Rect(at.X - size / 2, at.Y - size / 2, size, size));
        }
    }

    private static Pen FrozenPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }

    private static Pen FrozenDashPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width)
        {
            DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0),
        };
        pen.Freeze();
        return pen;
    }
}
