using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>CaptureResultWindow 的「原图预览」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 原文预览 ============

    /// <summary>
    /// 「原文预览」（照上游 <c>text_recognition/show_original_image_preview</c> +
    /// <c>setOriginalImagePreviewEnabled</c>）：勾上就在识别窗里**内嵌一栏**显示原图
    /// （默认形态，不遮挡任何东西、没有第二个窗口 / 层级问题）；点栏头的「⧉」弹出成独立小窗，
    /// 那时这一栏收起，小窗关掉再回到这一栏。
    /// </summary>
    private void Original_Changed(object sender, RoutedEventArgs e)
    {
        if (OriginalToggle.IsChecked == true) OpenPreview();
        else ClosePreview();
    }

    private CaptureOriginalPreviewWindow? _floating;
    private bool _previewDetached;
    private bool _previewPending;

    /// <summary>为了 1:1 把窗口撑大之前的矩形（收起预览时收回去）。</summary>
    private Rect _beforePreview;

    /// <summary>内嵌栏 / 小窗的按钮接线（两个构造函数都要走一遍）。</summary>
    private void WirePreview()
    {
        PreviewPane.PopOutRequested += DetachPreview;
        PreviewPane.CloseRequested += () => OriginalToggle.IsChecked = false;
    }

    private void OpenPreview()
    {
        if (_image == null) return;

        // 构造期 / Loaded 之前 ActualWidth 还是 0：等窗口量好尺寸再来
        if (!IsLoaded)
        {
            _previewPending = true;
            return;
        }

        if (_previewDetached) ShowFloatingPreview();
        else ShowPreviewPane();
    }

    /// <summary>
    /// 摆原文栏：**能 1:1 就 1:1**——文字层和原图都按 100% 完整显示（不滚不缩）。
    /// 宽度够就左右并列；宽度不够但高度够就上下并列；两样都不够才退回「右侧一栏 + 缩放」。
    /// </summary>
    /// <param name="fitWindow">
    /// true = 为了 1:1 可以把窗口撑大（用户刚勾上预览）；false = 窗口尺寸已经定了（最大化 / 还原后重排），
    /// 只按现在的内容区选边和大小。
    /// </param>
    private void ApplyPreviewPlan(bool fitWindow)
    {
        if (_image == null) return;

        Rect work = WorkAreaOfWindow();
        Size chrome = Chrome();
        var image = new Size(Math.Max(1, _image.PixelWidth / _scale),
            Math.Max(1, _image.PixelHeight / _scale));

        // 能用多少内容区：要撑窗口时按「工作区 − 固定开销」，否则按现在这一块
        var available = fitWindow
            ? new Size(Math.Max(1, work.Width - chrome.Width), Math.Max(1, work.Height - chrome.Height))
            : new Size(Math.Max(1, ContentBorder.ActualWidth - 2), Math.Max(1, ContentBorder.ActualHeight - 2));

        OriginalPanePlan plan = OriginalPreviewLayout.Plan(image, available, chrome);

        if (plan.OneToOne && fitWindow)
        {
            if (_beforePreview.IsEmpty)
                _beforePreview = new Rect(Left, Top, ActualWidth, ActualHeight);

            double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
            Width = Math.Min(plan.Window.Width, work.Width);
            Height = Math.Min(plan.Window.Height, work.Height);
            Left = Math.Clamp(cx - Width / 2, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(cy - Height / 2, work.Top, Math.Max(work.Top, work.Bottom - Height));
        }

        // 退回缩放时那一栏别把文字层挤没（最多占 55%）
        double pane = plan.OneToOne
            ? plan.Pane
            : Math.Min(plan.Pane, Math.Max(240, ActualWidth * 0.55));

        SetPaneSide(plan.Side, pane);
        PreviewPane.SetImage(_image, _scale);

        if (plan.OneToOne) PreviewPane.ActualSize();
        else PreviewPane.FitToViewport();
    }

    /// <summary>把原文栏挪到右边或下边（列 / 行 + 分隔条方向 + 边框），两种形态共用一份内容区。</summary>
    private void SetPaneSide(OriginalPaneSide side, double size)
    {
        bool bottom = side == OriginalPaneSide.Bottom;

        PreviewColumn.MinWidth = bottom ? 0 : 240;
        PreviewRow.MinHeight = bottom ? 160 : 0;
        PreviewColumn.Width = bottom ? new GridLength(0) : new GridLength(size);
        PreviewRow.Height = bottom ? new GridLength(size) : new GridLength(0);

        Grid.SetRow(PreviewHost, bottom ? 2 : 0);
        Grid.SetColumn(PreviewHost, bottom ? 0 : 2);
        Grid.SetColumnSpan(PreviewHost, bottom ? 3 : 1);

        Grid.SetRow(PreviewSplitter, bottom ? 1 : 0);
        Grid.SetColumn(PreviewSplitter, bottom ? 0 : 1);
        Grid.SetColumnSpan(PreviewSplitter, bottom ? 3 : 1);

        PreviewSplitter.ResizeDirection = bottom ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        PreviewSplitter.Width = bottom ? double.NaN : 7;
        PreviewSplitter.Height = bottom ? 7 : double.NaN;
        PreviewSplitter.HorizontalAlignment = bottom ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        PreviewSplitter.VerticalAlignment = bottom ? VerticalAlignment.Center : VerticalAlignment.Stretch;

        // 栏在右边是竖线、在下边是横线
        PreviewHost.BorderThickness = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);

        PreviewSplitter.Visibility = Visibility.Visible;
        PreviewHost.Visibility = Visibility.Visible;
    }

    /// <summary>窗口里除内容区之外的固定开销（标题栏 / 页签 / 底栏 / 外边距）：量出来，没量过就按布局常量估。</summary>
    private Size Chrome()
    {
        if (ContentBorder.ActualWidth > 0 && ContentBorder.ActualHeight > 0 &&
            ActualWidth > ContentBorder.ActualWidth && ActualHeight > ContentBorder.ActualHeight)
        {
            return new Size(ActualWidth - ContentBorder.ActualWidth,
                ActualHeight - ContentBorder.ActualHeight);
        }

        return new Size(26, 170);
    }

    /// <summary>最大化 / 还原之后按新的内容区重排一次（等这一帧布局量好再算）。</summary>
    private void ReplanPreview()
    {
        if (OriginalToggle.IsChecked != true) return;
        Dispatcher.BeginInvoke(new Action(() => ApplyPreviewPlan(fitWindow: false)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowPreviewPane()
    {
        if (_image == null) return;

        PreviewPane.CanPopOut = true;
        ApplyPreviewPlan(fitWindow: !_maximized);
    }

    private void HidePreviewPane()
    {
        if (PreviewHost.Visibility != Visibility.Visible) return;

        PreviewColumn.MinWidth = 0;
        PreviewRow.MinHeight = 0;
        PreviewColumn.Width = new GridLength(0);
        PreviewRow.Height = new GridLength(0);
        PreviewSplitter.Visibility = Visibility.Collapsed;
        PreviewHost.Visibility = Visibility.Collapsed;

        // 为了 1:1 撑大过就把窗口收回去（最大化态由它自己管）
        if (!_maximized && !_beforePreview.IsEmpty)
        {
            Rect before = _beforePreview;
            _beforePreview = default;
            Left = before.Left;
            Top = before.Top;
            Width = Math.Max(MinWidth, before.Width);
            Height = Math.Max(MinHeight, before.Height);
        }
    }

    /// <summary>栏头「⧉」：把这一栏换成独立小窗（尺寸 / 摆位见 <see cref="OriginalPreviewPlacement"/>）。</summary>
    private void DetachPreview()
    {
        if (_image == null) return;

        _previewDetached = true;
        HidePreviewPane();
        ShowFloatingPreview();
    }

    private void ShowFloatingPreview()
    {
        if (_image == null) return;

        if (_floating == null)
        {
            _floating = new CaptureOriginalPreviewWindow(_image, _scale);
            _floating.CloseRequested += () => OriginalToggle.IsChecked = false;
            _floating.Closed += (_, _) =>
            {
                _floating = null;
                if (!_previewDetached) return;

                // 小窗被直接关掉（不是点 ✕ / Esc）：退回内嵌那一栏，别把预览弄丢
                _previewDetached = false;
                if (OriginalToggle.IsChecked == true) ShowPreviewPane();
            };
            _floating.Show();
        }

        FollowOriginalPreview();
    }

    private void ClosePreview()
    {
        _previewDetached = false;

        CaptureOriginalPreviewWindow? floating = _floating;
        _floating = null;
        if (floating != null)
        {
            try { floating.Close(); }
            catch (InvalidOperationException) { /* 已经关掉 */ }
        }

        HidePreviewPane();
    }

    /// <summary>把原文小窗贴到识别窗旁边（右 → 左 → 上 → 下，四周都不够才贴内侧），并夹进工作区。</summary>
    private void FollowOriginalPreview()
    {
        if (_floating is not { IsVisible: true }) return;
        if (_floating.UserMoved) return;      // 用户自己挪过就别再拽回去

        Rect host = new(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        _floating.PlaceBeside(host, WorkArea());
    }

    /// <summary>识别窗所在显示器的工作区（DIP）。</summary>
    private Rect WorkArea()
    {
        ScreenGeometry? screen = CaptureRecordWindow.ScreenOf(_area);
        double scale = screen?.Scale ?? _scale;
        if (scale <= 0) scale = 1;
        if (screen == null) return SystemParameters.WorkArea;

        return new Rect(screen.WorkLeft / scale, screen.WorkTop / scale,
            screen.WorkWidth / scale, screen.WorkHeight / scale);
    }

    private void Fill_Changed(object sender, RoutedEventArgs e)
    {
        if (OcrCanvas == null) return;
        OcrCanvas.SetFillBackground(FillToggle.IsChecked == true);
    }

    /// <summary>
    /// 「保存图片」：照上游 <c>text_recognition/save_recognition_result_as_image</c>——
    /// 打开时保存带上当前识别 / 译文结果的图，关掉时只存原图。
    /// </summary>
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        BitmapSource? image = BuildSavableImage();
        if (image == null)
        {
            ToastService.Show("没有可保存的图片", "保存图片", 1600, ToastKind.warning);
            return;
        }

        CaptureSave.TrySave(this, image, CaptureSave.DefaultName, default,
            _area.Width > 0 && _area.Height > 0 ? _area : null);
    }

    /// <summary>保存 / 复制用的成品图（尊重「保存识别结果图片」开关）。</summary>
    /// <remarks>
    /// 识别结果图 = **原截图 + 原位替换的文字**（照上游 <c>renderScreenshotRecognitionImage</c>：
    /// 先把选区原图铺满，再按行把底色盖上去、把文字按原位置画回来）。所以这里必须
    /// <c>includeSource: true</c>——传 <c>false</c> 出来的是「只有文字、裁到文字外接框」的一张图，
    /// 保存下来根本不是截图（用户报过「保存的不是截图」）。
    /// </remarks>
    internal BitmapSource? BuildSavableImage()
    {
        if (_image == null) return null;
        if (!TextRecognitionStore.SaveResultAsImage) return _image;

        if (_translatedLines != null)
            return RenderTranslationImage() ?? _image;

        if (_model == null || _layer.Count == 0) return _image;

        return OcrImageRenderer.Render(_image, _layer, _model.Lines, includeSource: true,
                   fillBackground: FillToggle.IsChecked == true,
                   fillStyle: TextRecognitionStore.FillStyle,
                   highlight: TextRecognitionStore.ShowTextHighlighting)
               ?? _image;
    }

}
