using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views.Controls;

/// <summary>
/// 模板图选择：缩略图 / 框选截图 / 换图 / 预览 + 路径框。
/// 「截图找图」取值器与「图片点击」动作共用，避免两处各写一套取图逻辑。
/// </summary>
/// <remarks>
/// 路径语义与 <see cref="ImageLibrary"/> 一致：images 下存文件名、user 下存数据根相对路径。
/// 框选截图会把截下来的图存进 resources\images，并通过 <see cref="Captured"/> 告诉调用方框在哪儿。
/// </remarks>
public partial class TemplateImagePicker : UserControl
{
    /// <summary>用户 / 代码改动了模板图路径。</summary>
    public event Action? Changed;

    /// <summary>框选截图完成，参数是刚框的区域（虚拟桌面物理像素）。</summary>
    public event Action<Int32Rect>? Captured;

    /// <summary>模板图路径（用户可手改，支持绝对路径）。</summary>
    public string Path
    {
        get => (PathBox.Text ?? "").Trim();
        set => PathBox.Text = value ?? "";
    }

    private string? _resourceRoot;

    /// <summary>
    /// 草稿编辑时的资源根（<c>staging/&lt;id&gt;/files</c>）：缩略图按它解析 <c>files/x.png</c>。
    /// 正式动作 / 新建为 null，解析规则不变。
    /// </summary>
    public string? ResourceRoot
    {
        get => _resourceRoot;
        set
        {
            if (_resourceRoot == value) return;
            _resourceRoot = value;
            LoadThumbnail();
        }
    }

    public TemplateImagePicker()
    {
        InitializeComponent();
        LoadThumbnail();
    }

    // —— 取图 ——

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        ImageLibrary.EnsureDir(); // resources\user 得先存在才能当初始目录

        var dlg = new OpenFileDialog
        {
            Title = "选择模板图片",
            Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            InitialDirectory = ImageLibrary.UserDir,
        };
        if (dlg.ShowDialog() != true) return;

        // 外部图片复制进 resources\user：配置里存数据根相对路径，备份换机后才不会断链。
        PathBox.Text = ImageLibrary.ImportUserImage(dlg.FileName);
    }

    /// <summary>框选截图：截下来的就是模板；区域通过 <see cref="Captured"/> 交给调用方。</summary>
    private void CaptureShot_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ScreenPicker.TryPickRegion(window, out var area, out var image)) return;

        try
        {
            string path = ImageLibrary.SaveGenerated(image);
            PathBox.Text = ImageLibrary.ToStoredPath(path);
            Captured?.Invoke(area);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(window, "框选截图", "保存模板图失败：" + ex.Message);
        }
    }

    /// <summary>预览：在编辑窗口里弹出浮层（Popup），不新开顶层窗口。</summary>
    private void PreviewImage_Click(object sender, RoutedEventArgs e)
    {
        string path = ResolvePath();
        if (!File.Exists(path))
        {
            ShowHint("没有可预览的图片。");
            return;
        }

        BitmapSource image;
        try
        {
            image = LoadBitmap(path);
        }
        catch (Exception ex)
        {
            ShowHint("预览失败：" + ex.Message);
            return;
        }

        ImagePreview.Show(Window.GetWindow(this), image, System.IO.Path.GetFileName(path));
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e) => PreviewImage_Click(sender, e);

    /// <summary>路径改动后刷新缩略图，并通知调用方（编辑器据此更新保存状态）。</summary>
    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        LoadThumbnail();
        Changed?.Invoke();
    }

    // —— 缩略图 ——

    private string ResolvePath()
    {
        string text = Path;
        return text.Length == 0 ? "" : ImageLibrary.Resolve(text, ResourceRoot);
    }

    private void LoadThumbnail()
    {
        if (ImageThumb == null) return;

        string text = Path;
        if (text.Length == 0)
        {
            SetThumb(null, "无图", "还没有模板图，点「框选截图」直接在屏幕上截取。");
            return;
        }

        string path = ResolvePath();
        if (!File.Exists(path))
        {
            SetThumb(null, "找不到", "文件不存在：" + text);
            return;
        }

        try
        {
            var bmp = LoadBitmap(path);
            SetThumb(bmp, "", $"{bmp.PixelWidth} × {bmp.PixelHeight} px · {System.IO.Path.GetFileName(path)} · 点击缩略图可预览");
        }
        catch (Exception ex)
        {
            SetThumb(null, "无法读取", "图片读取失败：" + ex.Message);
        }
    }

    private void SetThumb(BitmapSource? image, string placeholder, string hint)
    {
        ImageThumb.Source = image;
        ImageThumbHint.Text = placeholder;
        ImageThumbHint.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        ImageFileHint.Text = hint;
        PreviewImageBtn.IsEnabled = image != null;
    }

    private void ShowHint(string text) => ImageFileHint.Text = text;

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(System.IO.Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
