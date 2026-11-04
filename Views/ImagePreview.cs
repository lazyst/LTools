using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;

namespace LTools.Views;

/// <summary>
/// 模板图预览：在编辑窗口内部弹出的轻量浮层（<see cref="Popup"/>），不是独立窗口。
/// </summary>
/// <remarks>
/// <para>
/// 之前用独立 Window 预览，遇到组合动作编辑器本身是 Topmost / 属于模态链时，
/// 新窗口会打乱激活与 Z 序（预览被压住、编辑器变灰甚至整条窗口链消失）。
/// 换成 Popup 后：不创建顶层窗口、不抢激活、不碰 Owner / Topmost，编辑窗口不可能被它影响。
/// </para>
/// <para>点击浮层任意处、按 Esc 或点浮层外面都会关闭；重复点预览只换图。</para>
/// </remarks>
internal static class ImagePreview
{
    private static Popup? _open;
    private static Window? _escOwner;
    private static KeyEventHandler? _escHandler;

    /// <summary>当前是否已打开（供调试 / 测试）。</summary>
    public static bool IsOpen => _open?.IsOpen == true;

    /// <summary>在 <paramref name="owner"/> 中央弹出预览。</summary>
    public static void Show(Window? owner, BitmapSource image, string caption)
    {
        Close();

        Rect work = SystemParameters.WorkArea;
        double maxW = Math.Max(240, work.Width * 0.8);
        double maxH = Math.Max(180, work.Height * 0.75);
        double scale = Math.Min(1.0, Math.Min(maxW / image.PixelWidth, maxH / image.PixelHeight));
        double w = Math.Max(120, image.PixelWidth * scale);
        double h = Math.Max(80, image.PixelHeight * scale);

        var picture = new Image
        {
            Source = image,
            Width = w,
            Height = h,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

        var text = new TextBlock
        {
            Text = $"{(caption.Length > 0 ? caption + "　·　" : "")}点击任意处或按 Esc 关闭",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new StackPanel { Children = { picture, text } },
        };

        // 点浮层自身也关：一边看一边点很自然。
        panel.MouseLeftButtonUp += (_, _) => Close();
        panel.MouseRightButtonUp += (_, e) => { e.Handled = true; Close(); };

        var popup = new Popup
        {
            PlacementTarget = owner,
            Placement = PlacementMode.Center,
            StaysOpen = false,          // 点外面自动关
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = panel,
        };

        popup.Closed += (_, _) => Detach();
        _open = popup;

        // 焦点留在编辑窗口里（Popup 不抢激活），Esc 由窗口的 PreviewKeyDown 处理。
        if (owner != null)
        {
            _escOwner = owner;
            _escHandler = (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
            owner.PreviewKeyDown += _escHandler;
        }

        popup.IsOpen = true;
    }

    /// <summary>关掉当前预览（没有则忽略）。</summary>
    public static void Close()
    {
        if (_open == null) { Detach(); return; }
        var popup = _open;
        _open = null;
        popup.IsOpen = false;
        Detach();
    }

    private static void Detach()
    {
        if (_escOwner != null && _escHandler != null)
        {
            try { _escOwner.PreviewKeyDown -= _escHandler; } catch { /* 窗口已关 */ }
        }
        _escOwner = null;
        _escHandler = null;
    }
}
