using System;
using System.Collections.Generic;
using System.Text;

namespace LTools.Features.CapturePro;

/// <summary>
/// 翻译语言目录与「这段文字是什么语言」的粗判。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>translationlanguagedetector</c> 的定位：只服务两件事——给模型一句「从 X 译到 Y」，
/// 以及把目标语言的标签显示给人看。**不做概率模型**，按字符族数比例判断：
/// 假名 → 日语、谚文 → 韩语、汉字 → 中文、西里尔 → 俄语、其余按拉丁 → 英语。
/// </para>
/// <para>
/// 键就是上游的语言 code（<c>zh-CN</c> / <c>en</c> …），存进配置的也是它；
/// 标签是给人看的名字，提示词里用「中文名（code）」拼一句。
/// </para>
/// </remarks>
internal static class TranslationLanguages
{
    /// <summary>源语言 = 自动判断。</summary>
    public const string Auto = "auto";

    /// <summary>默认目标语言（上游默认跟随界面语言，这里固定简体中文）。</summary>
    public const string DefaultTarget = "zh-CN";

    /// <summary>语言目录：键 / 标签 / 提示词里的写法。顺序就是下拉顺序。</summary>
    public static readonly IReadOnlyList<TranslationLanguage> All = new[]
    {
        new TranslationLanguage("zh-CN", "简体中文", "简体中文（Chinese, zh-CN）"),
        new TranslationLanguage("zh-TW", "繁體中文", "繁體中文（Traditional Chinese, zh-TW）"),
        new TranslationLanguage("en", "English", "English（英语, en）"),
        new TranslationLanguage("ja", "日本語", "日本語（Japanese, ja）"),
        new TranslationLanguage("ko", "한국어", "한국어（Korean, ko）"),
        new TranslationLanguage("fr", "Français", "Français（法语, fr）"),
        new TranslationLanguage("de", "Deutsch", "Deutsch（德语, de）"),
        new TranslationLanguage("es", "Español", "Español（西班牙语, es）"),
        new TranslationLanguage("ru", "Русский", "Русский（俄语, ru）"),
    };

