namespace LTools.Config;

/// <summary>
/// 「截图输出」的设置（<c>configs/settings.json</c> 的 <c>Export</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshot/*</c> 那一组输出项：<c>image_format</c>（默认 png）、
/// <c>image_quality</c>（0–100，默认 100，只对 JPEG 有效）、<c>compression_level</c>
/// （low / medium / high，控制 PNG 的压缩力度）、<c>image_save_directory</c>
/// （默认系统「图片」文件夹）、<c>manual_save_filename_format</c> /
/// <c>auto_save_filename_format</c>（默认 <c>截图_{YYYY-MM-DD_HH-mm-ss}</c>）、
/// <c>save_as_file_dialog</c>（builtin / system）、<c>show_save_result</c>、
/// <c>copy_image_file_to_clipboard</c>、<c>auto_save_after_execution_actions</c>。
/// </para>
/// <para>
/// 平台差异：上游还能输出 WebP / JPEG XL / AVIF / PDF（libvips 带的编码器），
/// 本地只有 WPF / 自己写的 PNG 编码器，所以格式只列 PNG / JPEG / BMP / TIFF。
/// 取值与默认值见 <c>Features/CapturePro/ExportSettings.cs</c>（英文键与上游一致）。
/// </para>
/// </remarks>
public sealed class ExportFile
{
    /// <summary>自动保存 / 快速保存用的图片格式：png / jpeg / bmp / tiff。</summary>
    public string? Format { get; set; } = "png";

    /// <summary>JPEG 质量（0–100，默认 100）。其它格式不看这个值。</summary>
    public int? Quality { get; set; } = 100;

    /// <summary>PNG 压缩力度：low / medium / high。</summary>
    public string? Compression { get; set; } = "medium";

    /// <summary>保存目录；空 = 系统「图片」文件夹。</summary>
    public string? SaveDirectory { get; set; } = "";

    /// <summary>手动另存的文件名模板（不含扩展名），如 <c>截图_{YYYY-MM-DD_HH-mm-ss}</c>。</summary>
    public string? ManualFileNameFormat { get; set; } = "截图_{YYYY-MM-DD_HH-mm-ss}";

    /// <summary>自动保存（快速保存 / 复制时存盘）的文件名模板。</summary>
    public string? AutoFileNameFormat { get; set; } = "截图_{YYYY-MM-DD_HH-mm-ss}";

    /// <summary>另存为用哪个对话框：builtin（自绘预览框）/ system（Windows 另存为）。</summary>
    public string? SaveAsDialog { get; set; } = "builtin";

    /// <summary>保存之后留着对话框显示结果（而不是马上关掉）。缺省 = 关。</summary>
    public bool? ShowSaveResult { get; set; }

    /// <summary>复制截图时把导出的那个文件也一起放进剪贴板（CF_HDROP）。缺省 = 关。</summary>
    public bool? CopyImageFileToClipboard { get; set; }

    /// <summary>哪些动作执行完之后顺带存一份图片（场景键见 <c>ExportSettings.Scenarios</c>）。</summary>
    public System.Collections.Generic.List<string>? AutoSaveAfterExecutionActions { get; set; } = new();
}
