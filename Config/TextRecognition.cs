namespace LTools.Config;

/// <summary>
/// 文字识别的全局设置（<c>configs/settings.json</c> 的 <c>TextRecognition</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>text_recognition/*</c>：默认排版 / 标点（<c>default_formatting</c> /
/// <c>default_punctuation</c>）、模型档（<c>model_type</c>）、检测缩放（<c>detector_resize_policy</c>）、
/// 检测处理（<c>text_detection_processing</c>）、常驻进程（<c>resident_process</c>）、
/// 模型热启动（<c>model_hot_start</c>）、填充样式（<c>fill_style</c>）、文字高亮
/// （<c>show_text_highlighting</c>）、结果存为图片（<c>save_recognition_result_as_image</c>）、
/// 原图预览（<c>show_original_image_preview</c>）。
/// </para>
/// <para>
/// 取值与默认值逐条对齐 <c>storage/configurationschema.cpp</c>：认不出的值一律回默认，
/// 归一化在 <c>Features/CapturePro/TextRecognitionStore.cs</c>（纯函数，<c>--selftest</c> 直接断言）。
/// </para>
/// </remarks>
public sealed class TextRecognitionFile
{
    /// <summary>默认排版（<c>none</c> / <c>keep</c> / <c>remove</c> / <c>smart</c>）。</summary>
    public string? DefaultFormatting { get; set; } = "none";

    /// <summary>默认标点（<c>none</c> / <c>half</c> / <c>full</c>）。</summary>
    public string? DefaultPunctuation { get; set; } = "none";

    /// <summary>
    /// OCR 模型档（照上游 <c>text_recognition/model_type</c>）：
    /// <c>extra_small</c>（超小 V6，随包）/ <c>small</c>（小 V6）/ <c>medium</c>（中 V6）。
    /// </summary>
    public string? ModelType { get; set; } = "small";

    /// <summary>
    /// 文本检测缩放（照上游 <c>text_recognition/detector_resize_policy</c>）：
    /// <c>max</c>（限制长边，更快）/ <c>min</c>（放大短边，更细）。
    /// </summary>
    public string? DetectorResizePolicy { get; set; } = "max";

    /// <summary>
    /// 文本检测处理（照上游 <c>text_recognition/text_detection_processing</c>）：
    /// <c>accuracy_first</c>（精度优先）/ <c>speed_first</c>（速度优先）。
    /// </summary>
    public string? TextDetectionProcessing { get; set; } = "accuracy_first";

    /// <summary>
    /// 常驻识别进程（照上游 <c>text_recognition/resident_process</c>，完整版默认开）：
    /// 开 = 识别完不卸载引擎（省下一次加载）；关 = 每次识别完立刻卸载。
    /// </summary>
    public bool? ResidentProcess { get; set; } = true;

    /// <summary>
    /// 模型热启动（照上游 <c>text_recognition/model_hot_start</c>，默认关）：
    /// 启动时预加载所选模型，第一次识别不必等加载（需要常驻进程）。
    /// </summary>
    public bool? ModelHotStart { get; set; } = false;

    /// <summary>
    /// 识别文字区域的填充样式（照上游 <c>text_recognition/fill_style</c>）：
    /// <c>blur</c>（高斯模糊原图）/ <c>background_fill</c>（填估出来的底色）。
    /// </summary>
    public string? FillStyle { get; set; } = "background_fill";

    /// <summary>
    /// 给识别结果加一层遮罩（照上游 <c>text_recognition/show_text_highlighting</c>，默认关）：
    /// 除文字本身以外压暗，让识别结果更醒目。
    /// </summary>
    public bool? ShowTextHighlighting { get; set; } = false;

    /// <summary>
    /// 保存截图时带上已显示的识别 / 译文结果（照上游
    /// <c>text_recognition/save_recognition_result_as_image</c>，默认开）。
    /// </summary>
    public bool? SaveRecognitionResultAsImage { get; set; } = true;

    /// <summary>
    /// 在识别 / 翻译结果旁边显示原图预览（照上游
    /// <c>text_recognition/show_original_image_preview</c>，默认开）。
    /// </summary>
    public bool? ShowOriginalImagePreview { get; set; } = true;
}
