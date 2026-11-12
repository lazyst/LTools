using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LTools.Views.Controls;

/// <summary>
/// 拖动排序视觉辅助（统一规格：跟随光标的快照幽灵 + 虚线占位 + 让位动画，照 Quicker 面板）。
/// <list type="bullet">
/// <item><see cref="CreateGhost"/>：把源元素渲染成快照（圆角卡片 + 投影），拖动时跟随光标；</item>
/// <item><see cref="CreateHole"/>：原位置的虚线占位框（主色 55% 虚线 + 10% 填充）；</item>
/// <item><see cref="CapturePositions"/> / <see cref="AnimateFrom"/>：重排前后按元素身份做 FLIP 让位动画；</item>
/// <item><see cref="CaptureByIndex"/> / <see cref="AnimateReorderByIndex"/>：按索引映射的 ListBox 让位动画；</item>
/// <item><see cref="Slide"/>：动作单元素从指定偏移滑回原位。</item>
/// </list>
/// </summary>
internal static class DragFx
{
    /// <summary>让位动画时长（Quicker 为 220ms）。</summary>
    public const int ReorderDurationMs = 220;

    /// <summary>跟随 Quicker 的 <c>cubic-bezier(0.22, 1, 0.36, 1)</c>，用四次缓出近似。</summary>
    public static IEasingFunction ReorderEase => new QuarticEase { EasingMode = EasingMode.EaseOut };

    private const int DefaultDurationMs = 160;

    // ============ 快照幽灵 / 虚线占位 ============

