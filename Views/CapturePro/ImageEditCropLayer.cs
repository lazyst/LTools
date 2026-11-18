using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的裁剪预览层（照上游 <c>ImageEditingCanvas::renderAfterCanvas</c>）：
/// 要剪掉的那一条铺半透明红、白字「N px」，两条 <c>#4096FF</c> 虚线导引线。
/// </summary>
/// <remarks>
/// 只画，不接鼠标（命中与拖动在 <see cref="ImageEditWindow"/> 里按图像坐标算）。
/// 坐标系是窗口内容区（DIP），<see cref="ImageRect"/> / <see cref="Strip"/> 都由调用方给。
/// </remarks>
internal sealed class ImageEditCropLayer : FrameworkElement
{
    private static readonly SolidColorBrush StripFill = Frozen(Color.FromArgb(95, 240, 65, 65));
    private static readonly Pen GuidePen = FrozenDashPen(Color.FromRgb(0x40, 0x96, 0xFF), 1);

    /// <summary>图像在窗口内容区里占的矩形（DIP）。</summary>
    public Rect ImageRect { get; set; } = Rect.Empty;

    /// <summary>要剪掉的那一条（DIP，可能在 <see cref="ImageRect"/> 之外的部分会被裁掉）。</summary>
    public Rect Strip { get; set; } = Rect.Empty;

    /// <summary>是剪行（横带）还是剪列（纵带）。</summary>
    public bool Rows { get; set; } = true;

    /// <summary>这一条的像素数（0 = 只画一条导引线）。</summary>
    public int Pixels { get; set; }

    /// <summary>当前屏幕 DPI 比（导引线往里收半个物理像素用）。</summary>
    public double DpiScale { get; set; } = 1;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (ImageRect.Width <= 0 || ImageRect.Height <= 0) return;

        dc.PushClip(new RectangleGeometry(ImageRect));

        if (Pixels > 0 && Strip.Width > 0 && Strip.Height > 0)
            dc.DrawRectangle(StripFill, null, Strip);

        // 导引线夹在图像里：最边上那条才不会画到外面去（上游 inset = 半个物理像素）
        double dpi = DpiScale > 0 ? DpiScale : 1;
        double inset = Math.Min((Rows ? ImageRect.Height : ImageRect.Width) / 2, 0.5 / dpi);

        if (Rows)
        {
            DrawGuide(dc, GuideAt(Strip.Top, ImageRect.Top + inset, ImageRect.Bottom - inset));
            if (Pixels > 0) DrawGuide(dc, GuideAt(Strip.Bottom, ImageRect.Top + inset, ImageRect.Bottom - inset));
        }
        else
        {
            DrawGuide(dc, GuideAt(Strip.Left, ImageRect.Left + inset, ImageRect.Right - inset));
            if (Pixels > 0) DrawGuide(dc, GuideAt(Strip.Right, ImageRect.Left + inset, ImageRect.Right - inset));
        }

        if (Pixels > 0 && Strip.Width > 0 && Strip.Height > 0)
        {
            var text = new FormattedText(
                Pixels.ToString(CultureInfo.InvariantCulture) + " px",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, dpi);

            var at = new Point(
                Strip.X + (Strip.Width - text.Width) / 2,
                Strip.Y + (Strip.Height - text.Height) / 2);
            dc.DrawText(text, at);
        }

        dc.Pop();
    }

    private LineGeometry GuideAt(double position, double minimum, double maximum)
    {
        double value = Math.Clamp(position, Math.Min(minimum, maximum), Math.Max(minimum, maximum));
        return Rows
            ? new LineGeometry(new Point(ImageRect.Left, value), new Point(ImageRect.Right, value))
            : new LineGeometry(new Point(value, ImageRect.Top), new Point(value, ImageRect.Bottom));
    }

    private static void DrawGuide(DrawingContext dc, LineGeometry line) => dc.DrawGeometry(null, GuidePen, line);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenDashPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0),
        };
        pen.Freeze();
        return pen;
    }
}
