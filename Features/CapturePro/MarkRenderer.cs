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

    /// <summary>滤镜处理后的整块位图同样按标注对象缓存（一次处理，拖动不再重算）。</summary>
    private static readonly ConditionalWeakTable<Mark.Filter, MosaicTile> FilterCache = new();

    /// <summary>画笔滤镜的处理结果同样按标注对象缓存。</summary>
    private static readonly ConditionalWeakTable<Mark.BrushFilter, MosaicTile> BrushFilterCache = new();

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
            case Mark.Filter f:
                DrawFilter(dc, f, baseImage, scale);
                break;
            case Mark.Spotlight sp:
                DrawSpotlight(dc, sp, canvas);
                break;
            case Mark.Line line:
                DrawLine(dc, line);
                break;
            case Mark.Distance distance:
                DrawDistance(dc, distance, scale);
                break;
            case Mark.Angle angle:
                DrawAngle(dc, angle, scale);
                break;
            case Mark.Bracket bracket:
                DrawBracket(dc, bracket);
                break;
            case Mark.SpeechBubble bubble:
                DrawSpeechBubble(dc, bubble);
                break;
            case Mark.Magnifier magnifier:
                DrawMagnifier(dc, magnifier, baseImage, scale);
                break;
            case Mark.Watermark watermark:
                DrawWatermark(dc, watermark, canvas);
                break;
            case Mark.Highlight highlight:
                dc.DrawRectangle(Freeze(highlight.Color, highlight.Opacity), null, highlight.Rect);
                break;
            case Mark.BrushFilter brush:
                DrawBrushFilter(dc, brush, baseImage, scale);
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
        switch (n.BadgeShape)
        {
            case SerialShape.Square:
                dc.DrawRectangle(fill, null, box);
                break;
            case SerialShape.Rounded:
                dc.DrawRoundedRectangle(fill, null, box, 6, 6);
                break;
            default:
                dc.DrawEllipse(fill, null, n.Center, Mark.Serial.Radius, Mark.Serial.Radius);
                break;
        }

        // 数字走「反色」：深底白字、浅底黑字；罗马 / 中文比数字长，按字数收字号
        string label = n.Label;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, SerialFontSize(n.Number, label), Freeze(Contrast(n.Color)), 1.0);
        var origin = new Point(n.Center.X - text.Width / 2, n.Center.Y - text.Height / 2);
        dc.DrawText(text, origin);
    }

    /// <summary>徽标里文字的字号：短数字按位数，长写法（罗马 / 中文）按字数缩小，别顶破边框。</summary>
    public static double SerialFontSize(int number, string label)
    {
        double size = MarkGeometry.SerialFontSize(number);
        if (label.Length > 2) size = Math.Min(size, 30.0 / label.Length);
        return Math.Max(7, size);
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

    /// <summary>
    /// 矩形滤镜：从底图裁出这块，按类型处理成一张位图再贴回去。
    /// 处理结果按标注对象缓存（拖动时只搬位置，不重算像素）。
    /// </summary>
    private static void DrawFilter(DrawingContext dc, Mark.Filter f, BitmapSource baseImage, double scale)
    {
        var rect = f.Rect;
        if (rect.Width < 1 || rect.Height < 1) return;
        var tile = FilterCache.GetValue(f, key => new MosaicTile { Image = BuildFilter(key, baseImage, scale) });
        if (tile.Image == null) return;

        var drawing = new ImageDrawing(tile.Image, rect);
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.Linear);
        dc.DrawDrawing(drawing);
    }

    private static BitmapSource? BuildFilter(Mark.Filter f, BitmapSource baseImage, double scale)
    {
        if (!TryCrop(baseImage, f.Rect, scale, out int x, out int y, out int w, out int h)) return null;

        try
        {
            var crop = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
            BitmapSource source = crop.Format == PixelFormats.Bgra32
                ? crop
                : new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);

            int stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);
            Process(f.Kind, f.Strength, pixels, w, h, Math.Max(scale, 0.1));

            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;   // 底图还没准备好 / 越界：这一步画不出来就不画
        }
    }

    /// <summary>把 DIP 矩形换成底图里的像素矩形（贴边裁掉越界部分；太小的不处理）。</summary>
    private static bool TryCrop(BitmapSource image, Rect rect, double scale, out int x, out int y, out int w, out int h)
    {
        x = (int)Math.Floor(rect.X * scale);
        y = (int)Math.Floor(rect.Y * scale);
        w = (int)Math.Ceiling(rect.Width * scale);
        h = (int)Math.Ceiling(rect.Height * scale);

        x = Math.Clamp(x, 0, Math.Max(0, image.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, image.PixelHeight - 1));
        w = Math.Clamp(w, 1, image.PixelWidth - x);
        h = Math.Clamp(h, 1, image.PixelHeight - y);
        return w > 0 && h > 0;
    }

    /// <summary>逐像素处理（BGRA，非预乘）。纯计算，<c>--selftest</c> 可以直接断言结果。</summary>
    public static void Process(FilterKind kind, double strength, byte[] pixels, int width, int height, double scale)
    {
        switch (kind)
        {
            case FilterKind.Blur:
            {
                // 强度 0–100 → 半径 1–24 像素（按缩放换算，逻辑上永远是屏幕上看着差不多粗）
                int radius = (int)Math.Round(Math.Clamp(strength, 0, 100) / 100 * 24 * scale);
                BoxBlur(pixels, width, height, Math.Clamp(radius, 1, 48));
                break;
            }
            case FilterKind.Grayscale:
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    byte luma = (byte)Math.Clamp(
                        Math.Round(0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i]), 0, 255);
                    pixels[i] = luma;
                    pixels[i + 1] = luma;
                    pixels[i + 2] = luma;
                }
                break;

            case FilterKind.Invert:
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)(255 - pixels[i]);
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                    pixels[i + 2] = (byte)(255 - pixels[i + 2]);
                }
                break;

            case FilterKind.Brighten:
            {
                double factor = Math.Clamp(strength / 50, 0, 2);
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)Math.Clamp(pixels[i] * factor, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] * factor, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] * factor, 0, 255);
                }
                break;
            }

            case FilterKind.Emboss:
                Emboss(pixels, width, height);
                break;
        }
    }

    /// <summary>浮雕：与左上角邻居求差、加 128 偏置，压成灰度（保留 alpha）。</summary>
    private static void Emboss(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        var copy = (byte[])pixels.Clone();
        for (int y = 1; y < height; y++)
        {
            for (int x = 1; x < width; x++)
            {
                int i = y * stride + x * 4;
                int j = (y - 1) * stride + (x - 1) * 4;
                for (int c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Clamp(128 + copy[i + c] - copy[j + c], 0, 255);
                pixels[i] = pixels[i + 1] = pixels[i + 2];
            }
        }
    }

    /// <summary>两趟方框模糊（横 → 竖），窗口滑动求和，O(pixels) 与半径无关。</summary>
    private static void BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        if (radius < 1 || width < 2 || height < 2) return;
        int stride = width * 4;
        var temp = new byte[pixels.Length];
        int window = radius * 2 + 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                    sum += pixels[row + Math.Clamp(k, 0, width - 1) * 4 + c];

                for (int x = 0; x < width; x++)
                {
                    temp[row + x * 4 + c] = (byte)(sum / window);
                    int add = Math.Clamp(x + radius + 1, 0, width - 1);
                    int sub = Math.Clamp(x - radius, 0, width - 1);
                    sum += pixels[row + add * 4 + c] - pixels[row + sub * 4 + c];
                }
            }
        }

        for (int x = 0; x < width; x++)
        {
            int column = x * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                    sum += temp[Math.Clamp(k, 0, height - 1) * stride + column + c];

                for (int y = 0; y < height; y++)
                {
                    pixels[y * stride + column + c] = (byte)(sum / window);
                    int add = Math.Clamp(y + radius + 1, 0, height - 1);
                    int sub = Math.Clamp(y - radius, 0, height - 1);
                    sum += temp[add * stride + column + c] - temp[sub * stride + column + c];
                }
            }
        }

        // 只写 RGB：alpha 留在 pixels 里没动，模糊不会把不透明区抹成透明
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

    // ============ P1：新增工具 ============

    private static void DrawLine(DrawingContext dc, Mark.Line line) =>
        dc.DrawLine(MakePen(line.Color, line.Width, line.Dashed), line.A, line.B);

    /// <summary>测距：主线 + 两端刻度 + 中点标签。</summary>
    private static void DrawDistance(DrawingContext dc, Mark.Distance d, double scale)
    {
        var pen = MakePen(d.Color, d.Width, dashed: false);
        dc.DrawLine(pen, d.A, d.B);

        var dir = d.B - d.A;
        if (dir.Length > 0.01)
        {
            dir.Normalize();
            var normal = new Vector(-dir.Y, dir.X) * (d.Tick / 2);
            dc.DrawLine(pen, d.A - normal, d.A + normal);
            dc.DrawLine(pen, d.B - normal, d.B + normal);
        }

        DrawLabel(dc, d.LabelBox(scale), d.LabelText(scale), d.Width);
    }

    /// <summary>测角：两条射线 + 圆弧 + 角度标签。</summary>
    private static void DrawAngle(DrawingContext dc, Mark.Angle a, double scale)
    {
        var pen = MakePen(a.Color, a.Width, dashed: false);
        dc.DrawLine(pen, a.Vertex, a.A);
        dc.DrawLine(pen, a.Vertex, a.B);

        double radius = a.ArcRadius;
        double half = a.Degrees * Math.PI / 360;
        var start = MarkGeometry.Polar(a.Vertex, a.Bisector - half, radius);
        var end = MarkGeometry.Polar(a.Vertex, a.Bisector + half, radius);

        var geo = new PathGeometry();
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
            a.Degrees > 180, SweepDirection.Clockwise, true));
        geo.Figures.Add(figure);
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        DrawLabel(dc, a.LabelBox, a.LabelText, a.Width);
    }

    /// <summary>括号：主线 + 两端穿过主线的垂直刻度。</summary>
    private static void DrawBracket(DrawingContext dc, Mark.Bracket b)
    {
        var pen = MakePen(b.Color, b.Width, dashed: false);
        dc.DrawLine(pen, b.A, b.B);

        var dir = b.B - b.A;
        if (dir.Length <= 0.01) return;
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X) * (b.Tick / 2);
        dc.DrawLine(pen, b.A - normal, b.A + normal);
        dc.DrawLine(pen, b.B - normal, b.B + normal);
    }

    private static void DrawSpeechBubble(DrawingContext dc, Mark.SpeechBubble b)
    {
        var pen = MakePen(b.Color, b.Width, dashed: false);
        dc.DrawRoundedRectangle(null, pen, b.Body, b.Radius, b.Radius);

        var tail = b.Tail;
        dc.DrawGeometry(null, pen, MarkGeometry.Polyline(new[] { tail[0], tail[2], tail[1] }));
    }

    /// <summary>放大镜：把镜头中心那一小块按倍率放大画进镜头框，再加一圈描边。</summary>
    private static void DrawMagnifier(DrawingContext dc, Mark.Magnifier m, BitmapSource baseImage, double scale)
    {
        var lens = m.Lens;
        if (lens.Width < 2 || lens.Height < 2) return;

        if (TryCrop(baseImage, m.SourceRect, scale, out int x, out int y, out int w, out int h))
        {
            try
            {
                var crop = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
                crop.Freeze();

                double radius = Math.Clamp(Math.Min(lens.Width, lens.Height) * 0.08, 2, 10);
                dc.PushClip(new RectangleGeometry(lens, radius, radius));
                var drawing = new ImageDrawing(crop, lens);
                RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.HighQuality);
                dc.DrawDrawing(drawing);
                dc.Pop();

                dc.DrawRoundedRectangle(null, MakePen(Color.FromArgb(0x88, 0, 0, 0), m.Width + 2, dashed: false),
                    lens, radius, radius);
                dc.DrawRoundedRectangle(null, MakePen(m.Color, m.Width, dashed: false), lens, radius, radius);
            }
            catch (Exception)
            {
                // 底图还没准备好 / 越界：画个空框提示位置，别让整帧失败
                dc.DrawRoundedRectangle(null, MakePen(m.Color, m.Width, dashed: false), lens, 4, 4);
            }
        }
        else
        {
            dc.DrawRoundedRectangle(null, MakePen(m.Color, m.Width, dashed: false), lens, 4, 4);
        }
    }

    /// <summary>水印：在整张图上按角度平铺文字，画在其它标注之上（后加入的本来就后画）。</summary>
    private static void DrawWatermark(DrawingContext dc, Mark.Watermark wm, Rect canvas)
    {
        if (canvas.Width < 2 || canvas.Height < 2) return;

        var text = new FormattedText(wm.Content, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, wm.FontSize, Freeze(WithAlpha(wm.Color, wm.Opacity)), 1.0);

        double stepX = Math.Max(24, text.Width + wm.Gap);
        double stepY = Math.Max(18, text.Height + wm.Gap * 0.8);
        double diagonal = Math.Sqrt(canvas.Width * canvas.Width + canvas.Height * canvas.Height);
        int nx = (int)Math.Ceiling(diagonal / stepX) + 1;
        int ny = (int)Math.Ceiling(diagonal / stepY) + 1;

        var center = new Point(canvas.X + canvas.Width / 2, canvas.Y + canvas.Height / 2);
        dc.PushClip(new RectangleGeometry(canvas));
        dc.PushTransform(new RotateTransform(wm.Rotation, center.X, center.Y));
        for (int row = -ny; row <= ny; row++)
        {
            double offset = (row & 1) == 0 ? 0 : stepX / 2;   // 隔行错开半格，看着像一片而不是一列列
            for (int col = -nx; col <= nx; col++)
            {
                double x = center.X + col * stepX + offset - text.Width / 2;
                double y = center.Y + row * stepY - text.Height / 2;
                dc.DrawText(text, new Point(x, y));
            }
        }
        dc.Pop();
        dc.Pop();
    }

    /// <summary>画笔滤镜：沿笔迹遮罩过滤底图像素，结果按标注缓存。</summary>
    private static void DrawBrushFilter(DrawingContext dc, Mark.BrushFilter f, BitmapSource baseImage, double scale)
    {
        var rect = f.Bounds;
        if (rect.Width < 1 || rect.Height < 1) return;
        var tile = BrushFilterCache.GetValue(f, key => new MosaicTile { Image = BuildBrushFilter(key, baseImage, scale) });
        if (tile.Image == null) return;

        var drawing = new ImageDrawing(tile.Image, rect);
        RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.Linear);
        dc.DrawDrawing(drawing);
    }

    private static BitmapSource? BuildBrushFilter(Mark.BrushFilter f, BitmapSource baseImage, double scale)
    {
        if (!TryCrop(baseImage, f.Bounds, scale, out int x, out int y, out int w, out int h)) return null;

        try
        {
            var crop = new CroppedBitmap(baseImage, new Int32Rect(x, y, w, h));
            BitmapSource source = crop.Format == PixelFormats.Bgra32
                ? crop
                : new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);

            int stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);

            var processed = (byte[])pixels.Clone();
            Process(f.Kind, f.Strength, processed, w, h, Math.Max(scale, 0.1));

            var mask = BuildStrokeMask(f.Points, f.Width / 2 * Math.Max(scale, 0.1), w, h, x, y, scale);
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                int at = i * 4;
                pixels[at] = processed[at];
                pixels[at + 1] = processed[at + 1];
                pixels[at + 2] = processed[at + 2];
            }

            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 笔迹遮罩（纯计算，<c>--selftest</c> 可断言）：像素到折线的距离 ≤ 半径就算命中。
    /// 逐段只扫自己的外接框，长笔迹也不会退化成「整块 × 全部线段」。
    /// </summary>
    public static bool[] BuildStrokeMask(IReadOnlyList<Point> points, double radius, int width, int height,
        int originX, int originY, double scale)
    {
        var mask = new bool[Math.Max(0, width * height)];
        if (points.Count == 0 || width <= 0 || height <= 0) return mask;

        double s = scale <= 0 ? 1 : scale;
        double r = Math.Max(0.75, radius);

        var pixel = new Point[points.Count];
        for (int i = 0; i < points.Count; i++)
            pixel[i] = new Point(points[i].X * s - originX, points[i].Y * s - originY);

        if (pixel.Length == 1)
        {
            Stamp(pixel[0], pixel[0], r, mask, width, height);
            return mask;
        }

        for (int i = 1; i < pixel.Length; i++)
            Stamp(pixel[i - 1], pixel[i], r, mask, width, height);

        return mask;
    }

    private static void Stamp(Point a, Point b, double radius, bool[] mask, int width, int height)
    {
        double minX = Math.Min(a.X, b.X) - radius;
        double maxX = Math.Max(a.X, b.X) + radius;
        double minY = Math.Min(a.Y, b.Y) - radius;
        double maxY = Math.Max(a.Y, b.Y) + radius;

        int left = Math.Max(0, (int)Math.Floor(minX));
        int right = Math.Min(width - 1, (int)Math.Ceiling(maxX));
        int top = Math.Max(0, (int)Math.Floor(minY));
        int bottom = Math.Min(height - 1, (int)Math.Ceiling(maxY));

        double r2 = radius * radius;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                double t = len2 <= double.Epsilon
                    ? 0
                    : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
                double px = a.X + t * dx, py = a.Y + t * dy;
                double d2 = (x - px) * (x - px) + (y - py) * (y - py);
                if (d2 <= r2) mask[y * width + x] = true;
            }
        }
    }

    /// <summary>深色底 + 白字的圆角标签（测距 / 测角共用）。</summary>
    private static void DrawLabel(DrawingContext dc, Rect box, string text, double lineWidth)
    {
        double radius = Math.Clamp(box.Height * 0.3, 2, 8);
        dc.DrawRoundedRectangle(Freeze(Colors.Black, 0.72), null, box, radius, radius);

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, Mark.Distance.FontSize, Freeze(Colors.White), 1.0);
        var origin = new Point(box.X + (box.Width - ft.Width) / 2, box.Y + (box.Height - ft.Height) / 2);
        dc.DrawText(ft, origin);
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
