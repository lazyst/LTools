using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LTools.Features.CapturePro;

/// <summary>文件名模板里的「哪个程序的哪个窗口」（<c>{AN}</c> / <c>{WN}</c>）。</summary>
public readonly record struct ExportNameContext(string Application, string Window)
{
    public static readonly ExportNameContext Empty = new("", "");

    public static ExportNameContext Of(string? application, string? window) =>
        new((application ?? "").Trim(), (window ?? "").Trim());

    public bool IsEmpty => Application.Length == 0 && Window.Length == 0;
}

/// <summary>
/// 截图文件名模板：<c>{YYYY-MM-DD_HH-mm-ss}</c> 这种时间变量 + <c>{AN}</c> / <c>{WN}</c>。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>storage/exportfilename.cpp</c>、<c>storage/timevariables.cpp</c>、
/// <c>storage/screenshotexportvariables.cpp</c>：花括号里是 Qt 的日期格式（<c>YYYY</c> / <c>MM</c> /
/// <c>DD</c> / <c>HH</c> / <c>mm</c> / <c>ss</c>…），<c>{AN}</c> = 抓图时前台程序的进程名、
/// <c>{WN}</c> = 窗口标题。文件名里不能出现的字符换 <c>_</c>，每个路径片段按 UTF-8 长度封顶
/// （文件名 248 字节，目录 255 字节，给原子写留出 <c>.XXXXXX</c>），末尾的点和空格去掉，
/// Windows 的保留设备名（CON / NUL / COM1…）前面补 <c>_</c>。
/// </para>
/// <para>
/// Qt 与 .NET 的日期格式只有三处不同：<c>YYYY</c>→<c>yyyy</c>、<c>YY</c>→<c>yy</c>、
/// <c>DD</c>→<c>dd</c>，另外 <c>AP</c> / <c>ap</c>（上午 / 下午）在 .NET 里是 <c>tt</c>。
/// 其余（<c>MM</c> 月 / <c>mm</c> 分 / <c>HH</c> / <c>hh</c> / <c>ss</c> / <c>zzz</c> / <c>ddd</c>）
/// 两边含义一致，原样交给 <see cref="DateTime.ToString(string, IFormatProvider)"/>。
/// </para>
/// </remarks>
internal static class ExportNames
{
    /// <summary>路径片段（目录）的 UTF-8 上限。</summary>
    public const int MaxComponentBytes = 255;

    /// <summary>文件名的 UTF-8 上限（留出 7 字节给 <c>.XXXXXX</c> 原子写）。</summary>
    public const int MaxFileBytes = MaxComponentBytes - 7;

