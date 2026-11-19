using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 文字识别全局设置（<c>configs/settings.json</c> 的 <c>TextRecognition</c> 段）的读写与归一化。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>TextRecognitionSettings</c> 的 <c>text_recognition/*</c>：默认排版 / 标点、
/// 模型档、检测缩放、检测处理、常驻进程、模型热启动、填充样式、文字高亮、
/// 结果存为图片、原图预览。默认值逐条对齐 <c>storage/configurationschema.cpp</c>。
/// </para>
/// <para>
/// 读盘只发生一次（覆盖层、钩子线程、识别线程都要问），写盘只写 settings 域。
/// 归一化都是纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class TextRecognitionStore
{
    public const string FormattingNone = "none";

    public const string ModelExtraSmall = "extra_small";
    public const string ModelSmall = "small";
    public const string ModelMedium = "medium";
    public const string DefaultModelType = ModelSmall;

    public const string ResizeMax = "max";
    public const string ResizeMin = "min";
    public const string DefaultResizePolicy = ResizeMax;

    public const string ProcessingAccuracyFirst = "accuracy_first";
    public const string ProcessingSpeedFirst = "speed_first";
    public const string DefaultDetectionProcessing = ProcessingAccuracyFirst;

    public const string FillBlur = OcrFillStyle.Blur;
    public const string FillBackground = OcrFillStyle.BackgroundFill;
    public const string DefaultFillStyle = OcrFillStyle.Default;

    public const bool DefaultResidentProcess = true;
    public const bool DefaultModelHotStart = false;
    public const bool DefaultShowTextHighlighting = false;
    public const bool DefaultSaveResultAsImage = true;
    /// <summary>打开识别结果时先显示原图（上游 <c>show_original_image</c> 默认关）。</summary>
    public const bool DefaultShowOriginalPreview = false;

    /// <summary>模型档的全部取值（下拉顺序同上游）。</summary>
    public static readonly string[] ModelTypes = { ModelExtraSmall, ModelSmall, ModelMedium };

    private static readonly object Gate = new();
    private static TextRecognitionFile? _cached;

    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    /// <summary>默认排版键（已归一）。</summary>
    public static string Formatting => OcrFormatting.Normalize(Snapshot().DefaultFormatting);

    /// <summary>默认标点键（已归一）。</summary>
    public static string Punctuation => OcrPunctuation.Normalize(Snapshot().DefaultPunctuation);

    /// <summary>OCR 模型档（<c>extra_small</c> / <c>small</c> / <c>medium</c>）。</summary>
    public static string ModelType => NormalizeModelType(Snapshot().ModelType);

    /// <summary>文本检测缩放（<c>max</c> / <c>min</c>）。</summary>
    public static string DetectorResizePolicy => NormalizeResizePolicy(Snapshot().DetectorResizePolicy);

    /// <summary>文本检测处理（<c>accuracy_first</c> / <c>speed_first</c>）。</summary>
    public static string DetectionProcessing =>
        NormalizeDetectionProcessing(Snapshot().TextDetectionProcessing);

    /// <summary>常驻识别引擎（关掉后每次识别完立刻卸载）。</summary>
    public static bool ResidentProcess =>
        Snapshot().ResidentProcess ?? DefaultResidentProcess;

    /// <summary>启动时预加载所选模型（需要常驻）。</summary>
    public static bool ModelHotStart => Snapshot().ModelHotStart ?? DefaultModelHotStart;

    /// <summary>文字区域的填充样式（<c>blur</c> / <c>background_fill</c>）。</summary>
    public static string FillStyle => NormalizeFillStyle(Snapshot().FillStyle);

    /// <summary>识别结果加遮罩（压暗文字以外的区域）。</summary>
    public static bool ShowTextHighlighting =>
        Snapshot().ShowTextHighlighting ?? DefaultShowTextHighlighting;

    /// <summary>保存图片时带上已显示的识别 / 译文结果。</summary>
    public static bool SaveResultAsImage =>
        Snapshot().SaveRecognitionResultAsImage ?? DefaultSaveResultAsImage;

    /// <summary>在结果旁显示原图预览。</summary>
    public static bool ShowOriginalPreview =>
        Snapshot().ShowOriginalImagePreview ?? DefaultShowOriginalPreview;

    /// <summary>两个默认值一起读（进文字编辑时一次取够，少读两遍盘）。</summary>
    public static (string Formatting, string Punctuation) Defaults() =>
        (Formatting, Punctuation);

    /// <summary>存默认排版（其余字段按当前值写回，避免把别的项抹成空）。</summary>
    public static void SetFormatting(string formatting)
    {
        string value = OcrFormatting.Normalize(formatting);
        Save(file => file.DefaultFormatting = value);
    }

    /// <summary>存默认标点。</summary>
    public static void SetPunctuation(string punctuation)
    {
        string value = OcrPunctuation.Normalize(punctuation);
        Save(file => file.DefaultPunctuation = value);
    }

    /// <summary>存模型档（同时让识别引擎按新档位重建）。</summary>
    public static void SetModelType(string? model)
    {
        string value = NormalizeModelType(model);
        Save(file => file.ModelType = value);
        PaddleOcr.OnSettingsChanged();
    }

    /// <summary>存检测缩放。</summary>
    public static void SetDetectorResizePolicy(string? policy)
    {
        string value = NormalizeResizePolicy(policy);
        Save(file => file.DetectorResizePolicy = value);
        PaddleOcr.OnSettingsChanged();
    }

    /// <summary>存检测处理。</summary>
    public static void SetDetectionProcessing(string? processing)
    {
        string value = NormalizeDetectionProcessing(processing);
        Save(file => file.TextDetectionProcessing = value);
        PaddleOcr.OnSettingsChanged();
    }

    /// <summary>存常驻开关（关掉时顺手把空闲的引擎收掉）。</summary>
    public static void SetResidentProcess(bool on)
    {
        Save(file => file.ResidentProcess = on);
        PaddleOcr.OnSettingsChanged();
    }

    /// <summary>存模型热启动（打开时立刻预热一次）。</summary>
    public static void SetModelHotStart(bool on)
    {
        Save(file => file.ModelHotStart = on);
        PaddleOcr.OnSettingsChanged();
    }

    /// <summary>存填充样式。</summary>
    public static void SetFillStyle(string? style) =>
        Save(file => file.FillStyle = NormalizeFillStyle(style));

    /// <summary>存文字高亮开关。</summary>
    public static void SetShowTextHighlighting(bool on) =>
        Save(file => file.ShowTextHighlighting = on);

    /// <summary>存「结果存为图片」。</summary>
    public static void SetSaveResultAsImage(bool on) =>
        Save(file => file.SaveRecognitionResultAsImage = on);

    /// <summary>存「显示原图预览」。</summary>
    public static void SetShowOriginalPreview(bool on) =>
        Save(file => file.ShowOriginalImagePreview = on);

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>模型档只认三个键，其余按 small。</summary>
    public static string NormalizeModelType(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() switch
        {
            ModelExtraSmall => ModelExtraSmall,
            ModelMedium => ModelMedium,
            _ => ModelSmall,
        };

    /// <summary>检测缩放只认 <c>min</c>，其余按 <c>max</c>。</summary>
    public static string NormalizeResizePolicy(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == ResizeMin ? ResizeMin : ResizeMax;

    /// <summary>检测处理只认 <c>speed_first</c>，其余按精度优先。</summary>
    public static string NormalizeDetectionProcessing(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == ProcessingSpeedFirst
            ? ProcessingSpeedFirst
            : ProcessingAccuracyFirst;

    /// <summary>填充样式只认 <c>blur</c>，其余按背景填充。</summary>
    public static string NormalizeFillStyle(string? key) => OcrFillStyle.Normalize(key);

    /// <summary>
    /// 模型档 → 识别引擎键（<c>v6tiny</c> / <c>v6small</c> / <c>v6medium</c>）。
    /// 该档的模型还没就位时退回随包的超小档；连超小档都没有返回 null（= 系统 OCR）。
    /// </summary>
    public static string? EngineForModelType(string? model)
    {
        PaddleOcr.Tier tier = PaddleOcr.TierOf(NormalizeModelType(model));
        if (PaddleOcr.IsReady(tier)) return PaddleOcr.EngineKey(tier);
        return PaddleOcr.IsReady(PaddleOcr.Tier.Tiny) ? PaddleOcr.EngineKey(PaddleOcr.Tier.Tiny) : null;
    }

    private static TextRecognitionFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        TextRecognitionFile loaded;
        try
        {
            string path = Anchor;
            loaded = string.IsNullOrEmpty(path)
                ? new TextRecognitionFile()
                : SettingsStore.Read(path).TextRecognition ?? new TextRecognitionFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextTransform", "读取文字识别设置失败：" + ex.Message);
            loaded = new TextRecognitionFile();
        }

        loaded.DefaultFormatting = OcrFormatting.Normalize(loaded.DefaultFormatting);
        loaded.DefaultPunctuation = OcrPunctuation.Normalize(loaded.DefaultPunctuation);
        loaded.ModelType = NormalizeModelType(loaded.ModelType);
        loaded.DetectorResizePolicy = NormalizeResizePolicy(loaded.DetectorResizePolicy);
        loaded.TextDetectionProcessing = NormalizeDetectionProcessing(loaded.TextDetectionProcessing);
        loaded.ResidentProcess ??= DefaultResidentProcess;
        loaded.ModelHotStart ??= DefaultModelHotStart;
        loaded.FillStyle = NormalizeFillStyle(loaded.FillStyle);
        loaded.ShowTextHighlighting ??= DefaultShowTextHighlighting;
        loaded.SaveRecognitionResultAsImage ??= DefaultSaveResultAsImage;
        loaded.ShowOriginalImagePreview ??= DefaultShowOriginalPreview;

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<TextRecognitionFile> mutate)
    {
        TextRecognitionFile next;
        lock (Gate)
        {
            next = Clone(Snapshot());
            mutate(next);
            next.DefaultFormatting = OcrFormatting.Normalize(next.DefaultFormatting);
            next.DefaultPunctuation = OcrPunctuation.Normalize(next.DefaultPunctuation);
            next.ModelType = NormalizeModelType(next.ModelType);
            next.DetectorResizePolicy = NormalizeResizePolicy(next.DetectorResizePolicy);
            next.TextDetectionProcessing = NormalizeDetectionProcessing(next.TextDetectionProcessing);
            next.ResidentProcess ??= DefaultResidentProcess;
            next.ModelHotStart ??= DefaultModelHotStart;
            next.FillStyle = NormalizeFillStyle(next.FillStyle);
            next.ShowTextHighlighting ??= DefaultShowTextHighlighting;
            next.SaveRecognitionResultAsImage ??= DefaultSaveResultAsImage;
            next.ShowOriginalImagePreview ??= DefaultShowOriginalPreview;
            _cached = Clone(next);
        }

        try
        {
            SettingsStore.Modify(Anchor, file =>
            {
                file.TextRecognition ??= new TextRecognitionFile();
                TextRecognitionFile target = file.TextRecognition;
                target.DefaultFormatting = next.DefaultFormatting;
                target.DefaultPunctuation = next.DefaultPunctuation;
                target.ModelType = next.ModelType;
                target.DetectorResizePolicy = next.DetectorResizePolicy;
                target.TextDetectionProcessing = next.TextDetectionProcessing;
                target.ResidentProcess = next.ResidentProcess;
                target.ModelHotStart = next.ModelHotStart;
                target.FillStyle = next.FillStyle;
                target.ShowTextHighlighting = next.ShowTextHighlighting;
                target.SaveRecognitionResultAsImage = next.SaveRecognitionResultAsImage;
                target.ShowOriginalImagePreview = next.ShowOriginalImagePreview;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextTransform", "保存文字识别设置失败：" + ex.Message);
        }
    }

    private static TextRecognitionFile Clone(TextRecognitionFile file) => new()
    {
        DefaultFormatting = file.DefaultFormatting,
        DefaultPunctuation = file.DefaultPunctuation,
        ModelType = file.ModelType,
        DetectorResizePolicy = file.DetectorResizePolicy,
        TextDetectionProcessing = file.TextDetectionProcessing,
        ResidentProcess = file.ResidentProcess,
        ModelHotStart = file.ModelHotStart,
        FillStyle = file.FillStyle,
        ShowTextHighlighting = file.ShowTextHighlighting,
        SaveRecognitionResultAsImage = file.SaveRecognitionResultAsImage,
        ShowOriginalImagePreview = file.ShowOriginalImagePreview,
    };
}
