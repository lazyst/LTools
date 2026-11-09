using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTools.Features.Builtin;

/// <summary>
/// 绑定引用的编解码：动作就是 <c>a1</c>，内置功能是 <c>common.volume#steps=3</c>。
/// </summary>
/// <remarks>
/// <para>
/// 参数放在 <c>#</c> 后面、按 <c>key=value&amp;…</c> 写，值做 URL 转义（区域里的逗号也能安全存）。
/// 这样面板 / 快捷键 / 悬浮的存储不用改格式——它们一直只存一个字符串。
/// </para>
/// <para>
/// 动作 Id 由 <c>ActionRegistry.NextId</c> 生成（<c>a1</c> / <c>a2</c>…），不会带 <c>#</c>，
/// 所以「有 <c>#</c> 就是功能引用」这条判断是稳定的。
/// </para>
/// </remarks>
internal static class FeatureRef
{
    public const char Separator = '#';

    /// <summary>引用里 <c>#</c> 之前的功能 Id / 动作 Id。</summary>
    public static string IdOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return "";
        int i = reference.IndexOf(Separator);
        return i < 0 ? reference : reference[..i];
    }

    /// <summary>解析 <c>#</c> 后面的参数；没有参数返回空表。</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? reference)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(reference)) return values;

        int i = reference.IndexOf(Separator);
        if (i < 0 || i == reference.Length - 1) return values;

        foreach (string pair in reference[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq]);
            string value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key.Length > 0) values[key] = value;
        }
        return values;
    }

    /// <summary>拼一个引用；没有参数时只返回 Id。</summary>
    public static string Build(string id, IEnumerable<KeyValuePair<string, string>>? values)
    {
        if (values == null) return id;

        var parts = new List<string>();
        foreach (var pair in values)
        {
            if (pair.Key.Length == 0) continue;
            parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? ""));
        }
        return parts.Count == 0 ? id : id + Separator + string.Join("&", parts);
    }

    /// <summary>只替换参数、保留 Id（界面改参数时用）。</summary>
    public static string WithValues(string reference, IEnumerable<KeyValuePair<string, string>> values)
        => Build(IdOf(reference), values);
}
