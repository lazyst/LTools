namespace LTools.Config;

/// <summary>
/// 拼图的默认设置（<c>configs/settings.json</c> 的 <c>Collage</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>collagesettingsstore</c>：键名与取值形状一一对应，方向 / 尺寸方式存字符串
/// （<c>vertical</c> / <c>horizontal</c> / <c>custom</c> 与 <c>uniform</c> / <c>original</c>），
/// 外边距 / 间距**一律存输出像素**（界面上可以按百分比输入，那只是编辑方式，见
/// <c>CollageWindow</c> 的长度输入），背景色是 <c>#RRGGBBAA</c>。
/// </remarks>
public sealed class CollageFile
{
    /// <summary>拼接方向：<c>vertical</c>（纵向）/ <c>horizontal</c>（横向）/ <c>custom</c>（宫格）。</summary>
    public string Direction { get; set; } = "vertical";

    /// <summary>尺寸方式：<c>uniform</c>（统一尺寸）/ <c>original</c>（原始尺寸）。</summary>
    public string Sizing { get; set; } = "uniform";

    /// <summary>外边距（输出像素）。</summary>
    public int BorderPx { get; set; }

    /// <summary>图片间距（输出像素）。</summary>
    public int SpacingPx { get; set; }

    /// <summary>背景色，<c>#RRGGBBAA</c>。</summary>
    public string Background { get; set; } = "#FFFFFFFF";

    /// <summary>宫格行数。</summary>
    public int Rows { get; set; } = 1;

    /// <summary>宫格列数。</summary>
    public int Columns { get; set; } = 1;

    /// <summary>宫格行列是否已按图片数量自动定过一次（照上游 <c>custom_grid_initialized</c>）。</summary>
    public bool CustomGridInitialized { get; set; }

    // ============ PDF 导出（照上游 collage/pdf_*） ============

    /// <summary>PDF 拼接方式：<c>page_based</c>（分页拼接）/ <c>template_based</c>（模板拼接）。</summary>
    public string PdfStitchingMethod { get; set; } = "page_based";

    /// <summary>模板：<c>two_images</c> / <c>id_card</c> / <c>three_images</c>…<c>nine_images</c>。</summary>
    public string PdfTemplateType { get; set; } = "two_images";

    /// <summary>模板拼接的图像间距（毫米）。</summary>
    public double PdfImageSpacingMm { get; set; } = 5;

    /// <summary>纸张：<c>a3</c> / <c>a4</c> / <c>a5</c> / <c>b5</c> / <c>letter</c> / <c>legal</c> / <c>custom</c> / <c>image_size</c>。</summary>
    public string PdfPaperSize { get; set; } = "a4";

    /// <summary>纸张方向：<c>portrait</c> / <c>landscape</c>。</summary>
    public string PdfOrientation { get; set; } = "portrait";

    /// <summary>自定义纸张宽度（毫米）。</summary>
    public double PdfCustomWidthMm { get; set; } = 210;

    /// <summary>自定义纸张高度（毫米）。</summary>
    public double PdfCustomHeightMm { get; set; } = 297;

    /// <summary>页边距（毫米，四边同一个值；读盘时也按上游只用这一个）。</summary>
    public double PdfMarginTopMm { get; set; } = 10;

    /// <summary>页边距（下，跟随 <see cref="PdfMarginTopMm"/>）。</summary>
    public double PdfMarginBottomMm { get; set; } = 10;

    /// <summary>页边距（左，跟随 <see cref="PdfMarginTopMm"/>）。</summary>
    public double PdfMarginLeftMm { get; set; } = 10;

    /// <summary>页边距（右，跟随 <see cref="PdfMarginTopMm"/>）。</summary>
    public double PdfMarginRightMm { get; set; } = 10;

    /// <summary>导出模式：<c>combined</c>（合并导出）/ <c>per_page</c>（逐页导出）。</summary>
    public string PdfExportMode { get; set; } = "combined";

    /// <summary>图像质量（0–100，100 = 无损）。</summary>
    public int PdfImageQuality { get; set; } = 100;
}
