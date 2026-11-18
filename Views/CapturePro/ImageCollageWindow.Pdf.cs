using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro.Collage;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拼接窗的「PDF 文档」模式（照上游 <c>ImageStitchingPdfSettingsWidget</c> +
/// <c>ImageStitchingPdfPageGrid</c> + <c>ImageStitchingPdfTemplateWidget</c> + <c>collagewindow.cpp</c>
/// 的 PDF 导出一节）。
/// </summary>
/// <remarks>
/// <para>
/// 输出类型切到 PDF 之后：静态画布 / 缩略图 / 空态 / 画布工具条全部收起，换成「页面网格」
/// （分页拼接）或「模板画布 + 页面条 + 缩略图条」（模板拼接）；表单只剩 PDF 设置；
/// 页脚改「毫米尺寸 + 共 N 页」，图像编辑 / 复制收起，「保存」走 PDF 导出。
/// </para>
/// <para>
/// **预览一律用 <see cref="CollageDocument.LastValidPdfSettings"/>**（照上游）：表单上填错的东西
/// 只进 <see cref="CollageDocument.PdfSettings"/>，画布仍按上一次有效的设置画，并挂一条
/// 「页面预览使用上一次有效的设置」；哪一项填错由各字段下方的红字说。
/// </para>
/// <para>
/// 导出照上游 <c>startPdfExport</c>：**先**置导出态、禁用表单，再弹文件框（取消文件框 = 取消导出，
/// 不算失败）；导出期间「取消」变成「取消导出」；成功关窗，失败 / 取消留窗并在页脚写清
/// 「已保存页面 / 未保存页面」。
/// </para>
/// </remarks>
public partial class ImageCollageWindow
{
    private readonly List<CollageThumbnail> _pdfCards = new();

    private CollagePdfSettings _savedPdf = CollagePdfSettings.Default;

    private bool _pdfExporting;
    private bool _pdfChoosing;
    private volatile bool _pdfCancelRequested;
    private int _pdfGeneration;

    private int _pdfPageNumber = 1;
    private int _pdfTotal = 1;
    private int _pdfPercent;
    private DispatcherTimer? _pdfProgressTimer;

    private long _pdfDragSource;
    private FrameworkElement? _pdfDragGhost;

    // ============ 初始化 ============

    /// <summary>接上两个 PDF 宿主与各自的选中 / 替换 / 预览 / 删除事件。</summary>
    private void InitPdf()
    {
        PdfGrid.Document = _doc;
        PdfTemplateCanvas.Document = _doc;

        PdfGrid.SelectionRequested += id => _doc.Select(id);
        PdfGrid.ReplaceRequested += PickReplacement;
        PdfGrid.RemoveRequested += id => _doc.Remove(id);
        PdfGrid.PreviewRequested += ToggleOriginalPreview;
        PdfGrid.LayoutChanged += PositionPdfToolbars;

        PdfTemplateCanvas.RemoveRequested += id => _doc.Remove(id);
        PdfTemplateCanvas.PageChanged += _ => SyncPdfPageBar();
        PdfTemplateCanvas.LayoutChanged += PositionPdfToolbars;
    }

    private IReadOnlyList<CollagePdfTemplateButton> PdfTemplateButtons() => new[]
    {
        PdfTemplateTwo, PdfTemplateIdCard, PdfTemplateThree, PdfTemplateFour, PdfTemplateFive,
        PdfTemplateSix, PdfTemplateSeven, PdfTemplateEight, PdfTemplateNine,
    };

    // ============ 表单 → 控件 ============

