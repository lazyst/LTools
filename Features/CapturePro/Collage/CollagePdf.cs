using System;
using System.Collections.Generic;
using System.Globalization;

namespace LTools.Features.CapturePro.Collage;

/// <summary>输出类型：静态图像 / PDF 文档（照上游 <c>ImageStitchingOutputType</c>）。</summary>
internal enum CollageOutputType
{
    StaticImage,
    PdfDocument,
}

/// <summary>PDF 拼接方式：分页拼接（一图一页）/ 模板拼接（一页多图）。</summary>
internal enum CollagePdfMethod
{
    PageBased,
    TemplateBased,
}

/// <summary>模板拼接的版式（照上游 <c>ImageStitchingPdfTemplateType</c>，顺序即下标）。</summary>
internal enum CollagePdfTemplate
{
    TwoImages,
    IdCard,
    ThreeImages,
    FourImages,
    FiveImages,
    SixImages,
    SevenImages,
    EightImages,
    NineImages,
}

/// <summary>纸张大小（照上游 <c>ImageStitchingPdfPaperSize</c>，顺序即下拉下标）。</summary>
internal enum CollagePdfPaper
{
    A3,
    A4,
    A5,
    B5,
    Letter,
    Legal,
    Custom,
    ImageSize,
}

internal enum CollagePdfOrientation
{
    Portrait,
    Landscape,
}

internal enum CollagePdfExportMode
{
    Combined,
    PerPage,
}

