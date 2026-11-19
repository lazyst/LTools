using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 翻译设置的读写：<c>configs/settings.json</c> 的 <c>Translation</c> 段。
/// </summary>
/// <remarks>
/// 和 <see cref="SelectionStore"/> 的差别是**不缓存、不防抖**：翻译设置只在设置框点「保存」、
/// 或结果窗换目标语言时改一次，不是滚轮连续操作，直接读 / 写一个域最省事，也免得缓存与
/// 设置页（P7）各存一份。
/// </remarks>
internal static class TranslationStore
{
    /// <summary>当前配置（没配过的字段按默认值补齐）。</summary>
    public static TranslationConfig Config
    {
        get
        {
            TranslationFile file = Load();
            return TranslationConfig.Normalize(file.Endpoint, file.Model, file.ApiKey, file.Target,
                file.Source, file.TimeoutMs);
        }
    }

    /// <summary>译文画进原图（照上游 <c>screenshot_translation/original_image_translation</c>）。</summary>
    public static bool OriginalImage => Load().OriginalImage;

    /// <summary>复制译文时复制成图片（照上游 <c>copy_translation_results_as_image</c>）。</summary>
    public static bool CopyAsImage => Load().CopyAsImage;

    /// <summary>翻译前的版面处理（<c>smart_merge</c> / <c>original</c>）。</summary>
    public static string Layout => TranslationLayout.Normalize(Load().LayoutProcessing);

    /// <summary>第二目标语言（空 = 不用；照上游 <c>secondary_target_language</c>）。</summary>
    public static string SecondaryTarget => TranslationLanguages.NormalizeSecondary(Load().SecondaryTarget);

    /// <summary>语言自动检测的置信度门槛（30–100，默认 60）。</summary>
    public static int DetectionThreshold => TranslationLanguages.NormalizeThreshold(Load().DetectionThreshold);

    /// <summary>这次实际译成哪种语言（原文已经是主目标语言就换第二目标语言）。</summary>
    public static string ResolveTarget(string? target, string? text) =>
        TranslationLanguages.ResolveTarget(target, SecondaryTarget, DetectionThreshold, text);

    /// <summary>只改第二目标语言（空 = 不用）。</summary>
    public static void SetSecondaryTarget(string? key)
    {
        string normalized = TranslationLanguages.NormalizeSecondary(key);
        Modify(file => file.SecondaryTarget = normalized.Length == 0 ? null : normalized);
    }

    /// <summary>只改置信度门槛（30–100，越界按范围夹取）。</summary>
    public static void SetDetectionThreshold(int percent) =>
        Modify(file => file.DetectionThreshold = TranslationLanguages.NormalizeThreshold(percent));

    public static void Save(TranslationConfig config)
    {
        Modify(file =>
        {
            file.Endpoint = config.Endpoint;
            file.Model = config.Model;
            file.ApiKey = config.ApiKey;
            file.Target = config.Target;
            file.Source = config.Source;
            file.TimeoutMs = config.TimeoutMs;
        });
    }

    /// <summary>设置框「保存」：端点 / 语言与三个显示选项一次写完（只动 <c>settings.json</c>）。</summary>
    public static void SaveWithDisplay(TranslationConfig config, bool originalImage,
        bool copyAsImage, string? layout, string? secondaryTarget = null, int? threshold = null)
    {
        string normalized = TranslationLayout.Normalize(layout);
        string secondary = TranslationLanguages.NormalizeSecondary(secondaryTarget);
        Modify(file =>
        {
            file.Endpoint = config.Endpoint;
            file.Model = config.Model;
            file.ApiKey = config.ApiKey;
            file.Target = config.Target;
            file.Source = config.Source;
            file.TimeoutMs = config.TimeoutMs;
            file.OriginalImage = originalImage;
            file.CopyAsImage = copyAsImage;
            file.LayoutProcessing = normalized;
            if (secondaryTarget != null) file.SecondaryTarget = secondary.Length == 0 ? null : secondary;
            if (threshold != null) file.DetectionThreshold = TranslationLanguages.NormalizeThreshold(threshold.Value);
        });
    }

    /// <summary>译文画进原图（只动这一个字段）。</summary>
    public static void SetOriginalImage(bool on) => Modify(file => file.OriginalImage = on);

    /// <summary>复制译文时复制成图片（只动这一个字段）。</summary>
    public static void SetCopyAsImage(bool on) => Modify(file => file.CopyAsImage = on);

    /// <summary>翻译前的版面处理（只动这一个字段；不认识的值回落到智能合并）。</summary>
    public static void SetLayout(string? layout)
    {
        string normalized = TranslationLayout.Normalize(layout);
        Modify(file => file.LayoutProcessing = normalized);
    }

    /// <summary>
    /// 就地改字段：**别用 <c>new TranslationFile</c> 整体替换**，那样会把别处刚写进去、
    /// 这段代码还不知道的字段（译文画进原图 / 复制成图片 / 版面处理，以后还会更多）一起抹掉。
    /// </summary>
    private static void Modify(Action<TranslationFile> change)
    {
        try
        {
            static void Apply(SettingsFile settings, Action<TranslationFile> change)
            {
                settings.Translation ??= new TranslationFile();
                change(settings.Translation);
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) SettingsStore.Modify(d => Apply(d, change));
            else SettingsStore.Modify(ConfigStore.ConfigPath, d => Apply(d, change));
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Translate", "保存翻译设置失败：" + ex.Message);
        }
    }

    /// <summary>只换目标语言（结果窗下拉立刻落盘，下次还是这个目标）。</summary>
    public static TranslationConfig SetTarget(string target)
    {
        TranslationConfig current = Config;
        TranslationConfig next = current with { Target = TranslationLanguages.Normalize(target) };
        if (next.Target != current.Target) Save(next);
        return next;
    }

    private static TranslationFile Load()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            return string.IsNullOrEmpty(path)
                ? new TranslationFile()
                : SettingsStore.Read(path).Translation ?? new TranslationFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Translate", "读取翻译设置失败：" + ex.Message);
            return new TranslationFile();
        }
    }
}
