using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 文字识别默认排版 / 标点的读写：<c>configs/settings.json</c> 的 <c>TextRecognition</c> 段。
/// </summary>
/// <remarks>
/// 照上游 <c>TextRecognitionSettings</c> 的 <c>text_recognition/default_formatting</c> 与
/// <c>text_recognition/default_punctuation</c>：
/// 进文字编辑时先应用一次，用户在识别窗下拉里改了就顺手写成新的默认值
/// （与上游「工具栏上改一次就记住」一致）。读坏 / 认不出的值按 <c>none</c>。
/// </remarks>
internal static class TextRecognitionStore
{
    /// <summary>默认排版键（已归一）。</summary>
    public static string Formatting
    {
        get
        {
            TextRecognitionFile file = Load();
            return OcrFormatting.Normalize(file.DefaultFormatting);
        }
    }

    /// <summary>默认标点键（已归一）。</summary>
    public static string Punctuation
    {
        get
        {
            TextRecognitionFile file = Load();
            return OcrPunctuation.Normalize(file.DefaultPunctuation);
        }
    }

    /// <summary>两个默认值一起读（进文字编辑时一次取够，少读两遍盘）。</summary>
    public static (string Formatting, string Punctuation) Defaults()
    {
        TextRecognitionFile file = Load();
        return (OcrFormatting.Normalize(file.DefaultFormatting),
            OcrPunctuation.Normalize(file.DefaultPunctuation));
    }

    /// <summary>存默认排版（同时把标点那一位按当前值写回，避免把另一项抹成空）。</summary>
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

    private static void Save(Action<TextRecognitionFile> mutate)
    {
        try
        {
            void Apply(TextRecognitionFile file)
            {
                file.DefaultFormatting ??= OcrFormatting.None;
                file.DefaultPunctuation ??= OcrPunctuation.None;
                mutate(file);
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath))
                SettingsStore.Modify(d =>
                {
                    d.TextRecognition ??= new TextRecognitionFile();
                    Apply(d.TextRecognition);
                });
            else
                SettingsStore.Modify(ConfigStore.ConfigPath, d =>
                {
                    d.TextRecognition ??= new TextRecognitionFile();
                    Apply(d.TextRecognition);
                });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextTransform", "保存文字识别默认值失败：" + ex.Message);
        }
    }

    private static TextRecognitionFile Load()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            return string.IsNullOrEmpty(path)
                ? new TextRecognitionFile()
                : SettingsStore.Read(path).TextRecognition ?? new TextRecognitionFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.TextTransform", "读取文字识别默认值失败：" + ex.Message);
            return new TextRecognitionFile();
        }
    }
}