/// <summary>毫米尺寸（左上角为原点时的宽高）。</summary>
internal readonly record struct CollagePdfSize(double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

/// <summary>毫米矩形（左上角原点）。</summary>
internal readonly record struct CollagePdfRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && Width > 0 && Height > 0;

    public CollagePdfRect Translate(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>照上游 <c>QRectF::translated</c>（只在模板画布拖动时用）。</summary>
    public CollagePdfRect WithSize(double width, double height) => new(X, Y, width, height);
}

/// <summary>
/// 一张图在模板单元里的手摆（照上游 <c>ImageStitchingPdfPlacement</c>）：相对格中心的毫米偏移 +
/// 相对变换后原图像素的「毫米 / 像素」比例；比例为空 = 自动等比放进格子。
/// </summary>
internal readonly record struct CollagePdfPlacement(
    double CenterOffsetXMm,
    double CenterOffsetYMm,
    double? MillimetersPerPixel)
{
    public bool IsDefault => CenterOffsetXMm == 0 && CenterOffsetYMm == 0 && MillimetersPerPixel is null;

    public bool IsValid =>
        double.IsFinite(CenterOffsetXMm) && double.IsFinite(CenterOffsetYMm) &&
        (MillimetersPerPixel is not double scale || (double.IsFinite(scale) && scale > 0));
}

/// <summary>
/// PDF 导出的全部设置（照上游 <c>ImageStitchingPdfSettings</c>）。默认值与上游一致：
/// 分页拼接 / 两张图像模板 / 间距 5 / A4 纵向 / 自定义纸张 210×297 / 四边 10 / 合并导出 / 质量 100。
/// </summary>
/// <remarks>
/// 数值字段允许 <see cref="double.NaN"/> 表示「输入框里是空的 / 写坏了」——校验会给出对应字段的提示，
/// 而**只有校验通过的设置才写盘**（照上游 <c>writePdf</c>），所以磁盘上永远不会有 NaN。
/// 页边距在界面上是**一个**控件，写盘与读盘都按上游：四边永远等于同一个值。
/// </remarks>
internal sealed record CollagePdfSettings(
    CollagePdfMethod Method,
    CollagePdfTemplate Template,
    double ImageSpacingMm,
    CollagePdfPaper Paper,
    CollagePdfOrientation Orientation,
    double CustomWidthMm,
    double CustomHeightMm,
    double MarginTopMm,
    double MarginBottomMm,
    double MarginLeftMm,
    double MarginRightMm,
    CollagePdfExportMode ExportMode,
    int ImageQuality)
{
    public static CollagePdfSettings Default => new(CollagePdfMethod.PageBased,
        CollagePdfTemplate.TwoImages, 5, CollagePdfPaper.A4, CollagePdfOrientation.Portrait,
        210, 297, 10, 10, 10, 10, CollagePdfExportMode.Combined, 100);

    /// <summary>页边距四边取同一个值（界面上只有一个「页边距」输入框）。</summary>
    public double MarginMm => MarginTopMm;

    public CollagePdfSettings WithMargin(double margin) =>
        this with
        {
            MarginTopMm = margin,
            MarginBottomMm = margin,
            MarginLeftMm = margin,
            MarginRightMm = margin,
        };

    public bool IsTemplateBased => Method == CollagePdfMethod.TemplateBased;

    /// <summary>纸张为「图像大小」且是分页拼接：此时没有纸张方向 / 页边距 / 间距（照上游）。</summary>
    public bool IsImageSizePageBased => Paper == CollagePdfPaper.ImageSize && !IsTemplateBased;

    public string MethodKey => Method == CollagePdfMethod.TemplateBased ? "template_based" : "page_based";

    public string TemplateKey => Template switch
    {
        CollagePdfTemplate.IdCard => "id_card",
        CollagePdfTemplate.ThreeImages => "three_images",
        CollagePdfTemplate.FourImages => "four_images",
        CollagePdfTemplate.FiveImages => "five_images",
        CollagePdfTemplate.SixImages => "six_images",
        CollagePdfTemplate.SevenImages => "seven_images",
        CollagePdfTemplate.EightImages => "eight_images",
        CollagePdfTemplate.NineImages => "nine_images",
        _ => "two_images",
    };

    public string PaperKey => Paper switch
    {
        CollagePdfPaper.A3 => "a3",
        CollagePdfPaper.A5 => "a5",
        CollagePdfPaper.B5 => "b5",
        CollagePdfPaper.Letter => "letter",
        CollagePdfPaper.Legal => "legal",
        CollagePdfPaper.Custom => "custom",
        CollagePdfPaper.ImageSize => "image_size",
        _ => "a4",
    };

    public string OrientationKey =>
        Orientation == CollagePdfOrientation.Landscape ? "landscape" : "portrait";

    public string ExportModeKey => ExportMode == CollagePdfExportMode.PerPage ? "per_page" : "combined";

    public static CollagePdfMethod ParseMethod(string? text) =>
        string.Equals(text, "template_based", StringComparison.OrdinalIgnoreCase)
            ? CollagePdfMethod.TemplateBased
            : CollagePdfMethod.PageBased;

    public static CollagePdfTemplate ParseTemplate(string? text) => text switch
    {
        "id_card" => CollagePdfTemplate.IdCard,
        "three_images" => CollagePdfTemplate.ThreeImages,
        "four_images" => CollagePdfTemplate.FourImages,
        "five_images" => CollagePdfTemplate.FiveImages,
        "six_images" => CollagePdfTemplate.SixImages,
        "seven_images" => CollagePdfTemplate.SevenImages,
        "eight_images" => CollagePdfTemplate.EightImages,
        "nine_images" => CollagePdfTemplate.NineImages,
        _ => CollagePdfTemplate.TwoImages,
    };

    public static CollagePdfPaper ParsePaper(string? text) => text switch
    {
        "a3" => CollagePdfPaper.A3,
        "a5" => CollagePdfPaper.A5,
        "b5" => CollagePdfPaper.B5,
        "letter" => CollagePdfPaper.Letter,
        "legal" => CollagePdfPaper.Legal,
        "custom" => CollagePdfPaper.Custom,
        "image_size" => CollagePdfPaper.ImageSize,
        _ => CollagePdfPaper.A4,
    };

    public static CollagePdfOrientation ParseOrientation(string? text) =>
        string.Equals(text, "landscape", StringComparison.OrdinalIgnoreCase)
            ? CollagePdfOrientation.Landscape
            : CollagePdfOrientation.Portrait;

    public static CollagePdfExportMode ParseExportMode(string? text) =>
        string.Equals(text, "per_page", StringComparison.OrdinalIgnoreCase)
            ? CollagePdfExportMode.PerPage
            : CollagePdfExportMode.Combined;
}

/// <summary>模板按钮 / 模板选择器需要的容量（照上游 <c>imageStitchingPdfTemplateCapacity</c>）。</summary>
internal static class CollagePdfTemplates
{
    public static int Capacity(CollagePdfTemplate template) => template switch
    {
        CollagePdfTemplate.TwoImages => 2,
        CollagePdfTemplate.IdCard => 2,
        CollagePdfTemplate.ThreeImages => 3,
        CollagePdfTemplate.FourImages => 4,
        CollagePdfTemplate.FiveImages => 5,
        CollagePdfTemplate.SixImages => 6,
        CollagePdfTemplate.SevenImages => 7,
        CollagePdfTemplate.EightImages => 8,
        CollagePdfTemplate.NineImages => 9,
        _ => 0,
    };

    public static IReadOnlyList<CollagePdfTemplate> All { get; } = new[]
    {
        CollagePdfTemplate.TwoImages,
        CollagePdfTemplate.IdCard,
        CollagePdfTemplate.ThreeImages,
        CollagePdfTemplate.FourImages,
        CollagePdfTemplate.FiveImages,
        CollagePdfTemplate.SixImages,
        CollagePdfTemplate.SevenImages,
        CollagePdfTemplate.EightImages,
        CollagePdfTemplate.NineImages,
    };

    /// <summary>模板在界面上叫什么（照上游 <c>ImageStitchingPdfSettingsWidget</c> 的中文文案）。</summary>
    public static string LabelOf(CollagePdfTemplate template) => template switch
    {
        CollagePdfTemplate.IdCard => "身份证",
        CollagePdfTemplate.ThreeImages => "三张图像",
        CollagePdfTemplate.FourImages => "四张图像",
        CollagePdfTemplate.FiveImages => "五张图像",
        CollagePdfTemplate.SixImages => "六张图像",
        CollagePdfTemplate.SevenImages => "七张图像",
        CollagePdfTemplate.EightImages => "八张图像",
        CollagePdfTemplate.NineImages => "九张图像",
        _ => "两张图像",
    };

    /// <summary>模板预览里的格子（0–1 的相对坐标，照上游 <c>TemplateButton::paintEvent</c>）。</summary>
    public static IReadOnlyList<(double X, double Y, double W, double H)> PreviewCells(
        CollagePdfTemplate template)
    {
        var cells = new List<(double, double, double, double)>(Capacity(template));
        if (template == CollagePdfTemplate.IdCard) return cells;

        const double gap = 0.065;
        void Column(int index, int columns, int rows)
        {
            double width = (1 - gap * (columns - 1)) / columns;
            double height = (1 - gap * (rows - 1)) / rows;
            for (int row = 0; row < rows; row++)
                cells.Add((index * (width + gap), row * (height + gap), width, height));
        }

        switch (template)
        {
            case CollagePdfTemplate.TwoImages:
                Column(0, 1, 2);
                break;
            case CollagePdfTemplate.ThreeImages:
                Column(0, 1, 3);
                break;
            case CollagePdfTemplate.FourImages:
                Column(0, 2, 2);
                Column(1, 2, 2);
                break;
            case CollagePdfTemplate.FiveImages:
                Column(0, 2, 2);
                Column(1, 2, 3);
                break;
            case CollagePdfTemplate.SixImages:
                Column(0, 2, 3);
                Column(1, 2, 3);
                break;
            case CollagePdfTemplate.SevenImages:
                Column(0, 2, 3);
                Column(1, 2, 4);
                break;
            case CollagePdfTemplate.EightImages:
                Column(0, 2, 4);
                Column(1, 2, 4);
                break;
            case CollagePdfTemplate.NineImages:
                Column(0, 3, 3);
                Column(1, 3, 3);
                Column(2, 3, 3);
                break;
        }

        return cells;
    }
}

/// <summary>一页 PDF 的单图布局（照上游 <c>ImageStitchingPdfPageLayout</c>）。</summary>
internal sealed record CollagePdfPageLayout(
    CollagePdfSize PageMillimeters,
    CollagePdfRect ContentMillimeters,
    CollagePdfRect ImageMillimeters,
    string Error,
    IReadOnlyDictionary<string, string> FieldErrors)
{
    public bool IsValid =>
        Error.Length == 0 && FieldErrors.Count == 0 && PageMillimeters.IsValid &&
        ContentMillimeters.IsValid && ImageMillimeters.IsValid;
}

/// <summary>合成页里的一格（照上游 <c>ImageStitchingPdfCell</c>；<see cref="ItemIndex"/> 为 -1 = 空格）。</summary>
internal readonly record struct CollagePdfCell(
    long ItemId,
    int ItemIndex,
    CollagePdfRect ClipMillimeters,
    CollagePdfRect ImageMillimeters);

/// <summary>一页合成结果（照上游 <c>ImageStitchingPdfComposedPage</c>）。</summary>
internal sealed record CollagePdfComposedPage(
    CollagePdfSize PageMillimeters,
    CollagePdfRect ContentMillimeters,
    IReadOnlyList<CollagePdfCell> Cells,
    string Error)
{
    public bool IsValid =>
        Error.Length == 0 && PageMillimeters.IsValid && ContentMillimeters.IsValid &&
        Cells.Count > 0;
}

/// <summary>计算合成页时对一张图的最小描述（避免把位图带进纯逻辑）。</summary>
internal readonly record struct CollagePdfItem(
    long Id,
    int Index,
    int PixelWidth,
    int PixelHeight,
    CollagePdfPlacement Placement);

/// <summary>
/// PDF 页面的毫米几何（照上游 <c>collagedocument.cpp</c> 的 PDF 一节）：纸张 / 内容区 /
/// 模板单元 / 校验 / 单图页面布局 / 合成页。**全部是纯函数**，预览、导出与自测共用同一份。
/// </summary>
internal static class CollagePdfGeometry
{
    /// <summary>PDF 1.7 允许的物理纸张上限（毫米）。</summary>
    public const double MaximumPdfDimensionMm = 381_000_000;

    /// <summary>96 DPI → 毫米（照上游「图像大小」纸张的换算）。</summary>
    public const double MillimetersPerPixel = 25.4 / 96.0;

    public const double PointsPerMillimeter = 72.0 / 25.4;

    /// <summary>纸张毫米尺寸（纵向；横向时转置）。图像大小 + 分页拼接没有固定纸张，返回空。</summary>
    public static CollagePdfSize PaperMillimeters(CollagePdfSettings settings)
    {
        CollagePdfSize page;
        switch (settings.Paper)
        {
            case CollagePdfPaper.A3:
                page = new CollagePdfSize(297, 420);
                break;
            case CollagePdfPaper.A4:
                page = new CollagePdfSize(210, 297);
                break;
            case CollagePdfPaper.A5:
                page = new CollagePdfSize(148, 210);
                break;
            case CollagePdfPaper.B5:
                page = new CollagePdfSize(176, 250);
                break;
            case CollagePdfPaper.Letter:
                page = new CollagePdfSize(215.9, 279.4);
                break;
            case CollagePdfPaper.Legal:
                page = new CollagePdfSize(215.9, 355.6);
                break;
            case CollagePdfPaper.Custom:
                page = new CollagePdfSize(settings.CustomWidthMm, settings.CustomHeightMm);
                break;
            case CollagePdfPaper.ImageSize:
                if (!settings.IsTemplateBased) return default;
                page = new CollagePdfSize(210, 297);
                break;
            default:
                return default;
        }

        if (settings.Orientation == CollagePdfOrientation.Landscape)
            page = new CollagePdfSize(page.Height, page.Width);
        return page;
    }

    /// <summary>内容区（纸张减去四边页边距）。</summary>
    public static CollagePdfRect ContentMillimeters(CollagePdfSettings settings)
    {
        var page = PaperMillimeters(settings);
        return new CollagePdfRect(settings.MarginLeftMm, settings.MarginTopMm,
            page.Width - settings.MarginLeftMm - settings.MarginRightMm,
            page.Height - settings.MarginTopMm - settings.MarginBottomMm);
    }

    /// <summary>模板拼接的格子（毫米，左上角原点；身份证模板已收成卡面比例）。</summary>
    public static IReadOnlyList<CollagePdfRect> TemplateCells(CollagePdfSettings settings)
    {
        int[] rows;
        switch (settings.Template)
        {
            case CollagePdfTemplate.TwoImages:
            case CollagePdfTemplate.IdCard:
                rows = new[] { 2 };
                break;
            case CollagePdfTemplate.ThreeImages:
                rows = new[] { 3 };
                break;
            case CollagePdfTemplate.FourImages:
                rows = new[] { 2, 2 };
                break;
            case CollagePdfTemplate.FiveImages:
                rows = new[] { 2, 3 };
                break;
            case CollagePdfTemplate.SixImages:
                rows = new[] { 3, 3 };
                break;
            case CollagePdfTemplate.SevenImages:
                rows = new[] { 3, 4 };
                break;
            case CollagePdfTemplate.EightImages:
                rows = new[] { 4, 4 };
                break;
            case CollagePdfTemplate.NineImages:
                rows = new[] { 3, 3, 3 };
                break;
            default:
                return Array.Empty<CollagePdfRect>();
        }

        var content = ContentMillimeters(settings);
        bool landscape = settings.Orientation == CollagePdfOrientation.Landscape;
        var portrait = landscape
            ? new CollagePdfSize(content.Height, content.Width)
            : new CollagePdfSize(content.Width, content.Height);
        double gap = settings.ImageSpacingMm;
        double columns = rows.Length;
        double width = (portrait.Width - (columns - 1) * gap) / columns;

        int maximumRows = 0;
        foreach (int count in rows) maximumRows = Math.Max(maximumRows, count);

        var result = new List<CollagePdfRect>(CollagePdfTemplates.Capacity(settings.Template));
        for (int row = 0; row < maximumRows; row++)
        {
            for (int column = 0; column < rows.Length; column++)
            {
                if (row >= rows[column]) continue;

                double height = (portrait.Height - (rows[column] - 1) * gap) / rows[column];
                var cell = new CollagePdfRect(column * (width + gap), row * (height + gap), width,
                    height);
                if (landscape)
                    cell = new CollagePdfRect(cell.Y, cell.X, cell.Height, cell.Width);
                cell = cell.Translate(content.X, content.Y);

                if (settings.Template == CollagePdfTemplate.IdCard)
                {
                    const double cardRatio = 85.60 / 53.98;
                    var paper = PaperMillimeters(settings);
                    double cardWidth = Math.Min(Math.Min(0.41 * Math.Min(paper.Width, paper.Height),
                        cell.Width), cell.Height * cardRatio);
                    double cardHeight = cardWidth / cardRatio;
                    cell = new CollagePdfRect(cell.CenterX - cardWidth / 2,
                        cell.CenterY - cardHeight / 2, cardWidth, cardHeight);
                }

                result.Add(cell);
            }
        }

        // 横向时格子按列落位，除了 5 / 7 张（错落版式）都要按「上 → 左」重排成阅读顺序。
        if (landscape && settings.Template is not (CollagePdfTemplate.FiveImages or
                CollagePdfTemplate.SevenImages))
        {
            result.Sort(static (first, second) =>
            {
                int byTop = first.Y.CompareTo(second.Y);
                return byTop != 0 ? byTop : first.X.CompareTo(second.X);
            });
        }

        return result;
    }

    /// <summary>一页能放几张（模板拼接）。</summary>
    public static int Capacity(CollagePdfSettings settings) =>
        CollagePdfTemplates.Capacity(settings.Template);

    /// <summary>总页数（照上游 <c>imageStitchingPdfPageCount</c>）。</summary>
    public static int PageCount(int itemCount, CollagePdfSettings settings)
    {
        if (itemCount <= 0) return 0;
        if (settings.Method == CollagePdfMethod.PageBased) return itemCount;
        if (settings.Method != CollagePdfMethod.TemplateBased) return 0;

        int capacity = Capacity(settings);
        return capacity > 0 ? itemCount / capacity + (itemCount % capacity != 0 ? 1 : 0) : 0;
    }

    /// <summary>
    /// 逐字段校验（键与上游 <c>ImageStitchingSettingsStore</c> 的 PDF 字段同名）。值为中文提示，
    /// 与上游 <c>validateImageStitchingPdfSettings</c> 的判定顺序 / 条件一致。
    /// </summary>
    public static Dictionary<string, string> Validate(CollagePdfSettings settings)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        void Positive(string key, double value)
        {
            if (!double.IsFinite(value) || value <= 0)
                errors[key] = "请输入正数的纸张尺寸";
            else if (value > MaximumPdfDimensionMm)
                errors[key] = "纸张尺寸超过了 PDF 大小限制";
        }

        void Nonnegative(string key, double value)
        {
            if (!double.IsFinite(value) || value < 0) errors[key] = "请输入非负的页边距";
        }

        if (settings.Method is not (CollagePdfMethod.PageBased or CollagePdfMethod.TemplateBased))
            errors["stitching_method"] = "请选择拼接方式";
        if (CollagePdfTemplates.Capacity(settings.Template) == 0)
            errors["template_type"] = "请选择模板";

        bool templateBased = settings.IsTemplateBased;
        if (templateBased && (!double.IsFinite(settings.ImageSpacingMm) || settings.ImageSpacingMm < 0))
            errors["image_spacing_mm"] = "请输入非负的图像间距";

        if (settings.Paper < CollagePdfPaper.A3 || settings.Paper > CollagePdfPaper.ImageSize)
            errors["paper_size"] = "请选择纸张大小";

        if ((settings.Paper != CollagePdfPaper.ImageSize || templateBased) &&
            settings.Orientation is not (CollagePdfOrientation.Portrait or
                CollagePdfOrientation.Landscape))
            errors["orientation"] = "请选择纸张方向";

        if (settings.ExportMode is not (CollagePdfExportMode.Combined or CollagePdfExportMode.PerPage))
            errors["export_mode"] = "请选择导出模式";

        if (settings.ImageQuality < 0 || settings.ImageQuality > 100)
            errors["image_quality"] = "请输入 0% 至 100% 之间的图像质量";

        if (settings.Paper == CollagePdfPaper.ImageSize && !templateBased) return errors;

        if (settings.Paper == CollagePdfPaper.Custom)
        {
            Positive("custom_width_mm", settings.CustomWidthMm);
            Positive("custom_height_mm", settings.CustomHeightMm);
        }

        Nonnegative("margin_top_mm", settings.MarginTopMm);
        Nonnegative("margin_bottom_mm", settings.MarginBottomMm);
        Nonnegative("margin_left_mm", settings.MarginLeftMm);
        Nonnegative("margin_right_mm", settings.MarginRightMm);

        var page = PaperMillimeters(settings);
        void Usable(double dimension, double before, double after, string beforeKey, string afterKey)
        {
            if (!double.IsFinite(dimension) || dimension <= 0 || errors.ContainsKey(beforeKey) ||
                errors.ContainsKey(afterKey))
                return;
            double remaining = dimension - before - after;
            if (double.IsFinite(remaining) && remaining > 0) return;

            const string message = "页边距必须留出可用的内容区域";
            errors[beforeKey] = message;
            errors[afterKey] = message;
        }

        Usable(page.Width, settings.MarginLeftMm, settings.MarginRightMm, "margin_left_mm",
            "margin_right_mm");
        Usable(page.Height, settings.MarginTopMm, settings.MarginBottomMm, "margin_top_mm",
            "margin_bottom_mm");

        if (templateBased && errors.Count == 0)
        {
            var cells = TemplateCells(settings);
            bool invalid = cells.Count == 0;
            foreach (var cell in cells)
            {
                if (!cell.IsValid) invalid = true;
            }

            if (invalid) errors["image_spacing_mm"] = "图像间距必须为模板区域留出可用空间";
        }

        return errors;
    }

    /// <summary>
    /// 分页拼接的一页：图像按内容区等比缩放并居中（照上游
    /// <c>calculateImageStitchingPdfPageLayout</c>）。「图像大小」纸张 = 按 96 DPI 换算的像素尺寸。
    /// </summary>
    public static CollagePdfPageLayout PageLayout((int Width, int Height) transformedPixels,
        CollagePdfSettings settings)
    {
        var errors = Validate(settings);
        if (errors.Count > 0)
            return new CollagePdfPageLayout(default, default, default, "PDF 页面设置无效", errors);

        if (transformedPixels.Width <= 0 || transformedPixels.Height <= 0)
            return new CollagePdfPageLayout(default, default, default, "无法准备图像以供导出为 PDF",
                errors);

        if (settings.Paper == CollagePdfPaper.ImageSize &&
            settings.Method == CollagePdfMethod.PageBased)
        {
            var pageSize = new CollagePdfSize(transformedPixels.Width * MillimetersPerPixel,
                transformedPixels.Height * MillimetersPerPixel);
            if (pageSize.Width > MaximumPdfDimensionMm || pageSize.Height > MaximumPdfDimensionMm)
                return new CollagePdfPageLayout(default, default, default,
                    "纸张尺寸超过了 PDF 大小限制", errors);

            var full = new CollagePdfRect(0, 0, pageSize.Width, pageSize.Height);
            return new CollagePdfPageLayout(pageSize, full, full, "", errors);
        }

        var paper = PaperMillimeters(settings);
        var content = new CollagePdfRect(settings.MarginLeftMm, settings.MarginTopMm,
            paper.Width - settings.MarginLeftMm - settings.MarginRightMm,
            paper.Height - settings.MarginTopMm - settings.MarginBottomMm);
        double scale = Math.Min(content.Width / transformedPixels.Width,
            content.Height / transformedPixels.Height);
        double fittedWidth = transformedPixels.Width * scale;
        double fittedHeight = transformedPixels.Height * scale;
        var image = new CollagePdfRect(content.X + (content.Width - fittedWidth) / 2,
            content.Y + (content.Height - fittedHeight) / 2, fittedWidth, fittedHeight);

        string error = image.IsValid && double.IsFinite(fittedWidth) && double.IsFinite(fittedHeight)
            ? ""
            : "PDF 页面设置无效";
        return new CollagePdfPageLayout(paper, content, image, error, errors);
    }

    /// <summary>
    /// 合成第 <paramref name="pageIndex"/> 页（照上游 <c>calculateImageStitchingPdfComposedPage</c>）：
    /// 分页拼接 = 单图页面布局；模板拼接 = 按格子摆图（每图的手摆偏移 / 比例）。
    /// </summary>
    public static CollagePdfComposedPage ComposedPage(IReadOnlyList<CollagePdfItem> items,
        CollagePdfSettings settings, int pageIndex)
    {
        CollagePdfComposedPage Invalid() =>
            new(default, default, Array.Empty<CollagePdfCell>(), "PDF 页面设置无效");

        if (pageIndex < 0 || pageIndex >= PageCount(items.Count, settings) ||
            Validate(settings).Count > 0)
            return Invalid();

        if (settings.Method == CollagePdfMethod.PageBased)
        {
            var item = items[pageIndex];
            var layout = PageLayout((item.PixelWidth, item.PixelHeight), settings);
            if (!layout.IsValid) return Invalid();

            return new CollagePdfComposedPage(layout.PageMillimeters, layout.ContentMillimeters,
                new[]
                {
                    new CollagePdfCell(item.Id, pageIndex, layout.ContentMillimeters,
                        layout.ImageMillimeters),
                }, "");
        }

        var page = PaperMillimeters(settings);
        var content = ContentMillimeters(settings);
        var cells = TemplateCells(settings);
        int first = pageIndex * cells.Count;
        var result = new List<CollagePdfCell>(cells.Count);

        for (int index = 0; index < cells.Count; index++)
        {
            var clip = cells[index];
            int itemIndex = first + index;
            if (itemIndex >= items.Count)
            {
                result.Add(new CollagePdfCell(0, -1, clip, default));
                continue;
            }

            var item = items[itemIndex];
            if (item.PixelWidth <= 0 || item.PixelHeight <= 0 || !item.Placement.IsValid)
                return Invalid();

            double scale = item.Placement.MillimetersPerPixel ??
                           Math.Min(clip.Width / item.PixelWidth, clip.Height / item.PixelHeight);
            double width = item.PixelWidth * scale;
            double height = item.PixelHeight * scale;
            double centerX = clip.CenterX + item.Placement.CenterOffsetXMm;
            double centerY = clip.CenterY + item.Placement.CenterOffsetYMm;
            var image = new CollagePdfRect(centerX - width / 2, centerY - height / 2, width, height);
            if (!image.IsValid) return Invalid();

            result.Add(new CollagePdfCell(item.Id, itemIndex, clip, image));
        }

        return new CollagePdfComposedPage(page, content, result, "");
    }

    /// <summary>给人看的毫米尺寸（照上游「%1 × %2 毫米」，最多 6 位有效数字）。</summary>
    public static string DescribeMillimeters(CollagePdfSize size) =>
        size.IsValid
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#####} × {1:0.#####} 毫米", size.Width,
                size.Height)
            : "";

    /// <summary>像素尺寸（页脚沿用现有的「W × H px」）。</summary>
    public static string DescribePixels(int width, int height) => $"{width} × {height} 像素";
}
