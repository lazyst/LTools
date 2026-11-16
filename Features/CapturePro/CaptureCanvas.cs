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

    /// <summary>内容真源（底图 + 标注列表）。</summary>
    public CaptureDocument? Document { get; set; }

    /// <summary>选区（窗口 DIP）；空 = 还没框选。</summary>
    public Rect Selection { get; set; } = Rect.Empty;

    /// <summary>悬停命中的窗口 / 控件（窗口 DIP）；只在还没选区时显示。</summary>
    public Rect Hover { get; set; } = Rect.Empty;

    /// <summary>正在画的那一笔（还没进文档）；空 = 没有。</summary>
    public Mark? Preview { get; set; }

    /// <summary>拖动中的那条（由 <see cref="Preview"/> 代画），按列表下标跳过，避免同一笔画两遍。</summary>
    public int SkipIndex { get; set; } = -1;

    /// <summary>被选中的标注下标（-1 = 没有）；画一圈虚线框，告诉用户 Delete 会删哪一条。</summary>
    public int SelectedIndex { get; set; } = -1;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var doc = Document;
        if (doc == null) return;

        // 底图：位图物理像素，按文档换算成 DIP 整块铺满窗口
        var canvas = doc.Canvas;
        dc.DrawImage(doc.Base, canvas);

        // 标注：裁到选区，显示与导出看到的是同一块内容
        bool hasSelection = Selection.Width >= 0.5 && Selection.Height >= 0.5;
        if (hasSelection) dc.PushClip(new RectangleGeometry(Selection));
        for (int i = 0; i < doc.Marks.Count; i++)
        {
            if (i == SkipIndex) continue;
            MarkRenderer.Draw(dc, doc.Marks[i], doc.Base, canvas, doc.Scale);
        }
        if (Preview != null) MarkRenderer.Draw(dc, Preview, doc.Base, canvas, doc.Scale);

        // 选中的那条：一圈虚线，不参与导出（导出只看标注本身）
        if (SelectedIndex >= 0 && SelectedIndex < doc.Marks.Count && SelectedIndex != SkipIndex)
        {
            var box = doc.Marks[SelectedIndex].Bounds;
            if (!box.IsEmpty)
            {
                box.Inflate(3, 3);
                dc.DrawRectangle(null, SelectPen, box);
            }
        }

        if (hasSelection) dc.Pop();

        // 压暗：EvenOdd 挖洞，选区（或悬停命中的元素）保持原亮度
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var hole = hasSelection ? Selection : Hover.Width >= 0.5 && Hover.Height >= 0.5 ? Hover : (Rect?)null;
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        if (hole is Rect h) geo.Children.Add(new RectangleGeometry(h));
        geo.Freeze();
        dc.DrawGeometry(DimBrush, null, geo);

        // 选区框：外面一层深色描边保证浅底上也看得清
        if (!hasSelection) return;
        dc.DrawRectangle(null, FrameShadow, Selection);
        dc.DrawRectangle(null, FramePen, Selection);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
