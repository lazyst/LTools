using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro;

/// <summary>
/// 标注的唯一绘制实现：**显示与导出都调它**，否则「所见」与「所得」必然分叉。
/// 坐标一律 DIP；只有马赛克取底图那一步按 <paramref name="scale"/> 换成物理像素。
/// </summary>
internal static class MarkRenderer
{
    /// <summary>马赛克小块位图按标注对象缓存：拖动时每帧重采样会明显掉帧。</summary>
    private static readonly ConditionalWeakTable<Mark.Mosaic, MosaicTile> MosaicCache = new();

    /// <summary>缓存单元格（位图可能因为底图未就绪而为空）。</summary>
    private sealed class MosaicTile
    {
        public BitmapSource? Image { get; init; }
    }

    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>按顺序画一条标注。</summary>
    /// <param name="canvas">底图在 DIP 下的整块矩形（聚光灯要压暗选区以外的部分）。</param>
    /// <param name="scale">物理像素 / DIP，马赛克取图时用。</param>
    public static void Draw(DrawingContext dc, Mark mark, BitmapSource baseImage, Rect canvas, double scale)
    {
        switch (mark)
        {
            case Mark.Stroke s:
                DrawStroke(dc, s);
                break;
            case Mark.Shape s:
                DrawShape(dc, s);
                break;
            case Mark.Arrow a:
                DrawArrow(dc, a);
                break;
            case Mark.Polyline p:
                DrawPolyline(dc, p);
                break;
            case Mark.Text t:
                DrawText(dc, t);
                break;
            case Mark.Serial n:
                DrawSerial(dc, n);
                break;
            case Mark.Mosaic m:
                DrawMosaic(dc, m, baseImage, scale);
                break;
            case Mark.Spotlight sp:
                DrawSpotlight(dc, sp, canvas);
                break;
        }
    }

    /// <summary>一次画完底图与全部标注（导出与 <c>CaptureCanvas.OnRender</c> 共用）。</summary>
    public static void DrawAll(DrawingContext dc,
        IReadOnlyList<Mark> marks, BitmapSource baseImage, Rect canvas, double scale)
    {
        foreach (var mark in marks) Draw(dc, mark, baseImage, canvas, scale);
    }

    // ============ 逐类 ============

    private static void DrawStroke(DrawingContext dc, Mark.Stroke s)
    {
        if (s.Points.Count == 0) return;
        var color = s.Highlighter ? WithAlpha(s.Color, s.Opacity) : s.Color;
        var pen = new Pen(Freeze(color), s.Width)
        {
            StartLineCap = s.Highlighter ? PenLineCap.Square : PenLineCap.Round,
            EndLineCap = s.Highlighter ? PenLineCap.Square : PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, MarkGeometry.Polyline(s.Points));
    }

    private static void DrawShape(DrawingContext dc, Mark.Shape s)
    {
        var pen = MakePen(s.Color, s.Width, s.Dashed);
        dc.DrawGeometry(null, pen, MarkGeometry.ShapeOf(s.Rect, s.Ellipse));
    }

    private static void DrawArrow(DrawingContext dc, Mark.Arrow a)
    {
        var pen = MakePen(a.Color, a.Width, dashed: false);
        var head = MarkGeometry.ArrowHead(a.Start, a.End, a.Width);
        // 线只画到箭头底部，避免粗线把三角头顶破
        var end = head.Length > 0 ? head[1] + (head[2] - head[1]) / 2 : a.End;
        dc.DrawLine(pen, a.Start, end);
        if (head.Length == 3)
        {
            var geo = new PathGeometry();
            var figure = new PathFigure { StartPoint = head[0], IsClosed = true, IsFilled = true };
            figure.Segments.Add(new PolyLineSegment(new[] { head[1], head[2] }, true));
            geo.Figures.Add(figure);
            geo.Freeze();
            dc.DrawGeometry(Freeze(a.Color), null, geo);
        }
    }

    private static void DrawPolyline(DrawingContext dc, Mark.Polyline p)
    {
        if (p.Points.Count == 0) return;
        dc.DrawGeometry(null, MakePen(p.Color, p.Width, p.Dashed), MarkGeometry.Polyline(p.Points));
    }