    /// <summary>按文档刷新 PDF 表单（照上游 <c>collagepdfsettingswidget.cpp refresh()</c>）。</summary>
    private void SyncPdfForm(bool force = false)
    {
        if (_closing) return;

        var settings = _doc.PdfSettings;
        var errors = CollagePdfGeometry.Validate(settings);
        bool templateBased = settings.IsTemplateBased;
        bool imageSize = settings.IsImageSizePageBased;

        _syncing = true;
        try
        {
            OutStatic.IsChecked = _doc.OutputType == CollageOutputType.StaticImage;
            OutPdf.IsChecked = _doc.OutputType == CollageOutputType.PdfDocument;

            PdfMethodPage.IsChecked = !templateBased;
            PdfMethodTemplate.IsChecked = templateBased;

            PdfTemplateRow.Visibility = templateBased ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in PdfTemplateButtons())
            {
                button.Selected = button.TemplateKind == settings.Template;
                button.Landscape = settings.Orientation == CollagePdfOrientation.Landscape;
            }

            if (PdfPaperBox.SelectedItem is not ComboBoxItem selected ||
                (selected.Tag as string) != settings.PaperKey)
            {
                foreach (ComboBoxItem item in PdfPaperBox.Items)
                {
                    if ((item.Tag as string) != settings.PaperKey) continue;
                    PdfPaperBox.SelectedItem = item;
                    break;
                }
            }

            PdfPortrait.IsChecked = settings.Orientation == CollagePdfOrientation.Portrait;
            PdfLandscape.IsChecked = settings.Orientation == CollagePdfOrientation.Landscape;

            PdfOrientationRow.Visibility = imageSize ? Visibility.Collapsed : Visibility.Visible;
            PdfCustomRow.Visibility = settings.Paper == CollagePdfPaper.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSpacingRow.Visibility = imageSize ? Visibility.Collapsed : Visibility.Visible;
            PdfImageSpacingPanel.Visibility = templateBased
                ? Visibility.Visible
                : Visibility.Collapsed;
            PdfSpacingColumn.Width = templateBased
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

            SyncPdfNumber(PdfWidthBox, settings.CustomWidthMm, force);
            SyncPdfNumber(PdfHeightBox, settings.CustomHeightMm, force);
            SyncPdfNumber(PdfMarginBox, settings.MarginMm, force);
            SyncPdfNumber(PdfImageSpacingBox, settings.ImageSpacingMm, force);

            PdfExportCombined.IsChecked = settings.ExportMode == CollagePdfExportMode.Combined;
            PdfExportPerPage.IsChecked = settings.ExportMode == CollagePdfExportMode.PerPage;

            if (!PdfQualitySlider.IsMouseCaptureWithin)
                PdfQualitySlider.Value = Math.Clamp(settings.ImageQuality, 0, 100);

            SetPdfError(PdfMethodError, errors, "stitching_method");
            SetPdfError(PdfTemplateError, errors, "template_type");
            SetPdfError(PdfPaperError, errors, "paper_size");
            SetPdfError(PdfOrientationError, errors, "orientation");
            SetPdfError(PdfWidthError, errors, "custom_width_mm");
            SetPdfError(PdfHeightError, errors, "custom_height_mm");
            SetPdfError(PdfMarginError, errors, "margin_top_mm", "margin_bottom_mm", "margin_left_mm",
                "margin_right_mm");
            SetPdfError(PdfImageSpacingError, errors, "image_spacing_mm");
            SetPdfError(PdfExportError, errors, "export_mode");
            SetPdfError(PdfQualityError, errors, "image_quality");
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 数字框刷新：设置值有限且和输入框里的数不同才回写（用户写的「210.0」别被改成「210」）；
    /// 设置值非有限而输入框还能解析出数时清空（照上游「保留用户原文」的口径）。
    /// </summary>
    private static void SyncPdfNumber(TextBox box, double value, bool force)
    {
        if (!force && box.IsKeyboardFocusWithin) return;

        bool parsed = TryParsePdfNumber(box.Text, out double current);
        if (double.IsFinite(value))
        {
            if (parsed && Math.Abs(current - value) < 1e-9) return;
            box.Text = value.ToString("0.#####", CultureInfo.InvariantCulture);
            return;
        }

        if (parsed) box.Text = "";
    }

    private static void SetPdfError(TextBlock block, Dictionary<string, string> errors,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!errors.TryGetValue(key, out string? message) || string.IsNullOrEmpty(message))
                continue;
            block.Text = message;
            return;
        }

