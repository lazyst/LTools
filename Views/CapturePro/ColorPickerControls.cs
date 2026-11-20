using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 取色弹窗里的「饱和度 × 明度」方板（照上游 <c>AdColorPicker</c> 的色板）：
/// 底是当前色相、左上角白、底部黑；拖十字改 S / V，不改色相与 alpha。
/// </summary>
internal sealed class ColorBoard : FrameworkElement
{
    /// <summary>色相（0–360），由外面的色相条给。</summary>
    public double Hue { get; set; }

    /// <summary>饱和度 0–1（横轴）。</summary>
    public double Saturation { get; set; } = 1;

    /// <summary>明度 0–1（纵轴，上 = 1）。</summary>
    public double Brightness { get; set; } = 1;

    /// <summary>用户拖动之后（外面据此重算颜色）。</summary>
    public event EventHandler? Picked;

    public ColorBoard()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        var rect = new Rect(0, 0, w, h);

        dc.DrawRoundedRectangle(
            new SolidColorBrush(ColorText.FromHsb(Hue, 100, 100)), null, rect, 4, 4);

        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        // 左白右透 → 再叠一层上透下黑，就是标准的 SV 方板
        dc.DrawRectangle(new LinearGradientBrush(Colors.White,
            Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)), null, rect);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black,
            new Point(0, 0), new Point(0, 1)), null, rect);
        dc.Pop();

        var at = new Point(Math.Clamp(Saturation, 0, 1) * w, (1 - Math.Clamp(Brightness, 0, 1)) * h);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), at, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), at, 6, 6);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Pick(Point p)
    {
        Saturation = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
        Brightness = Math.Clamp(1 - p.Y / Math.Max(1, ActualHeight), 0, 1);
        InvalidateVisual();
        Picked?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>取色弹窗里的色相条（竖着，上 = 0°），拖动改色相。</summary>
internal sealed class HueBar : FrameworkElement
{
    /// <summary>色相 0–360。</summary>
    public double Hue { get; set; }

    public event EventHandler? Picked;

    private static readonly Color[] Stops =
    {
        Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x00, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x00, 0xFF), Color.FromRgb(0xFF, 0x00, 0xFF),
        Color.FromRgb(0xFF, 0x00, 0x00),
    };

    public HueBar()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        var rect = new Rect(0, 0, w, h);

        var brush = new LinearGradientBrush(new GradientStopCollection(
            BuildStops()), new Point(0, 0), new Point(0, 1));
        dc.DrawRoundedRectangle(brush, null, rect, 4, 4);

        double y = Math.Clamp(Hue, 0, 360) / 360 * h;
        dc.DrawLine(new Pen(Brushes.Black, 3), new Point(0, y), new Point(w, y));
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(0, y), new Point(w, y));
    }

    private static GradientStop[] BuildStops()
    {
        var stops = new GradientStop[Stops.Length];
        for (int i = 0; i < Stops.Length; i++)
            stops[i] = new GradientStop(Stops[i], i / (double)(Stops.Length - 1));
        return stops;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Pick(Point p)
    {
        Hue = Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1) * 360;
        InvalidateVisual();
        Picked?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>取色弹窗里的透明度条（棋盘格 + 透明到不透明的渐变），拖动改 alpha。</summary>
internal sealed class AlphaBar : FrameworkElement
{
    /// <summary>当前颜色（渐变用它，alpha 不看它）。</summary>
    public Color Color { get; set; } = Colors.White;

    /// <summary>透明度 0–255。</summary>
    public byte Alpha { get; set; } = 255;

    public event EventHandler? Picked;

    public AlphaBar()
    {
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        var rect = new Rect(0, 0, w, h);

        dc.PushClip(new RectangleGeometry(rect, 4, 4));

        const double cell = 6;
        dc.DrawRectangle(Brushes.White, null, rect);
        var gray = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8));
        for (int row = 0; row * cell < h; row++)
            for (int col = 0; col * cell < w; col++)
                if ((row + col) % 2 == 0)
                    dc.DrawRectangle(gray, null,
                        new Rect(col * cell, row * cell, cell, cell));

        var opaque = Color.FromRgb(Color.R, Color.G, Color.B);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, Color.R, Color.G, Color.B),
            opaque, new Point(0, 0), new Point(1, 0)), null, rect);
        dc.Pop();

        double x = Math.Clamp((int)Alpha, 0, 255) / 255.0 * w;
        dc.DrawLine(new Pen(Brushes.Black, 3), new Point(x, 0), new Point(x, h));
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, 0), new Point(x, h));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Pick(Point p)
    {
        Alpha = (byte)Math.Clamp(Math.Round(p.X / Math.Max(1, ActualWidth) * 255), 0, 255);
        InvalidateVisual();
        Picked?.Invoke(this, EventArgs.Empty);
    }
}
