using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把快速文字标注画到画布上（照上游 <c>screenshottextannotationrenderer.cpp</c>）。
/// </summary>
/// <remarks>
/// <para>
/// **高亮在下、线标注在上**：上游用 <c>CompositionMode_Multiply</c> 叠高亮，WPF 没有等价的
/// 逐像素混合，所以高亮画在文字**下面**、不透明色按 alpha 0.5 近似（见
/// <see cref="HighlightColor"/>，已登记进 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </para>
/// <para>
/// 线标注复用形状族的笔迹实现（<see cref="ShapeInk.DrawStroke"/>）：实 / 虚 / 点线与
/// 干净 / 手绘 / 涂鸦三档风格与截图工具条上的形状完全一致，抖动种子来自
/// <see cref="PinTextAnnotationGeometry.Seed"/>，所以同一段永远画成同一个样子。
/// </para>
/// </remarks>
internal static class PinTextAnnotationPainter
{
    /// <summary>高亮的实际绘制色：不透明色降到 50%（WPF 没有 Multiply 的近似）。</summary>
    public static Color HighlightColor(Color color) =>
        color.A == 255 ? Color.FromArgb(128, color.R, color.G, color.B) : color;

    /// <summary>
    /// 画一份标注。<paramref name="rectOf"/> 把源文本区间换成**源图像素**里的矩形
    /// （画布自己知道每行的框与字宽，这里不重复排版）。
    /// </summary>
    public static void Paint(DrawingContext dc, PinTextAnnotationDocument document,
        Func<PinTextRange, Rect?> rectOf)
    {
        PaintHighlights(dc, document, rectOf);
        PaintLines(dc, document, rectOf);
    }

    /// <summary>只铺高亮（画布把它压在文字**下面**，见类型注释）。</summary>
    public static void PaintHighlights(DrawingContext dc, PinTextAnnotationDocument document,
        Func<PinTextRange, Rect?> rectOf)
    {
        if (document.Empty) return;

        foreach (PinTextAnnotationRun run in document.Runs)
        {
            if (run.Highlight is not { } highlight || highlight.Color.A == 0) continue;
            if (rectOf(run.Range) is not { } rect || rect.IsEmpty) continue;

            var brush = new SolidColorBrush(HighlightColor(highlight.Color));
            brush.Freeze();
            dc.DrawRectangle(brush, null, rect);
        }
    }

    /// <summary>只画线标注（画布把它画在文字 / 选区**上面**）。</summary>
    public static void PaintLines(DrawingContext dc, PinTextAnnotationDocument document,
        Func<PinTextRange, Rect?> rectOf)
    {
        if (document.Empty) return;

        foreach (PinTextAnnotationRun run in document.Runs)
        {
            if (run.LineTool is not { } tool) continue;
            PinTextAnnotationStyle style = run.LineStyle;
            if (!style.IsValid || style.Color.A == 0) continue;
            if (rectOf(run.Range) is not { } rect || rect.IsEmpty) continue;

            List<InkSegment> centerline = PinTextAnnotationGeometry.Centerline(rect, tool, style.Width);
            if (centerline.Count == 0) continue;

            var spec = new ShapeSpec(
                ShapeKind.Rectangle,
                style.Color,
                style.Width,
                style.StrokeStyle,
                style.RenderingStyle,
                Colors.Transparent,
                FillKind.None,
                0,
                PinTextAnnotationGeometry.Seed(run.Range, tool));

            // 下划线 / 波浪线不许压到字身上：手绘抖动的上沿越过行底时整条往下挪（照上游 sourceCoverage）
            if (tool != PinTextAnnotationTool.Strikethrough)
                ShiftBelow(rect, centerline, spec);

            ShapeInk.DrawStroke(dc, centerline, closed: false, spec);
        }
    }

    /// <summary>中心线（含抖动与线宽）的上沿如果高过行底，就把整条下移，别压住字。</summary>
    private static void ShiftBelow(Rect rect, List<InkSegment> centerline, ShapeSpec spec)
    {
        Rect bounds = Rect.Empty;
        foreach (PathGeometry path in ShapeInk.StrokePaths(centerline, closed: false, spec))
            bounds = bounds.IsEmpty ? path.Bounds : Rect.Union(bounds, path.Bounds);

        if (bounds.IsEmpty) return;

        double radius = spec.Width / 2 + ShapeInk.Outset(spec.Rendering);
        double top = bounds.Top - radius;
        if (top >= rect.Bottom) return;

        double shift = rect.Bottom - top;
        var offset = new Vector(0, shift);
        for (int i = 0; i < centerline.Count; i++)
        {
            InkSegment segment = centerline[i];
            centerline[i] = new InkSegment(segment.A + offset, segment.C1 + offset,
                segment.C2 + offset, segment.B + offset, segment.Straight);
        }
    }
}