    private static void DrawText(DrawingContext dc, Mark.Text t)
    {
        var box = t.Box;
        if (t.BackgroundOpacity > 0.01)
            dc.DrawRoundedRectangle(Freeze(Colors.Black, t.BackgroundOpacity), null, box, t.Radius, t.Radius);

        var ft = new FormattedText(t.Content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, t.FontSize, Brushes.Black, 1.0);
        var geo = ft.BuildGeometry(t.Origin);
        if (geo == null) return;

        if (t.OutlineWidth > 0.01)
            dc.DrawGeometry(null, new Pen(Freeze(t.OutlineColor), t.OutlineWidth * 2)
            {
                LineJoin = PenLineJoin.Round,
            }, geo);
        dc.DrawGeometry(Freeze(t.Color), null, geo);
    }

    private static void DrawSerial(DrawingContext dc, Mark.Serial n)
    {
        var box = n.Box;
        var fill = Freeze(n.Color);
        if (n.Square)
            dc.DrawRoundedRectangle(fill, null, box, 3, 3);
        else
            dc.DrawEllipse(fill, null, n.Center, Mark.Serial.Radius, Mark.Serial.Radius);

        // 数字走「反色」：深底白字、浅底黑字
        var text = new FormattedText(n.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
            MarkGeometry.SerialFontSize(n.Number), Freeze(Contrast(n.Color)), 1.0);
        var origin = new Point(n.Center.X - text.Width / 2, n.Center.Y - text.Height / 2);
        dc.DrawText(text, origin);
    }

    private static void DrawMosaic(DrawingContext dc, Mark.Mosaic m, BitmapSource baseImage, double scale)
    {
        var rect = m.Rect;
        if (rect.Width < 1 || rect.Height < 1) return;
        var tile = MosaicCache.GetValue(m, key => new MosaicTile { Image = BuildMosaic(key, baseImage, scale) });
        if (tile.Image == null) return;

        var drawing = new ImageDrawing(tile.Image, rect);
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
        dc.DrawDrawing(drawing);
    }

    private static BitmapSource? BuildMosaic(Mark.Mosaic m, BitmapSource baseImage, double scale)
    {
        var rect = m.Rect;
        int x = (int)Math.Floor(rect.X * scale);
        int y = (int)Math.Floor(rect.Y * scale);
        int w = (int)Math.Ceiling(rect.Width * scale);
        int h = (int)Math.Ceiling(rect.Height * scale);

        // 贴边时把越界部分裁掉，别让 CroppedBitmap 抛「矩形不在画面内」
        x = Math.Clamp(x, 0, Math.Max(0, baseImage.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, baseImage.PixelHeight - 1));
        w = Math.Clamp(w, 1, baseImage.PixelWidth - x);
        h = Math.Clamp(h, 1, baseImage.PixelHeight - y);

        try
        {
            var crop = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
            double block = Math.Max(2, m.Block * Math.Max(scale, 0.1));
            var small = new TransformedBitmap(crop, new ScaleTransform(1 / block, 1 / block));
            small.Freeze();
            return small;
        }
        catch (Exception)
        {
            return null;   // 底图还没准备好 / 越界：这一步画不出来就不画，别让整帧渲染失败
        }
    }

    private static void DrawSpotlight(DrawingContext dc, Mark.Spotlight sp, Rect canvas)
    {
        var hole = sp.Rect;
        if (hole.Width < 1 || hole.Height < 1) return;
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(canvas), new RectangleGeometry(hole));
        outside.Freeze();
        dc.PushClip(outside);
        dc.DrawRectangle(Freeze(Colors.Black, Math.Clamp(sp.Dim, 0, 1)), null, canvas);
        dc.Pop();
    }

    // ============ 小工具 ============

    private static Pen MakePen(Color color, double width, bool dashed)
    {
        var pen = new Pen(Freeze(color), Math.Max(0.5, width))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (dashed) pen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Freeze(Color color) => Freeze(color, 1);

    private static SolidColorBrush Freeze(Color color, double opacity)
    {
        var brush = new SolidColorBrush(WithAlpha(color, opacity));
        brush.Freeze();
        return brush;
    }

    /// <summary>把透明度乘进颜色（不透明度参数比画刷 Opacity 更好预测：叠加顺序不变）。</summary>
    public static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>在给定底色上取一个看得清的字色。</summary>
    public static Color Contrast(Color background)
    {
        double luma = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luma > 0.6 ? Color.FromRgb(0x18, 0x18, 0x1B) : Colors.White;
    }
}
