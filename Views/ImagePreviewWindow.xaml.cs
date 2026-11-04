using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace LTools.Views;

/// <summary>
/// 模板图预览（无边框，点击任意处或 Esc 关闭）。只在编辑器里用来确认「截的是不是这块」。
/// </summary>
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(BitmapSource image, string caption)
    {
        InitializeComponent();
        Preview.Source = image;
        Caption.Text = caption + "　·　点击任意处或按 Esc 关闭";

        // 按图片比例定初始大小，最多占工作区 80%，不为一张小图撑出大窗口。
        Rect work = SystemParameters.WorkArea;
        double maxW = Math.Max(240, work.Width * 0.8);
        double maxH = Math.Max(180, work.Height * 0.8);
        double scale = Math.Min(1.0, Math.Min(maxW / image.PixelWidth, maxH / image.PixelHeight));
        Width = Math.Max(260, image.PixelWidth * scale + 32);
        Height = Math.Max(200, image.PixelHeight * scale + 58);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e) => Close();
}
