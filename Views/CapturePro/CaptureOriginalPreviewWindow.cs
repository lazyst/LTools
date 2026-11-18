using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果旁边的「原文预览」窗：贴原图，100%（按屏幕缩放折算 DIP），可左右停靠。
/// </summary>
/// <remarks>
/// 照上游 <c>screenshotoriginalimagepreviewwindow.cpp</c>：无边框、置顶、不抢焦点出场，
/// 说的是「识别结果和原图对不对得上」。识别窗那一侧只负责摆位（<see cref="PlaceBeside"/>），
/// 不把图像再复制一份到别的数据结构里。
/// </remarks>
internal sealed class CaptureOriginalPreviewWindow : Window
{
    private readonly BitmapSource _image;

    public CaptureOriginalPreviewWindow(BitmapSource image, double scale)
    {
        _image = image;

        Title = "原文预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;

        var chrome = new WindowChrome
        {
            CaptionHeight = 30,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, chrome);

        double dipWidth = image.PixelWidth / Math.Max(0.1, scale);
        double dipHeight = image.PixelHeight / Math.Max(0.1, scale);

        var picture = new Image
        {
            Source = image,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);

        var scroller = new ScrollViewer
        {
            Content = picture,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush?)TryFind("BgBrush") ?? Brushes.Black,
        };

        var title = new TextBlock
        {
            Text = string.Format("原文预览 · {0} × {1}", image.PixelWidth, image.PixelHeight),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = (Brush?)TryFind("SecondaryTextBrush") ?? Brushes.Gray,
        };

        var close = new Button
        {
            Content = "✕",
            Width = 30,
            Height = 28,
            Focusable = false,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush?)TryFind("TextBrush") ?? Brushes.White,
        };
        close.Click += (_, _) => Close();

        var header = new Grid { Height = 30 };
        header.Children.Add(title);
        header.Children.Add(close);
        close.HorizontalAlignment = HorizontalAlignment.Right;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        root.Children.Add(header);
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        Content = new Border
        {
            Background = (Brush?)TryFind("SurfaceBrush") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFind("BorderBrush") ?? Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = root,
        };

        Width = Math.Clamp(dipWidth + 2, 240, 1100);
        Height = Math.Clamp(dipHeight + 34, 160, 860);
    }

    /// <summary>窗口在屏幕上的 DIP 矩形（识别窗据此摆位）。</summary>
    public Rect Bounds => new(Left, Top, Width, Height);

    /// <summary>
    /// 贴在 <paramref name="anchor"/> 右边；右边放不下就翻到左边，再放不下就压住右边。
    /// </summary>
    public void PlaceBeside(Rect anchor, Rect workArea)
    {
        double gap = 10;
        double left = anchor.Right + gap;
        if (left + Width > workArea.Right) left = anchor.Left - gap - Width;
        if (left < workArea.Left) left = Math.Max(workArea.Left, workArea.Right - Width);

        double top = Math.Clamp(anchor.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        Left = left;
        Top = top;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long ex = Native.Win32.GetWindowLongPtr(hwnd, Native.Win32.GwlExstyle).ToInt64();
        Native.Win32.SetWindowLongPtr(hwnd, Native.Win32.GwlExstyle,
            (IntPtr)(ex | Native.Win32.WsExToolwindow));

        Native.Win32.SetWindowPos(hwnd, Native.Win32.HwndTopmost, 0, 0, 0, 0,
            Native.Win32.SwpNomove | Native.Win32.SwpNosize | Native.Win32.SwpNoactivate);
    }

    private object? TryFind(string key) => TryFindResource(key) ??
                                          Application.Current?.TryFindResource(key);
}
