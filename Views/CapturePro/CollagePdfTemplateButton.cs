using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// PDF 模板按钮（照上游 <c>ImageStitchingPdfSettingsWidget</c> 里的 <c>TemplateButton</c>）：
/// 一张 22×30（横向 30×22）的小纸面，按模板把格子填成实心色；选中 = 主色 + 浅主色底，
/// 未选中 = 次文字色 + 透明底，悬停 / 按下 / 聚焦各有自己的底色。
/// </summary>
/// <remarks>
/// 纸面 / 格子的几何与上游同一套（纸面内缩 3、格子间隙 0.065、身份证按 85.6 / 53.98 的卡面比例），
/// 但**格子的拓扑**直接取 <see cref="CollagePdfTemplates.PreviewCells"/>——那是模板预览的唯一来源，
/// 与导出用的 <see cref="CollagePdfGeometry.TemplateCells"/> 各管一边（一边是图标，一边是毫米）。
/// </remarks>
internal sealed class CollagePdfTemplateButton : Button
{
    private const double PaperWidth = 22;
    private const double PaperHeight = 30;
    private const double ContentInset = 3;
    private const double CardRatio = 85.6 / 53.98;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(CollagePdfTemplateButton),
        new FrameworkPropertyMetadata("two_images",
            FrameworkPropertyMetadataOptions.AffectsRender, OnKindChanged));

    public static readonly DependencyProperty LandscapeProperty = DependencyProperty.Register(
        nameof(Landscape), typeof(bool), typeof(CollagePdfTemplateButton),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(bool), typeof(CollagePdfTemplateButton),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender));

    private CollagePdfTemplate _template = CollagePdfTemplate.TwoImages;

    public CollagePdfTemplateButton()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MinWidth = 42;
        MinHeight = 48;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;

        // 按钮自己的模板只留一块透明的命中区：纸面全部由 OnRender 画。
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(BackgroundProperty, Brushes.Transparent);
        template.VisualTree = border;
        Template = template;
    }

    /// <summary>模板键（<c>two_images</c> / <c>id_card</c> / <c>three_images</c> …）。</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>纸张横向时纸面与格子转 90°（照上游 <c>m_landscape</c>）。</summary>
    public bool Landscape
    {
        get => (bool)GetValue(LandscapeProperty);
        set => SetValue(LandscapeProperty, value);
    }

    public bool Selected
    {
        get => (bool)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public CollagePdfTemplate TemplateKind => _template;

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CollagePdfTemplateButton button)
            button._template = CollagePdfSettings.ParseTemplate(e.NewValue as string);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        var primary = Brush("CapturePrimary", Color.FromRgb(0x16, 0x77, 0xFF));
        var secondary = Brush("CaptureTextSecondary", Color.FromRgb(0x71, 0x71, 0x7A));
        var borderColor = Selected ? primary :
            IsMouseOver ? primary : Brush("CaptureBorder", Color.FromRgb(0xD9, 0xD9, 0xD9));
        Brush background = Selected ? Brush("CapturePrimaryBg", Color.FromRgb(0xE6, 0xF4, 0xFF)) :
            IsPressed ? Brush("CaptureActive", Color.FromRgb(0xE6, 0xF4, 0xFF)) :
            IsMouseOver ? Brush("CaptureHover", Color.FromRgb(0xF0, 0xF2, 0xF3)) : Brushes.Transparent;

        double opacity = IsEnabled ? 1 : 0.4;
        dc.PushOpacity(opacity);
        var frame = Rect.Inflate(bounds, -0.5, -0.5);
        dc.DrawRoundedRectangle(background, new Pen(borderColor, 1), frame, 4, 4);

        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(primary, 1), Rect.Inflate(frame, -2, -2), 3, 3);

        bool landscape = Landscape;
        var paperSize = landscape
            ? new Size(PaperHeight, PaperWidth)
            : new Size(PaperWidth, PaperHeight);

        // 纸面在小按钮里居中；按钮比最小尺寸高时也只画这么大（照上游固定 22×30）。
        var paper = new Rect(bounds.X + (bounds.Width - paperSize.Width) / 2,
            bounds.Y + (bounds.Height - paperSize.Height) / 2, paperSize.Width, paperSize.Height);
        var paperPen = new Pen(Selected ? primary : secondary, 1);
        dc.DrawRoundedRectangle(null, paperPen, Rect.Inflate(paper, -0.5, -0.5), 1, 1);

        var content = Rect.Inflate(paper, -ContentInset, -ContentInset);
        if (content.Width <= 0 || content.Height <= 0)
        {
            dc.Pop();
            return;
        }

        var fill = Selected ? primary : secondary;

        if (_template == CollagePdfTemplate.IdCard)
        {
            for (int index = 0; index < 2; index++)
            {
                var section = landscape
                    ? new Rect(content.X + index * content.Width / 2, content.Y, content.Width / 2,
                        content.Height)
                    : new Rect(content.X, content.Y + index * content.Height / 2, content.Width,
                        content.Height / 2);
                double width = Math.Min(Math.Min(0.41 * Math.Min(paper.Width, paper.Height),
                        section.Width), section.Height * CardRatio);
                double height = width / CardRatio;
                if (width <= 0 || height <= 0) continue;
                dc.DrawRectangle(fill, null,
                    new Rect(section.X + (section.Width - width) / 2,
                        section.Y + (section.Height - height) / 2, width, height));
            }

            dc.Pop();
            return;
        }

        foreach (var cell in CollagePdfTemplates.PreviewCells(_template))
        {
            double x = cell.X;
            double y = cell.Y;
            double width = cell.W;
            double height = cell.H;
            if (landscape) (x, y, width, height) = (y, x, height, width);

            var rect = new Rect(content.X + x * content.Width, content.Y + y * content.Height,
                width * content.Width, height * content.Height);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            dc.DrawRectangle(fill, null, rect);
        }

        dc.Pop();
    }

    private Brush Brush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? ThemeBrush.Frozen(fallback);

}
