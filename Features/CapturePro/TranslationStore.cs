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

    public static void Save(TranslationConfig config)
    {
        try
        {
            var file = new TranslationFile
            {
                Endpoint = config.Endpoint,
                Model = config.Model,
                ApiKey = config.ApiKey,
                Target = config.Target,
                Source = config.Source,
                TimeoutMs = config.TimeoutMs,
            };

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) SettingsStore.Modify(d => d.Translation = file);
            else SettingsStore.Modify(ConfigStore.ConfigPath, d => d.Translation = file);
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