    /// <summary>
    /// 把元素渲染成「被拿起来」的快照卡片：圆角 8、主题面底、1px 边、向下 12px 的柔和投影，
    /// 尺寸与源元素一致。源元素必须在可视树中且有实际尺寸。
    /// </summary>
    public static FrameworkElement CreateGhost(FrameworkElement source)
    {
        double w = Math.Max(1, source.ActualWidth);
        double h = Math.Max(1, source.ActualHeight);

        var image = new Image
        {
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            Source = Rasterize(source, w, h),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        return Card(image, w, h);
    }

    /// <summary>
    /// 不带快照的「卡片幽灵」：把调用方给的现成内容放进同一套卡片壳里。
    /// 磁贴用它（内容可再生，避免个别环境下栅格化得到空白图）；列表行仍用 <see cref="CreateGhost"/>。
    /// </summary>
    public static FrameworkElement CreateCard(UIElement body, double width, double height)
    {
        body.IsHitTestVisible = false;
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { body },
        };
        return Card(host, Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>幽灵卡片的统一外壳：主题面底、1px 边、圆角 8、向下 12px 投影。</summary>
    private static Border Card(UIElement child, double w, double h) => new()
    {
        Width = w,
        Height = h,
        Background = Brush("SurfaceBrush", Brushes.White),
        BorderBrush = Brush("BorderBrush", Brushes.Gainsboro),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = child,
        Opacity = 0.96,
        IsHitTestVisible = false,
        SnapsToDevicePixels = true,
        Effect = new DropShadowEffect
        {
            BlurRadius = 28,
            ShadowDepth = 12,
            Direction = 270,
            Opacity = 0.38,
            Color = Colors.Black,
        },
    };

    /// <summary>
    /// 把元素栅格化成快照。个别环境（高 DPI / 硬件加速 / 元素带特效）直接
    /// <see cref="RenderTargetBitmap.Render(Visual)"/> 会得到一张全空白的图，
    /// 这里检测到空白就改用 <see cref="VisualBrush"/> 重画一次。
    /// </summary>
    private static BitmapSource Rasterize(FrameworkElement source, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(source);
        int pw = Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY));
        double dx = 96 * dpi.DpiScaleX;
        double dy = 96 * dpi.DpiScaleY;

        var bmp = new RenderTargetBitmap(pw, ph, dx, dy, PixelFormats.Pbgra32);
        bmp.Render(source);
        if (!IsBlank(bmp)) return bmp;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new VisualBrush(source) { Stretch = Stretch.Fill },
                null, new Rect(0, 0, w, h));
        }
        var fallback = new RenderTargetBitmap(pw, ph, dx, dy, PixelFormats.Pbgra32);
        fallback.Render(visual);
        return IsBlank(fallback) ? bmp : fallback;
    }

    /// <summary>整张图所有像素都一样（全透明 / 全白）→ 视为没渲染出内容。</summary>
    private static bool IsBlank(BitmapSource bmp)
    {
        try
        {
            int stride = bmp.PixelWidth * 4;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            if (pixels.Length < 8) return true;

            uint first = BitConverter.ToUInt32(pixels, 0);
            for (int i = 4; i + 3 < pixels.Length; i += 4)
                if (BitConverter.ToUInt32(pixels, i) != first) return false;
            return true;
        }
        catch
        {
            return false;   // 取像素失败就按有内容处理，别白白丢掉快照
        }
    }

    /// <summary>虚线占位框：主色 55% 虚线 + 主色 10% 填充，圆角 8（照 Quicker 的 <c>--dragging</c>）。</summary>
    public static Rectangle CreateHole(double cornerRadius = 8)
    {
        var accent = Brush("PrimaryBrush", Brushes.SteelBlue);
        Brush stroke = accent.Clone();
        stroke.Opacity = 0.55;

        Brush fill = Brushes.Transparent;
        if (accent is SolidColorBrush sb)
            fill = new SolidColorBrush(Color.FromArgb(26, sb.Color.R, sb.Color.G, sb.Color.B));

        return new Rectangle
        {
            Stroke = stroke,
            StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeDashCap = PenLineCap.Round,
            Fill = fill,
            RadiusX = cornerRadius,
            RadiusY = cornerRadius,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>把覆盖层（幽灵 / 占位框）摆到目标元素的位置与尺寸（坐标为覆盖层自身坐标系）。</summary>
    public static void PlaceOver(Canvas layer, FrameworkElement overlay, FrameworkElement target, Visual window)
    {
        var rect = BoundsIn(target, layer, window);
        if (rect is not { } r)
        {
            overlay.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(overlay, r.X);
        Canvas.SetTop(overlay, r.Y);
        overlay.Width = Math.Max(1, r.Width);
        overlay.Height = Math.Max(1, r.Height);
        overlay.Visibility = Visibility.Visible;
    }

    /// <summary>元素在 <paramref name="layer"/> 坐标系里的矩形；元素不在可视树里返回 null。</summary>
    public static Rect? BoundsIn(FrameworkElement target, Canvas layer, Visual window)
    {
        if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0) return null;
        try
        {
            var topLeft = target.TransformToAncestor(window).Transform(new Point(0, 0));
            var p = ((UIElement)window).TranslatePoint(topLeft, layer);
            return new Rect(p, target.RenderSize);
        }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    // ============ FLIP 让位动画 ============

    /// <summary>按元素身份记录当前位置（相对 <paramref name="window"/>）；重排前调用。</summary>
    public static Dictionary<FrameworkElement, Point> CapturePositions(
        IEnumerable<FrameworkElement> elements, Visual window)
    {
        var map = new Dictionary<FrameworkElement, Point>();
        foreach (var el in elements)
        {
            try { map[el] = el.TransformToAncestor(window).Transform(new Point(0, 0)); }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
        return map;
    }

    /// <summary>布局已更新后调用：位置变化的元素从旧位置滑到新位置（220ms 缓出）。</summary>
    public static void AnimateFrom(
        IReadOnlyDictionary<FrameworkElement, Point> old, Visual window,
        int durationMs = ReorderDurationMs)
    {
        foreach (var pair in old)
        {
            var el = pair.Key;
            if (!el.IsVisible) continue;
            try
            {
                var now = el.TransformToAncestor(window).Transform(new Point(0, 0));
                Slide(el, pair.Value.X - now.X, pair.Value.Y - now.Y, durationMs, ReorderEase);
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
    }

    // ============ 按索引（ListBox） ============

    /// <summary>记录 ListBox 各 index 的 container 当前位置（相对 list）；重排前调用。</summary>
    public static Point?[] CaptureByIndex(ItemsControl list)
    {
        var arr = new Point?[list.Items.Count];
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
                arr[i] = c.TransformToAncestor(list).Transform(new Point(0, 0));
        return arr;
    }

    /// <summary>
    /// 重排已发生、布局已更新后调用：对每个新 index，用 <paramref name="oldIndexOf"/>
    /// 映射到旧 index 的位置作为起始，滑回当前位置。
    /// </summary>
    public static void AnimateReorderByIndex(
        ItemsControl list, Point?[] old, Func<int, int> oldIndexOf, int durationMs = DefaultDurationMs)
    {
        if (old.Length == 0) return;
        list.UpdateLayout();
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            int oi = oldIndexOf(i);
            if (oi < 0 || oi >= old.Length || old[oi] is not Point p) continue;
            var now = c.TransformToAncestor(list).Transform(new Point(0, 0));
            Slide(c, p.X - now.X, p.Y - now.Y, durationMs, ReorderEase);
        }
    }

    /// <summary>插入语义的新→旧索引映射：把 <paramref name="from"/> 拔出插到 <paramref name="to"/>。</summary>
    public static int OldIndexOfMove(int i, int from, int to)
    {
        if (i == to) return from;
        if (from < to) return i >= from && i < to ? i + 1 : i;
        return i > to && i <= from ? i - 1 : i;
    }

    /// <summary>交换语义的新→旧索引映射：交换 <paramref name="a"/> 与 <paramref name="b"/>。</summary>
    public static int OldIndexOfSwap(int i, int a, int b)
        => i == a ? b : i == b ? a : i;

    /// <summary>两元素交换动画（格子场景）：交换已发生后调用，让两格内容从对方旧位置滑入。</summary>
    public static void AnimateSwap(
        FrameworkElement a, FrameworkElement b, FrameworkElement relativeTo,
        int durationMs = DefaultDurationMs)
    {
        var pa = a.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        var pb = b.TransformToAncestor(relativeTo).Transform(new Point(0, 0));
        Slide(a, pb.X - pa.X, pb.Y - pa.Y, durationMs);
        Slide(b, pa.X - pb.X, pa.Y - pb.Y, durationMs);
    }

    /// <summary>动作单元素从指定偏移滑回原位（dx/dy 为起始偏移，目标 0）。</summary>
    public static void Slide(
        UIElement el, double dx, double dy, int durationMs = DefaultDurationMs,
        IEasingFunction? ease = null)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }

        // 上一次让位动画还在跑时，先停掉，否则 X/Y 的基准值写不进去
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.X = dx;
        tt.Y = dy;

        var dur = TimeSpan.FromMilliseconds(durationMs);
        var easing = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = easing });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = easing });
    }

    private static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
