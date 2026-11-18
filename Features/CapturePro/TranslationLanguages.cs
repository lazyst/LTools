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
}

/// <summary>一门语言：配置键 / 显示名 / 提示词写法。</summary>
internal sealed record TranslationLanguage(string Key, string Label, string Prompt);
