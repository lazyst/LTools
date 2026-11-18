using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro.AutoFilter;

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

    /// <summary>放大镜移动手柄的六个小点（选中色）。</summary>
    private static readonly SolidColorBrush GripFill = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));

    /// <summary>正在圈 / 正在点的选区：白色虚线 + 深色底描边（浅底上也看得见）。</summary>
    private static readonly Pen PendingPen = FrozenDashPen(Colors.White, 1.5);
    private static readonly Pen PendingShadow = FrozenDashPen(Color.FromArgb(0xAA, 0, 0, 0), 3);

    // 自动打码（照上游：命中红 / 已填绿，框选蓝）
    private static readonly Color AutoRed = Color.FromRgb(0xFF, 0x4D, 0x4F);
    private static readonly Color AutoGreen = Color.FromRgb(0x52, 0xC4, 0x1A);
    private static readonly Color AutoBlue = Color.FromRgb(0x40, 0x96, 0xFF);
    private static readonly SolidColorBrush AutoRedFill = Frozen(Color.FromArgb(51, AutoRed.R, AutoRed.G, AutoRed.B));
    private static readonly Pen AutoRedPen = FrozenPen(AutoRed, 1);
    private static readonly SolidColorBrush AutoGreenFill = Frozen(Color.FromArgb(51, AutoGreen.R, AutoGreen.G, AutoGreen.B));
    private static readonly Pen AutoGreenPen = FrozenPen(AutoGreen, 1);
    private static readonly SolidColorBrush AutoMarqueeFill = Frozen(Color.FromArgb(0x33, AutoBlue.R, AutoBlue.G, AutoBlue.B));
    private static readonly Pen AutoMarqueePen = FrozenPen(AutoBlue, 1);

    /// <summary>内容真源（底图 + 标注列表）。</summary>
    public CaptureDocument? Document { get; set; }

    /// <summary>选区（窗口 DIP）；空 = 还没框选。</summary>
    public Rect Selection { get; set; } = Rect.Empty;

    /// <summary>
    /// 选区合起来的几何（窗口 DIP）：多块、自由 / 折线、挖洞都按它裁；空 = 一整块矩形，看 <see cref="Selection"/>。
    /// 由 <c>CaptureOverlayWindow.SelectionGeometry</c> 给出，和导出用的是同一份 <see cref="SelectionSet"/> 规则。
    /// </summary>
    public Geometry? SelectionGeometry { get; set; }

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
    /// 选中的全部标注（多选：框选 / Shift+点选）。顺序即加入顺序，<see cref="SelectedIndex"/> 是最后那个（主选中项）。
    /// 空 = 退回只看 <see cref="SelectedIndex"/>（单选 / 冒烟夹具直接设单条）。
    /// </summary>
    public IReadOnlyList<int> SelectedIndices { get; set; } = Array.Empty<int>();

    /// <summary>多选整体拖动时的那几条（由预览代画，按 <see cref="SelectedIndices"/> 跳过原件）。</summary>
    public IReadOnlyList<Mark>? Previews { get; set; }

    /// <summary>正在框选标注的矩形（Shift+拖，照上游 <c>InteractionState::MarqueeSelection</c>）。</summary>
    public Rect MarkMarquee { get; set; } = Rect.Empty;

    /// <summary>框选过程中已命中的标注（只画候选轮廓，松手才真的选中）。</summary>
    public IReadOnlyList<int> MarqueeHits { get; set; } = Array.Empty<int>();

    /// <summary>
    /// 选中的标注要不要画八个缩放手柄：只有「选择」工具下、且这一条能缩放（序号徽标是固定尺寸的）。
    /// 判定用 <see cref="MarkGeometry.PaddedFrame"/>，拖手柄也按它算，看到的就是按得到的。
    /// </summary>
    public bool ShowHandles { get; set; }

    /// <summary>
    /// 自动打码的界面状态（识别结果 / 扫描光带 / 闪红 / 悬停 / 框选）；空 = 没在用这个工具。
    /// 由覆盖层在切工具时给上 / 清掉，画布只读。
    /// </summary>
    public AutoFilterState? AutoFilter { get; set; }

    /// <summary>自动打码当前的滤镜类型：判断「这一块是不是已经用同类型填过」（绿 / 红高亮）。</summary>
    public FilterKind AutoFillKind { get; set; } = FilterKind.Mosaic;

    /// <summary>扫描光带这一刻的位置（选区 DIP）；空选区 = 不画。</summary>
    public long AutoFilterClock { get; set; } = Environment.TickCount64;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var doc = Document;
        if (doc == null) return;

        // 底图：位图物理像素，按文档换算成 DIP 整块铺满窗口
        var canvas = doc.Canvas;
        dc.DrawImage(doc.Base, canvas);

        // 标注：裁到选区，显示与导出看到的是同一块内容（多块 / 自由 / 折线 / 挖洞按合并几何裁）
        Geometry? selectionGeo = SelectionGeometry;
        if (selectionGeo == null && Selection.Width >= 0.5 && Selection.Height >= 0.5)
            selectionGeo = new RectangleGeometry(Selection);

        if (selectionGeo != null) dc.PushClip(selectionGeo);
        bool dragging = Previews is { Count: > 0 };
        for (int i = 0; i < doc.Marks.Count; i++)
        {
            if (i == SkipIndex) continue;
            if (dragging && SelectedIndices.Contains(i)) continue;   // 整组拖动：原件让位给预览
            MarkRenderer.Draw(dc, doc.Marks[i], doc.Base, canvas, doc.Scale);
        }
        if (Preview != null) MarkRenderer.Draw(dc, Preview, doc.Base, canvas, doc.Scale);
        if (Previews != null)
            foreach (var preview in Previews) MarkRenderer.Draw(dc, preview, doc.Base, canvas, doc.Scale);

        DrawAutoFilter(dc);

        // 选中的那些：各自一圈虚线（与上游 selection_elements 的逐条轮廓一致）；
        // 八个把手只在**单条**选中时出现（多选整体缩放还没做，见 docs/CAPTURE_PARITY.md）
        var selection = SelectedIndices.Count > 0
            ? SelectedIndices
            : SelectedIndex >= 0 ? new[] { SelectedIndex } : Array.Empty<int>();
        bool handles = ShowHandles && selection.Count == 1;

        foreach (int index in selection)
        {
            if (index < 0 || index >= doc.Marks.Count || index == SkipIndex) continue;
            var mark = doc.Marks[index];
            var box = MarkGeometry.PaddedFrame(mark.Frame);
            if (!box.IsEmpty)
            {
                dc.DrawRectangle(null, SelectPen, box);
                if (handles && mark.CanScale) DrawHandles(dc, box);
            }

            // 放大镜额外画源轮廓与移动手柄（照上游 overlay_composition 的
            // magnifier_source_outline + magnifier_move_grip）：源轮廓一直看得见，
            // 手柄在镜头下方 24 DIP，抓着它 = 只挪镜头
            if (mark is Mark.Magnifier magnifier) DrawMagnifierChrome(dc, magnifier);
        }

        // 正在框选标注：蓝色虚线框（与自动打码的框选同一个观感）
        if (MarkMarquee.Width > 0.5 && MarkMarquee.Height > 0.5)
        {
            // 框里已经命中的先描一圈候选轮廓（上游 marquee_candidate_elements 的即时反馈）
            foreach (int index in MarqueeHits)
            {
                if (index < 0 || index >= doc.Marks.Count) continue;
                var box = MarkGeometry.PaddedFrame(doc.Marks[index].Frame);
                if (!box.IsEmpty) dc.DrawRectangle(null, SelectPen, box);
            }

            dc.DrawRectangle(AutoMarqueeFill, AutoMarqueePen, MarkMarquee);
        }

        if (selectionGeo != null) dc.Pop();

        // 压暗：EvenOdd 挖洞，选区（或悬停命中的元素）保持原亮度
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        Geometry? hole = selectionGeo
            ?? (Hover.Width >= 0.5 && Hover.Height >= 0.5 ? new RectangleGeometry(Hover) : null);
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        if (hole != null) geo.Children.Add(hole);
        geo.Freeze();
        dc.DrawGeometry(DimBrush, null, geo);

        // 选区框：外面一层深色描边保证浅底上也看得清；一整块矩形走双描边，其它几何按路径描边
        if (selectionGeo != null)
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

    /// <summary>
    /// 自动打码的覆盖层（照上游 <c>ScreenshotAutoFilterVisual</c> + <c>auto_filter_highlights</c>）：
    /// 悬停 / 框中的区域描边 + 半透明填充（已用同类型填过 → 绿，否则红）、框选矩形（蓝）、
    /// 检测中的扫描光带、结果刚到时的整批闪红。
    /// </summary>
    private void DrawAutoFilter(DrawingContext dc)
    {
        var state = AutoFilter;
        if (state == null) return;

        var marks = Document?.Marks ?? (IReadOnlyList<Mark>)Array.Empty<Mark>();

        // 高亮：拖动中是整批命中，否则只是光标下那一块（同面积取 id 小的那块，见 AutoFilterLogic）
        IReadOnlyList<int> hits = state.GestureActive
            ? state.Hits
            : state.HoverId >= 0 ? new[] { state.HoverId } : Array.Empty<int>();

        foreach (int id in hits)
        {
            var region = state.Regions.FirstOrDefault(r => r.Id == id);
            if (region.Bounds.Width <= 0 || region.Bounds.Height <= 0) continue;
            bool filled = AutoFilterLogic.FilledWith(marks, id, AutoFillKind);
            dc.DrawRectangle(filled ? AutoGreenFill : AutoRedFill,
                filled ? AutoGreenPen : AutoRedPen, region.Bounds);
        }

        // 框选矩形（上游就是选区套索那一套颜色：蓝描边 + 20% 填充）
        if (state.Marquee is { } marquee && marquee.Width > 0 && marquee.Height > 0)
            dc.DrawRectangle(AutoMarqueeFill, AutoMarqueePen, marquee);

        // 检测中的扫描光带：42 高，中心 y 按 1400ms 一圈从上走到下（带子从选区上下各多出 30）
        if (state.Detecting)
        {
            var box = state.Bounds.Width > 0 ? state.Bounds : Selection;
            if (box.Width > 0 && box.Height > 0)
            {
                double y = state.ScanY(AutoFilterClock);
                var glow = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                };
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x40, 0x96, 0xFF), 0));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(45, 0x40, 0x96, 0xFF), 0.70));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(145, 0x60, 0xDC, 0xFF), 0.76));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x40, 0x96, 0xFF), 1));
                glow.Freeze();
                dc.DrawRectangle(glow, null, new Rect(box.Left, y - 30, box.Width, 42));
            }
        }

        // 结果刚到：整批红框闪一下（300ms，上游 <c>m_flashUntil = arrived + 300</c>）
        if (state.Flashing(AutoFilterClock))
        {
            foreach (var region in state.Regions)
                dc.DrawRectangle(AutoRedFill, AutoRedPen, region.Bounds);
        }
    }

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

    /// <summary>
    /// 选中放大镜时的两件套（照上游 <c>overlay_composition.rs</c> + <c>selection_overlay.rs</c>）：
    /// ① 源区域轮廓（选中色、1 DIP 虚线，形状跟着镜头走，圆角是镜头圆角 ÷ 倍率）；
    /// ② 移动手柄（镜头下缘 +24 DIP，14×14 的 2×3 六个小点）——抓着它只挪镜头。
    /// </summary>
    private static void DrawMagnifierChrome(DrawingContext dc, Mark.Magnifier magnifier)
    {
        var spec = magnifier.Spec;
        double radius = spec.Shape == ShapeKind.Rectangle ? spec.CornerRadius / Math.Max(1, spec.Factor) : 0;
        var source = MagnifierInk.Outline(magnifier.SourceRect, spec.Shape, radius);
        if (!source.IsEmpty()) dc.DrawGeometry(null, SelectPen, source);

        const double unit = MagnifierInk.MoveGripSize / 14.0;
        var grip = MagnifierInk.MoveGrip(magnifier.Lens);
        foreach (double gx in new[] { -1.5, 1.5 })
            foreach (double gy in new[] { -2.5, 0.0, 2.5 })
                dc.DrawEllipse(GripFill, null,
                    new Point(grip.X + gx * unit, grip.Y + gy * unit), 0.65 * unit, 0.65 * unit);
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
