using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 图片参数：缩略图 + 「点这里框选 / 换图」那一行，以及框选 / 换图 / 预览三个入口。
/// </summary>
public partial class ValueField
{
    private void UpdateImage()
    {
        string stored = (Box.Text ?? "").Trim();
        if (stored.Length == 0)
        {
            Thumb.Source = null;
            ImageName.Text = "无图";
            ImageName.Foreground = (Brush)FindResource("HintTextBrush");
            ImageSize.Text = "点这里框选 / 换图";
            return;
        }

        string path = ImageLibrary.Resolve(stored, ResourceRoot);
        if (!File.Exists(path))
        {
            Thumb.Source = null;
            ImageName.Text = stored;
            ImageName.Foreground = (Brush)FindResource("DangerBrush");
            ImageSize.Text = "文件不存在";
            return;
        }

        try
        {
            var bmp = LoadBitmap(path);
            Thumb.Source = bmp;
            ImageName.Text = Path.GetFileName(path);
            ImageName.Foreground = (Brush)FindResource("TextBrush");
            ImageSize.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight}";
        }
        catch
        {
            Thumb.Source = null;
            ImageName.Text = stored;
            ImageName.Foreground = (Brush)FindResource("DangerBrush");
            ImageSize.Text = "读不出来";
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenMenu();
    }

    private void CaptureImage()
    {
        var window = Window.GetWindow(this);
        if (!ScreenPicker.TryPickRegion(window, out var area, out var image)) return;

        try
        {
            string path = ImageLibrary.SaveGenerated(image);
            SetLiteral(ImageLibrary.ToStoredPath(path));
            Captured?.Invoke(area);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(window, "框选截图", "保存模板图失败：" + ex.Message);
        }
    }

    private void PickImageFile()
    {
        ImageLibrary.EnsureDir();
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            InitialDirectory = ImageLibrary.UserDir,
        };
        if (dlg.ShowDialog() != true) return;
        SetLiteral(ImageLibrary.ImportUserImage(dlg.FileName));
    }

    private void PreviewImage()
    {
        string stored = (Box.Text ?? "").Trim();
        string path = stored.Length == 0 ? "" : ImageLibrary.Resolve(stored, ResourceRoot);
        if (!File.Exists(path))
        {
            ToastService.Show("没有可预览的图片", "参数", 2500, ToastKind.warning);
            return;
        }

        try
        {
            ImagePreview.Show(Window.GetWindow(this), LoadBitmap(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ToastService.Show("预览失败：" + ex.Message, "参数", ToastService.StickyMs, ToastKind.error);
        }
    }

}