        block.Text = "";
    }

    private static bool TryParsePdfNumber(string? text, out double value)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            value = double.NaN;
            return false;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ============ 控件 → 表单 ============

    private void OutputType_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        var type = OutPdf.IsChecked == true
            ? CollageOutputType.PdfDocument
            : CollageOutputType.StaticImage;
        if (type == _doc.OutputType) return;

        CommitPendingFields();
        if (type == CollageOutputType.PdfDocument) ReleasePreview();

        _doc.SetOutputType(type);
        SetStatus("", StatusKind.None);
        UpdateChrome();
        PositionToolbars();
        PositionPdfToolbars();
    }

    private void PdfMethod_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        CommitPendingFields();
        var method = PdfMethodTemplate.IsChecked == true
            ? CollagePdfMethod.TemplateBased
            : CollagePdfMethod.PageBased;
        ApplyPdfSettings(settings => settings with { Method = method });
    }

    private void PdfTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;
        if (sender is not CollagePdfTemplateButton button) return;

        CommitPendingFields();
        ApplyPdfSettings(settings => settings with { Template = button.TemplateKind });
    }

    private void PdfPaper_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _closing) return;
        if (PdfPaperBox.SelectedItem is not ComboBoxItem item) return;

        CommitPendingFields();
        var paper = CollagePdfSettings.ParsePaper(item.Tag as string);
        ApplyPdfSettings(settings => settings with { Paper = paper });
    }

    private void PdfOrientation_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        CommitPendingFields();
        var orientation = PdfLandscape.IsChecked == true
            ? CollagePdfOrientation.Landscape
            : CollagePdfOrientation.Portrait;
        ApplyPdfSettings(settings => settings with { Orientation = orientation });
    }

    private void PdfExport_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _closing) return;

        CommitPendingFields();
        var mode = PdfExportPerPage.IsChecked == true
            ? CollagePdfExportMode.PerPage
            : CollagePdfExportMode.Combined;
        ApplyPdfSettings(settings => settings with { ExportMode = mode });
    }

    private void PdfQuality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _closing) return;

        int quality = Math.Clamp((int)Math.Round(PdfQualitySlider.Value), 0, 100);
        ApplyPdfSettings(settings => settings with { ImageQuality = quality });
    }

    private void PdfTemplateFit_Click(object sender, RoutedEventArgs e) => PdfTemplateCanvas.FitToCanvas();

    private void PdfPrevPage_Click(object sender, RoutedEventArgs e) =>
        PdfTemplateCanvas.SetPage(PdfTemplateCanvas.PageIndex - 1);

    private void PdfNextPage_Click(object sender, RoutedEventArgs e) =>
        PdfTemplateCanvas.SetPage(PdfTemplateCanvas.PageIndex + 1);

    /// <summary>提交一次 PDF 设置（照上游 <c>settingsChanged</c>）。</summary>
    private void ApplyPdfSettings(Func<CollagePdfSettings, CollagePdfSettings> edit)
    {
        if (_loading || _exporting || _pdfExporting || _closing)
        {
            SyncPdfForm(force: true);
            return;
        }

        if (!_doc.SetPdfSettings(edit(_doc.PdfSettings))) return;

        // 文档事件已经刷过一遍，这里只把表单拉到真值（值被规整 / 校验不通过时用户要看得见）。
        SyncPdfForm(force: true);
        UpdateChrome();
        PositionPdfToolbars();
        SaveSettings();
    }

    /// <summary>四个 PDF 数字框（单位 / 页边距 / 图像间距）。</summary>
    private bool IsPdfField(TextBox box) =>
        ReferenceEquals(box, PdfWidthBox) || ReferenceEquals(box, PdfHeightBox) ||
        ReferenceEquals(box, PdfMarginBox) || ReferenceEquals(box, PdfImageSpacingBox);

    /// <summary>PDF 数字框提交：解析不了就存 NaN（校验会给红字，画布退回上一次有效的设置）。</summary>
    private void CommitPdfField(TextBox box)
    {
        if (_syncing || _closing) return;
        if (_loading || _exporting || _pdfExporting)
        {
            SyncPdfForm(force: true);
            return;
        }

        TryParsePdfNumber(box.Text, out double value);
        var current = _doc.PdfSettings;

        CollagePdfSettings next;
        if (ReferenceEquals(box, PdfWidthBox))
            next = current with { CustomWidthMm = value };
        else if (ReferenceEquals(box, PdfHeightBox))
            next = current with { CustomHeightMm = value };
        else if (ReferenceEquals(box, PdfMarginBox))
            next = current.WithMargin(value);
        else if (ReferenceEquals(box, PdfImageSpacingBox))
            next = current with { ImageSpacingMm = value };
        else
            return;

        if (next == current) return;

        _doc.SetPdfSettings(next);
        SyncPdfForm(force: true);
        UpdateChrome();
        PositionPdfToolbars();
        SaveSettings();
    }

    // ============ 缩略图条（模板拼接） ============

    /// <summary>按文档顺序对齐 PDF 缩略图（换了顺序 / 增删就重建；只改内容就原地更新）。</summary>
    private void SyncPdfThumbnails()
    {
        bool templateMode = _doc.OutputType == CollageOutputType.PdfDocument &&
                            _doc.LastValidPdfSettings.IsTemplateBased;
        if (!templateMode)
        {
            if (_pdfCards.Count > 0)
            {
                CancelPdfThumbnailDrag();
                PdfThumbHost.Children.Clear();
                _pdfCards.Clear();
            }

            return;
        }

        var items = _doc.Items;
        bool rebuild = _pdfCards.Count != items.Count;
        if (!rebuild)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (_pdfCards[i].ItemId == items[i].Id) continue;
                rebuild = true;
                break;
            }
        }

        if (rebuild)
        {
            CancelPdfThumbnailDrag();
            PdfThumbHost.Children.Clear();
            _pdfCards.Clear();

            foreach (var item in items)
            {
                var card = new CollageThumbnail(item.Id, this);
                card.SelectRequested += id => _doc.Select(id);
                card.ReplaceRequested += PickReplacement;
                card.RemoveRequested += id => _doc.Remove(id);
                card.DragStarted += OnPdfThumbnailDragStarted;
                card.DragMoved += OnPdfThumbnailDragMoved;
                card.DragEnded += OnPdfThumbnailDragEnded;
                _pdfCards.Add(card);
                PdfThumbHost.Children.Add(card);
            }
        }

        for (int i = 0; i < items.Count && i < _pdfCards.Count; i++)
            _pdfCards[i].Update(items[i], i + 1, _doc.SelectedId == items[i].Id);
    }

    private void OnPdfThumbnailDragStarted(long id)
    {
        if (_loading || _exporting || _pdfExporting || _closing) return;

        var item = _doc.Find(id);
        if (item == null) return;

        _pdfDragSource = id;
        var body = new Image
        {
            Source = item.Preview,
            Stretch = Stretch.Uniform,
            Width = 80,
            Height = 64,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(body, BitmapScalingMode.HighQuality);

        _pdfDragGhost = DragFx.CreateCard(body, 80, 64);
        _pdfDragGhost.Opacity = 0.92;
        DragLayer.Children.Add(_pdfDragGhost);
    }

    private void OnPdfThumbnailDragMoved(long id, Point screen)
    {
        if (_pdfDragSource == 0 || _pdfDragGhost == null) return;

        Point local = DragLayer.PointFromScreen(screen);
        Canvas.SetLeft(_pdfDragGhost, local.X - _pdfDragGhost.Width / 2);
        Canvas.SetTop(_pdfDragGhost, local.Y - _pdfDragGhost.Height / 2);

        long target = PdfThumbnailTargetAt(screen);
        foreach (var card in _pdfCards) card.IsTarget = target != 0 && card.ItemId == target;
    }

    private void OnPdfThumbnailDragEnded(long id, Point screen)
    {
        long source = _pdfDragSource;
        long target = source == 0 ? 0 : PdfThumbnailTargetAt(screen);
        EndPdfThumbnailDrag();

        if (source == 0 || target == 0) return;
        _doc.Swap(source, target);
    }

    private void CancelPdfThumbnailDrag()
    {
        if (_pdfDragSource != 0)
        {
            foreach (var card in _pdfCards)
            {
                if (card.ItemId == _pdfDragSource) card.CancelDrag();
            }
        }

        EndPdfThumbnailDrag();
    }

    private void EndPdfThumbnailDrag()
    {
        foreach (var card in _pdfCards) card.IsTarget = false;

        if (_pdfDragGhost != null)
        {
            DragLayer.Children.Remove(_pdfDragGhost);
            _pdfDragGhost = null;
        }

        _pdfDragSource = 0;
    }

    private long PdfThumbnailTargetAt(Point screen)
    {
        foreach (var card in _pdfCards)
        {
            if (card.ItemId == _pdfDragSource) continue;
            if (card.ContainsScreen(screen)) return card.ItemId;
        }

        return 0;
    }

    // ============ 页面条 / 工具条 ============

    private void SyncPdfPageBar()
    {
        if (_doc.OutputType != CollageOutputType.PdfDocument) return;

        int pageCount = CollagePdfGeometry.PageCount(_doc.Items.Count, _doc.LastValidPdfSettings);
        int index = PdfTemplateCanvas.PageIndex;
        PdfPageLabel.Text = pageCount > 0
            ? $"第 {index + 1} 页，共 {pageCount} 页"
            : "暂无页面";
        PdfPrevPageBtn.IsEnabled = pageCount > 0 && index > 0;
        PdfNextPageBtn.IsEnabled = pageCount > 0 && index < pageCount - 1;
    }

    private void SyncPdfSelectionToolbar(bool hasSelection, bool editing)
    {
        foreach (var button in new[]
                 {
                     PdfRotateCwBtn, PdfRotateCcwBtn, PdfFlipHBtn, PdfFlipVBtn, PdfResetBtn,
                     PdfReplaceBtn, PdfCardDeleteBtn, PdfTemplateRotateCwBtn,
                     PdfTemplateRotateCcwBtn, PdfTemplateFlipHBtn, PdfTemplateFlipVBtn,
                     PdfTemplateResetBtn, PdfTemplateReplaceBtn, PdfTemplateDeleteBtn,
                 })
        {
            button.IsEnabled = hasSelection && editing;
        }

        PdfPreviewBtn.IsEnabled = hasSelection && !_pdfExporting;
        PdfTemplatePreviewBtn.IsEnabled = hasSelection && !_pdfExporting;
    }

    /// <summary>PDF 浮动工具条定位（照上游：贴着选中框上边 12，夹在画布内 8，不压住底部工具条）。</summary>
    private void PositionPdfToolbars()
    {
        if (_closing) return;

        if (_doc.OutputType != CollageOutputType.PdfDocument)
        {
            PdfCardToolbar.Visibility = Visibility.Collapsed;
            PdfTemplateSelectionToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_doc.LastValidPdfSettings.IsTemplateBased)
        {
            PdfCardToolbar.Visibility = Visibility.Collapsed;
            var frame = PdfTemplateCanvas.SelectionFrame;
            if (_doc.SelectedId == 0 || frame.IsEmpty)
            {
                PdfTemplateSelectionToolbar.Visibility = Visibility.Collapsed;
                return;
            }

            var bounds = new Rect(PdfTemplateCanvas.RenderSize);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            PositionPdfToolbar(PdfTemplateSelectionToolbar, frame, bounds, PdfTemplateToolbar);
            return;
        }

        PdfTemplateSelectionToolbar.Visibility = Visibility.Collapsed;
        var card = PdfGrid.SelectedCardRect;
        if (_doc.SelectedId == 0 || card.IsEmpty)
        {
            PdfCardToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        var host = new Rect(PdfGrid.RenderSize);
        if (host.Width <= 0 || host.Height <= 0) return;
        PositionPdfToolbar(PdfCardToolbar, card, host, PdfGridToolbar);
    }

    private void PositionPdfToolbar(Border toolbar, Rect anchor, Rect bounds,
        FrameworkElement bottomBar)
    {
        toolbar.Visibility = Visibility.Visible;
        toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Max(toolbar.DesiredSize.Width, toolbar.ActualWidth);
        double height = Math.Max(toolbar.DesiredSize.Height, toolbar.ActualHeight);
        if (width <= 0 || height <= 0) return;

        double maximumX = Math.Max(8, bounds.Width - width - 8);
        double x = anchor.X + anchor.Width / 2 - width / 2;
        double y = anchor.Top - height - 12;
        if (y < 8) y = anchor.Bottom + 8;

        double maximumY = Math.Max(8, bounds.Height - height - 8);
        if (bottomBar.Visibility == Visibility.Visible && bottomBar.ActualHeight > 0)
            maximumY = Math.Max(8, Math.Min(maximumY,
                bounds.Height - bottomBar.ActualHeight - 24 - height));

        Canvas.SetLeft(toolbar, Math.Clamp(x, 8, maximumX));
        Canvas.SetTop(toolbar, Math.Clamp(y, 8, maximumY));
    }

    /// <summary>页脚尺寸（照上游 <c>refreshPdfDescription</c>：模板取第 0 页合成页，分页取当前图）。</summary>
    private string PdfDimensionsText()
    {
        var settings = _doc.LastValidPdfSettings;
        if (settings.IsTemplateBased)
        {
            var composed = CollagePdfGeometry.ComposedPage(CollagePdfRender.Snapshot(_doc.Items),
                settings, 0);
            return composed.IsValid
                ? CollagePdfGeometry.DescribeMillimeters(composed.PageMillimeters)
                : "";
        }

        var pixels = (Width: 0, Height: 0);
        if (settings.Paper == CollagePdfPaper.ImageSize && _doc.Selected is { } selected)
            pixels = selected.TransformedSize;
        else
            pixels = (1, 1);

        var layout = CollagePdfGeometry.PageLayout(pixels, settings);
        return layout.IsValid ? CollagePdfGeometry.DescribeMillimeters(layout.PageMillimeters) : "";
    }

    private bool PdfSettingsValid() => CollagePdfGeometry.Validate(_doc.PdfSettings).Count == 0;

    // ============ 导出 ============

    /// <summary>PDF 导出（照上游 <c>startPdfExport</c>）：先置导出态，再弹保存框 / 选文件夹。</summary>
    private void StartPdfExport()
    {
        if (_closing || _loading || _exporting || _pdfExporting || _confirming) return;

        if (_doc.IsEmpty)
        {
            SetStatus("没有可导出的图像", StatusKind.Error);
            return;
        }

        if (!PdfSettingsValid())
        {
            SetStatus("PDF 页面设置无效", StatusKind.Error);
            return;
        }

        var settings = _doc.PdfSettings;
        int pageCount = CollagePdfGeometry.PageCount(_doc.Items.Count, settings);
        if (pageCount <= 0)
        {
            SetStatus("PDF 设置或图像无效", StatusKind.Error);
            return;
        }

        _pdfExporting = true;
        _pdfChoosing = true;
        _pdfCancelRequested = false;
        _pdfPageNumber = 1;
        _pdfTotal = pageCount;
        _pdfPercent = 0;
        int generation = ++_pdfGeneration;
        UpdateChrome();

        string destination;
        if (settings.ExportMode == CollagePdfExportMode.Combined)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存 PDF 文档",
                Filter = "PDF 文档 (*.pdf)|*.pdf",
                FileName = CollagePdfExport.DefaultName,
                DefaultExt = ".pdf",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                FinishPdfExport(new CollagePdfExportResult
                {
                    TotalCount = pageCount,
                    Cancelled = true,
                });

                return;
            }

            destination = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择目标文件夹" };
            if (dialog.ShowDialog(this) != true)
            {
                FinishPdfExport(new CollagePdfExportResult
                {
                    TotalCount = pageCount,
                    Cancelled = true,
                });

                return;
            }

            destination = dialog.FolderName;
        }

        _pdfChoosing = false;
        SetStatus(PdfProgressText(), StatusKind.Progress);
        StartPdfProgressTimer();

        var items = new List<CollageItem>(_doc.Items);
        Task.Run(() => CollagePdfExport.Export(destination, items, settings,
                () => _pdfCancelRequested,
                progress =>
                {
                    _pdfTotal = Math.Max(1, progress.TotalPages);
                    _pdfPageNumber = Math.Clamp(progress.PageNumber, 1, _pdfTotal);
                    _pdfPercent = (int)Math.Round(Math.Clamp(progress.PageFraction, 0, 1) * 100);
                }))
            .ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new CollagePdfExportResult
                    {
                        TotalCount = pageCount,
                        Error = "无法导出 PDF，请重试",
                    };

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_closing || generation != _pdfGeneration || !_pdfExporting) return;
                    FinishPdfExport(result);
                }));
            });
    }

    private string PdfProgressText() =>
        $"正在导出 PDF 第 {Math.Max(1, _pdfPageNumber)} 页，共 {_pdfTotal} 页：{_pdfPercent}%";

    private void StartPdfProgressTimer()
    {
        if (_pdfProgressTimer == null)
        {
            _pdfProgressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            _pdfProgressTimer.Tick += (_, _) =>
            {
                if (!_pdfExporting || _pdfChoosing || _pdfCancelRequested) return;
                SetStatus(PdfProgressText(), StatusKind.Progress);
            };
        }

        _pdfProgressTimer.Start();
    }

    private void StopPdfProgressTimer() => _pdfProgressTimer?.Stop();

    private void FinishPdfExport(CollagePdfExportResult result)
    {
        if (_closing || !_pdfExporting) return;

        StopPdfProgressTimer();
        _pdfExporting = false;
        _pdfChoosing = false;
        _pdfCancelRequested = false;

        if (result.Succeeded)
        {
            FinishExportAndClose();
            return;
        }

        UpdateChrome();
        SetStatus(PdfResultText(result), result.Cancelled ? StatusKind.Progress : StatusKind.Error);
    }

    /// <summary>导出失败的页脚文案（照上游 <c>showStatus(PdfPartial)</c>）。</summary>
    private static string PdfResultText(CollagePdfExportResult result)
    {
        string reason = result.Cancelled
            ? "PDF 导出已取消。您的图像和编辑已保留"
            : string.IsNullOrWhiteSpace(result.Error)
                ? "无法导出 PDF，请重试"
                : result.Error;

        if (result.CompletedPages.Count == 0) return reason;

        string pages = string.Join(", ", result.CompletedPages);
        int firstRemaining = result.CompletedPages[result.CompletedPages.Count - 1] + 1;
        if (firstRemaining <= result.TotalCount)
        {
            reason += "\n" + (firstRemaining == result.TotalCount
                ? $"第 {firstRemaining} 页未保存"
                : $"第 {firstRemaining}–{result.TotalCount} 页未保存");
        }

        return $"已保存页面：{pages}\n文件：\n{string.Join("\n", result.Paths)}\n{reason}";
    }

    /// <summary>导出期间的「取消导出」（照上游 <c>requestPdfCancellation</c>）。</summary>
    private void RequestPdfCancellation()
    {
        if (!_pdfExporting || _pdfCancelRequested) return;

        _pdfCancelRequested = true;
        if (_pdfChoosing)
        {
            FinishPdfExport(new CollagePdfExportResult
            {
                TotalCount = _pdfTotal,
                Cancelled = true,
            });

            return;
        }

        SetStatus("正在取消 PDF 导出…", StatusKind.Progress);
        UpdateChrome();
    }
}