    private static readonly Regex Placeholder =
        new(@"\{([^{}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Reserved =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 展开模板（<paramref name="file"/> = true 按文件名封顶 248 字节，
    /// 展开出来带目录的按目录 255 字节封顶）。
    /// </summary>
    public static string Expand(string template, DateTime time, ExportNameContext names, bool file = true)
    {
        string input = (template ?? "").Trim();
        if (input.Length == 0) return "";

        var text = new StringBuilder(input.Length + 16);
        int at = 0;
        foreach (Match match in Placeholder.Matches(input))
        {
            text.Append(input, at, match.Index - at);
            text.Append(Resolve(match.Groups[1].Value, time, names));
            at = match.Index + match.Length;
        }
        text.Append(input, at, input.Length - at);

        return Bound(text.ToString(), file);
    }

    /// <summary>文件名（不含扩展名）：模板展开 + 清洗 + 保留设备名补 <c>_</c>。</summary>
    public static string FileStem(string template, DateTime time, ExportNameContext names)
    {
        string expanded = Expand(template, time, names);
        string name = Sanitize(expanded, "截图");
        return Reserved.IsMatch(name) ? "_" + name : name;
    }

    /// <summary>把一个名字片段清洗成 Windows 能落盘的文件名（空 → <paramref name="fallback"/>）。</summary>
    public static string Sanitize(string? name, string fallback)
    {
        string text = name ?? "";
        var buffer = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            ushort value = ch;
            bool bad = value < 0x20 || (value >= 0x7f && value <= 0x9f) ||
                       "<>:\"/\\|?*".IndexOf(ch) >= 0;
            buffer.Append(bad ? '_' : ch);
        }

        string result = Truncate(buffer.ToString().Trim(), MaxFileBytes);
        while (result.EndsWith('.') || result.EndsWith(' '))
            result = result[..^1];
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>
    /// 文件名 = 主名 + 后缀，整体不超过 <paramref name="maxBytes"/> 个 UTF-8 字节。
    /// 只截主名（后缀是扩展名，截了就没用了）。
    /// </summary>
    public static string Compose(string stem, string suffix, int maxBytes = MaxFileBytes)
    {
        string tail = suffix ?? "";
        int tailBytes = Encoding.UTF8.GetByteCount(tail);
        int room = Math.Max(0, maxBytes - tailBytes);

        int original = stem.Length;
        string head = Truncate(stem, room);
        if (head.Length != original)
        {
            while (head.EndsWith('.') || head.EndsWith(' '))
                head = head[..^1];
        }
        return head + tail;
    }

    /// <summary>按 UTF-8 字节数截断，不切断代理对（emoji 不会变成半个字符）。</summary>
    public static string Truncate(string text, int maxBytes)
    {
        if (maxBytes <= 0 || text.Length == 0) return maxBytes <= 0 ? "" : text;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        int bytes = 0;
        int index = 0;
        while (index < text.Length)
        {
            int size = char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                ? 4
                : Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
            if (bytes + size > maxBytes) break;
            bytes += size;
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
        }

        string result = text[..index];
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return result;
    }

    // ============ 内部 ============

    /// <summary>一个花括号里的东西：<c>AN</c> / <c>WN</c> / 日期格式。</summary>
    private static string Resolve(string token, DateTime time, ExportNameContext names)
    {
        if (token == "AN")
            return Component(string.IsNullOrWhiteSpace(names.Application) ? "Unknown" : names.Application);
        if (token == "WN")
            return Component(string.IsNullOrWhiteSpace(names.Window) ? "Unknown" : names.Window);

        string format = TimeFormat(token);
        if (format.Length == 0) return "";
        try { return time.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return token; }
    }

    /// <summary>窗口名 / 进程名当目录片段用：清洗 + 封顶（别让一个长标题把路径撑爆）。</summary>
    private static string Component(string value)
    {
        string text = Sanitize(value, "Unknown");
        return Reserved.IsMatch(text) ? "_" + text : text;
    }

    /// <summary>Qt 的日期格式 → .NET 的日期格式（只处理真正不同的那几个记号）。</summary>
    internal static string TimeFormat(string qt)
    {
        var result = new StringBuilder(qt.Length + 4);
        for (int i = 0; i < qt.Length; i++)
        {
            char ch = qt[i];

            // AP / ap（上午 / 下午）→ tt
            if ((ch == 'A' || ch == 'a') && i + 1 < qt.Length && (qt[i + 1] == 'P' || qt[i + 1] == 'p'))
            {
                result.Append("tt");
                i++;
                continue;
            }

            if (ch is 'Y' or 'D')
            {
                int run = 1;
                while (i + run < qt.Length && qt[i + run] == ch) run++;
                result.Append(char.ToLowerInvariant(ch), run);
                i += run - 1;
                continue;
            }

            // z 在 Qt 里是毫秒，在 .NET 里是时区偏移 —— 统一按毫秒写（zzz → fff）
            if (ch is 'z' or 'Z')
            {
                while (i + 1 < qt.Length && (qt[i + 1] == 'z' || qt[i + 1] == 'Z')) i++;
                result.Append("fff");
                continue;
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    /// <summary>把展开结果按路径片段逐个封顶（<c>/</c> 与 <c>\</c> 都算分隔符）。</summary>
    private static string Bound(string text, bool file)
    {
        var bounded = new StringBuilder(text.Length);
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '/' && text[i] != '\\') continue;

            string part = text[start..i];
            bool last = i == text.Length;
            int limit = file && last ? MaxFileBytes : MaxComponentBytes;
            bounded.Append(Truncate(part, limit));
            if (!last) bounded.Append(text[i]);
            start = i + 1;
        }
        return bounded.ToString();
    }

    /// <summary>模板里用到的变量（设置页的说明文字用）。</summary>
    public static IReadOnlyList<(string Token, string Meaning)> Tokens { get; } = new[]
    {
        ("{YYYY}", "四位年份"),
        ("{YY}", "两位年份"),
        ("{MM}", "月（01–12）"),
        ("{DD}", "日（01–31）"),
        ("{HH}", "小时（00–23）"),
        ("{mm}", "分钟（00–59）"),
        ("{ss}", "秒（00–59）"),
        ("{zzz}", "毫秒"),
        ("{AN}", "前台程序的进程名"),
        ("{WN}", "前台窗口标题"),
    };
}
