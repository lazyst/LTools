using System;
using System.Collections.Generic;

namespace LTools.Core.Actions;

/// <summary>
/// 「复制为副本」的命名：<c>X 副本</c> / <c>X 副本 2</c> / <c>X 副本 3</c>…
/// </summary>
/// <remarks>
/// <para>
/// 动作名在本项目里不强制唯一，但连着复制三次得到三个同名动作时，面板、快捷键、触发规则里
/// 就再也挑不出谁是谁。所以副本一律带后缀，且按<b>现有名字</b>往后编（大小写不敏感）。
/// </para>
/// <para>
/// 复制「副本」本身要剥掉结尾的「副本 / 副本 N」再编号：<c>X 副本</c> 再复制是
/// <c>X 副本 2</c>，不是 <c>X 副本 副本</c>。
/// </para>
/// </remarks>
internal static class CopyNaming
{
    /// <summary>副本后缀（带前导空格）。</summary>
    public const string Suffix = " 副本";

    /// <summary>取一个没被占用的副本名。</summary>
    /// <param name="name">源动作名。</param>
    /// <param name="taken">现有名字（可为 null）；按 <see cref="StringComparer.OrdinalIgnoreCase"/> 判重。</param>
    public static string Next(string name, IEnumerable<string>? taken = null)
    {
        string root = Trim(name);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 源名字本身也算占着：复制「X 副本」时不能又叫「X 副本」（等于没换名）
        if (!string.IsNullOrWhiteSpace(name)) used.Add(name.Trim());
        if (taken != null)
        {
            foreach (string n in taken)
                if (!string.IsNullOrWhiteSpace(n)) used.Add(n.Trim());
        }

        string first = root + Suffix;
        if (!used.Contains(first)) return first;

        for (int n = 2; n < 1000; n++)
        {
            string candidate = $"{root}{Suffix} {n}";
            if (!used.Contains(candidate)) return candidate;
        }
        return first;
    }

    /// <summary>把结尾的「副本 / 副本 N」剥掉；本来就不是副本名就原样（去空白）返回。</summary>
    public static string Trim(string name)
    {
        string s = (name ?? "").Trim();
        for (int guard = 0; guard < 8 && s.Length > 0; guard++)
        {
            if (TryCutNumbered(s) is { } numbered) { s = numbered; continue; }
            if (s.EndsWith(Suffix, StringComparison.Ordinal))
            {
                s = s[..^Suffix.Length].TrimEnd();
                continue;
            }
            break;
        }
        return s.Length == 0 ? "未命名动作" : s;
    }

    /// <summary>结尾是「 副本 N」（N 全是数字）时返回剥掉后的名字。</summary>
    private static string? TryCutNumbered(string s)
    {
        string prefix = Suffix + " ";
        int at = s.LastIndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return null;

        ReadOnlySpan<char> digits = s.AsSpan(at + prefix.Length);
        if (digits.Length == 0) return null;
        foreach (char c in digits)
            if (c < '0' || c > '9') return null;

        return s[..at].TrimEnd();
    }
}
