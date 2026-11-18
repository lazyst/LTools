using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼图设置的读写：<c>configs/settings.json</c> 的 <c>Collage</c> 段（照上游
/// <c>collagesettingsstore</c> 的键名）。
/// </summary>
/// <remarks>
/// 和 <see cref="TranslationStore"/> 同一套办法：不缓存、不防抖，只在开窗 / 关窗和改设置时
/// 读一次、写一次，永远只写这一个域。**就地改字段**，别整体替换 <see cref="CollageFile"/>
/// （以后加的字段会被抹掉）。
/// </remarks>
internal static class CollageStore
{
    /// <summary>当前设置（没配过的字段按默认值补齐）。</summary>
    public static CollageSettings Load()
    {
        CollageFile file = Read();
        return new CollageSettings(
            CollageSettings.ParseDirection(file.Direction),
            CollageSettings.ParseSizing(file.Sizing),
            Math.Max(0, file.BorderPx),
            Math.Max(0, file.SpacingPx),
            CollageColor.Parse(file.Background),
            Math.Max(1, file.Rows),
            Math.Max(1, file.Columns),
            file.CustomGridInitialized);
    }

    /// <summary>整份写回（方向 / 尺寸方式 / 外边距 / 间距 / 背景色 / 宫格行列）。</summary>
    public static void Save(CollageSettings settings)
    {
        try
        {
            void Apply(SettingsFile file)
            {
                file.Collage ??= new CollageFile();
                file.Collage.Direction = settings.DirectionKey;
                file.Collage.Sizing = settings.SizingKey;
                file.Collage.BorderPx = Math.Max(0, settings.Border);
                file.Collage.SpacingPx = Math.Max(0, settings.Spacing);
                file.Collage.Background = CollageColor.Format(settings.Background);
                file.Collage.Rows = Math.Max(1, settings.Rows);
                file.Collage.Columns = Math.Max(1, settings.Columns);
                file.Collage.CustomGridInitialized = settings.CustomGridInitialized;
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) SettingsStore.Modify(Apply);
            else SettingsStore.Modify(ConfigStore.ConfigPath, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "保存拼图设置失败：" + ex.Message);
        }
    }

    // ============ PDF 导出 ============

    /// <summary>
    /// 读 PDF 设置（照上游 <c>readPdf</c>）：单个字段能读就用，**整份校验不过就整体回默认**，
    /// 所以手改坏的组合不会让导出框一打开就是不可用的形状。
    /// </summary>
    public static CollagePdfSettings LoadPdf()
    {
        CollageFile file = Read();
        var settings = new CollagePdfSettings(
            CollagePdfSettings.ParseMethod(file.PdfStitchingMethod),
            CollagePdfSettings.ParseTemplate(file.PdfTemplateType),
            Numeric(file.PdfImageSpacingMm, 5),
            CollagePdfSettings.ParsePaper(file.PdfPaperSize),
            CollagePdfSettings.ParseOrientation(file.PdfOrientation),
            Numeric(file.PdfCustomWidthMm, 210),
            Numeric(file.PdfCustomHeightMm, 297),
            Numeric(file.PdfMarginTopMm, 10),
            Numeric(file.PdfMarginTopMm, 10),
            Numeric(file.PdfMarginTopMm, 10),
            Numeric(file.PdfMarginTopMm, 10),
            CollagePdfSettings.ParseExportMode(file.PdfExportMode),
            file.PdfImageQuality is >= 0 and <= 100 ? file.PdfImageQuality : 100);

        return CollagePdfGeometry.Validate(settings).Count == 0 ? settings : CollagePdfSettings.Default;
    }

    /// <summary>整份写回 PDF 设置；**校验不过不写**（照上游 <c>writePdf</c>）。</summary>
    public static void SavePdf(CollagePdfSettings settings)
    {
        if (CollagePdfGeometry.Validate(settings).Count > 0) return;

        try
        {
            void Apply(SettingsFile file)
            {
                file.Collage ??= new CollageFile();
                file.Collage.PdfStitchingMethod = settings.MethodKey;
                file.Collage.PdfTemplateType = settings.TemplateKey;
                file.Collage.PdfImageSpacingMm = settings.ImageSpacingMm;
                file.Collage.PdfPaperSize = settings.PaperKey;
                file.Collage.PdfOrientation = settings.OrientationKey;
                file.Collage.PdfCustomWidthMm = settings.CustomWidthMm;
                file.Collage.PdfCustomHeightMm = settings.CustomHeightMm;
                file.Collage.PdfMarginTopMm = settings.MarginMm;
                file.Collage.PdfMarginBottomMm = settings.MarginMm;
                file.Collage.PdfMarginLeftMm = settings.MarginMm;
                file.Collage.PdfMarginRightMm = settings.MarginMm;
                file.Collage.PdfExportMode = settings.ExportModeKey;
                file.Collage.PdfImageQuality = Math.Clamp(settings.ImageQuality, 0, 100);
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) SettingsStore.Modify(Apply);
            else SettingsStore.Modify(ConfigStore.ConfigPath, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "保存 PDF 设置失败：" + ex.Message);
        }
    }

    private static double Numeric(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private static CollageFile Read()    {
        try
        {
            string path = ConfigStore.ConfigPath;
            return string.IsNullOrEmpty(path)
                ? new CollageFile()
                : SettingsStore.Read(path).Collage ?? new CollageFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "读取拼图设置失败：" + ex.Message);
            return new CollageFile();
        }
    }
}