    /// <summary>认不出的键回落到默认目标语言。</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return DefaultTarget;
        string trimmed = key.Trim();
        foreach (TranslationLanguage language in All)
            if (string.Equals(language.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                return language.Key;

        return DefaultTarget;
    }

    /// <summary>源语言：<c>auto</c> 或目录里的键。</summary>
    public static string NormalizeSource(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Auto;
        string trimmed = key.Trim();
        if (string.Equals(trimmed, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        foreach (TranslationLanguage language in All)
            if (string.Equals(language.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                return language.Key;

        return Auto;
    }

    public static string LabelOf(string? key)
    {
        string normalized = Normalize(key);
        foreach (TranslationLanguage language in All)
            if (language.Key == normalized) return language.Label;

        return normalized;
    }

    /// <summary>提示词里的写法（「简体中文（Chinese, zh-CN）」）。</summary>
    public static string PromptOf(string? key)
    {
        string normalized = Normalize(key);
        foreach (TranslationLanguage language in All)
            if (language.Key == normalized) return language.Prompt;

        return normalized;
    }

    /// <summary>按字符族判语言；空文本返回 <see cref="Auto"/>。</summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Auto;

        int kana = 0, hangul = 0, han = 0, cyrillic = 0, latin = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsDigit(c) || char.IsPunctuation(c) ||
                char.IsSymbol(c))
            {
                continue;
            }

            if (c is >= '\u3040' and <= '\u30FF') kana++;
            else if (c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF') hangul++;
            else if (c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF') han++;
            else if (c is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                     (c >= '\u00C0' && c <= '\u024F')) latin++;
        }

        // 假名 / 谚文是强信号：哪怕只占一小部分也说明是那门语言
        if (kana > 0 && kana * 4 >= han) return "ja";
        if (hangul > 0) return "ko";
        if (han > 0) return "zh-CN";
        if (cyrillic > latin) return "ru";
        if (latin > 0) return "en";
        return Auto;
    }

    /// <summary>把多行拼成一段给检测器看（保留换行不影响判语言）。</summary>
    public static string Join(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 检测结果：语言键 + 置信度（0–1）。
    /// </summary>
    /// <remarks>
    /// 上游用 <c>snow_language_detect</c> 的概率模型；本项目按字符族占比给一个等价信号——
    /// 全是汉字 = 1.0，中英混排按汉字占比。够用来实现「原文已经是目标语言就换第二目标语言」
    /// （上游 <c>translationlanguagedetector</c> 的用法），不是同一套数值。
    /// </remarks>
    public static DetectedLanguage DetectDetailed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new DetectedLanguage(Auto, 0);

        int kana = 0, hangul = 0, han = 0, cyrillic = 0, latin = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsDigit(c) || char.IsPunctuation(c) ||
                char.IsSymbol(c))
            {
                continue;
            }

            if (c is >= '\u3040' and <= '\u30FF') kana++;
            else if (c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF') hangul++;
            else if (c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF') han++;
            else if (c is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                     (c >= '\u00C0' && c <= '\u024F')) latin++;
        }

        int total = kana + hangul + han + cyrillic + latin;
        if (total == 0) return new DetectedLanguage(Auto, 0);

        // 与 Detect 同一套判定顺序，只是把「有多少字支持这个结论」也带出来
        if (kana > 0 && kana * 4 >= han) return new DetectedLanguage("ja", (double)(kana + han) / total);
        if (hangul > 0) return new DetectedLanguage("ko", (double)hangul / total);
        if (han > 0) return new DetectedLanguage("zh-CN", (double)han / total);
        if (cyrillic > latin) return new DetectedLanguage("ru", (double)cyrillic / total);
        if (latin > 0) return new DetectedLanguage("en", (double)latin / total);
        return new DetectedLanguage(Auto, 0);
    }

    /// <summary>置信度门槛的默认值 / 范围（照上游 <c>kSchemaRange4</c>：30–100）。</summary>
    public const int DefaultThreshold = 60;
    public const int MinThreshold = 30;
    public const int MaxThreshold = 100;

    public static int NormalizeThreshold(int percent) =>
        Math.Clamp(percent <= 0 ? DefaultThreshold : percent, MinThreshold, MaxThreshold);

    /// <summary>第二目标语言：空 = 不用（与上游 <c>secondary_target_language</c> 空值同义）。</summary>
    public static string NormalizeSecondary(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        string trimmed = key.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            return "";
        return Normalize(trimmed);
    }

    /// <summary>两种语言算不算「同一门」（简繁中文算同一门，与上游映射到 <c>cmn</c> 一致）。</summary>
    public static bool SameLanguage(string? a, string? b)
    {
        string left = Normalize(a), right = Normalize(b);
        if (left == right) return true;
        return IsChinese(left) && IsChinese(right);
    }

    private static bool IsChinese(string key) => key is "zh-CN" or "zh-TW";

    /// <summary>
    /// 这次实际该译成哪种语言（照上游 <c>resolveTranslationTarget</c>）：
    /// 自动判断出原文**已经是主目标语言**、且置信度达到门槛时，改用第二目标语言。
    /// </summary>
    public static string ResolveTarget(string? target, string? secondary, int thresholdPercent,
        string? text)
    {
        string primary = Normalize(target);
        string second = NormalizeSecondary(secondary);
        if (second.Length == 0 || SameLanguage(second, primary)) return primary;

        DetectedLanguage detected = DetectDetailed(text);
        if (detected.Key == Auto) return primary;
        if (detected.Confidence * 100 < NormalizeThreshold(thresholdPercent)) return primary;

        return SameLanguage(detected.Key, primary) ? second : primary;
    }
}

/// <summary>语言检测结果（语言键 + 置信度 0–1）。</summary>
internal sealed record DetectedLanguage(string Key, double Confidence);

/// <summary>一门语言：配置键 / 显示名 / 提示词写法。</summary>
internal sealed record TranslationLanguage(string Key, string Label, string Prompt);
