using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Core;

/// <summary>
/// 设置里的一条可搜索项：纯数据，不含控件引用（便于 <c>--selftest</c> 直接断言）。
/// </summary>
/// <remarks>
/// <see cref="Tag"/> 是导航 tag；同一页里的 <see cref="Group"/> 是卡片 / 分组标题；
/// <see cref="Label"/> 是显示名（CheckBox 的文字、行标签、按钮文字…）。
/// 页面标题由调用方从导航读出后传进来（不在 Core 里再维护一份中文名表）。
/// </remarks>
internal sealed class SettingsEntry
{
    public SettingsEntry(string tag, string page, string group, string label, int order, string keywords = "")
    {
        Tag = tag;
        Page = page;
        Group = group;
        Label = label;
        Order = order;
        Keywords = keywords;
        LabelKey = SettingsSearch.Normalize(label);
        GroupKey = SettingsSearch.Normalize(group);
        PageKey = SettingsSearch.Normalize(page);
        KeywordKey = SettingsSearch.Normalize(keywords);
    }

    /// <summary>导航 tag（如 <c>macros-settings</c>）。</summary>
    public string Tag { get; }

    /// <summary>页面显示名（如「键鼠宏 · 设置」）。</summary>
    public string Page { get; }

    /// <summary>卡片 / 分组标题（可能为空）。</summary>
    public string Group { get; }

    /// <summary>条目文字。</summary>
    public string Label { get; }

    /// <summary>额外别名（空格分隔，可选）。</summary>
    public string Keywords { get; }

    /// <summary>页面内顺序，用于稳定排序。</summary>
    public int Order { get; }

    /// <summary>规范化后的文本（只算一次，匹配循环里直接比）。</summary>
    public string LabelKey { get; }

    public string GroupKey { get; }

    public string PageKey { get; }

    public string KeywordKey { get; }

    public string Display => Label;

    /// <summary>结果列表第二行：页面 › 分组。</summary>
    public string Hint => Group.Length == 0 ? Page : Page + " › " + Group;
}

/// <summary>一条命中结果：条目 + 分数（越大越相关）。</summary>
internal readonly record struct SettingsHit(SettingsEntry Entry, int Score);

/// <summary>
/// 设置搜索的匹配规则（纯函数）。
/// </summary>
/// <remarks>
/// <para>
/// 界面文字就是索引，**不维护「某某设置在哪里」的手工清单**：新增一行设置、改一个标签，
/// 下次打开搜索就自动能搜到。这里只负责「怎么算命中」，索引由
/// <c>Views/ConfigHelper.Search.cs</c> 从页面的逻辑树里抓。
/// </para>
/// <para>
/// 打分只分五档：标签开头 &gt; 标签包含 &gt; 别名 &gt; 卡片标题 &gt; 页面名。
/// 查询按空格拆词，每个词都必须命中（AND），分数取各词最高档的平均——这样
/// 「宏 停止」会同时收窄到宏页里与停止相关的项，而不是把整个宏页顶上来。
/// </para>
/// </remarks>
internal static class SettingsSearch
{
    public const int MaxResults = 40;

    /// <summary>去掉首尾空白、合并连续空白、转小写；全角空格也算分隔符。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            char ch = c == '\u3000' ? ' ' : c;
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>按查询筛条目并排序；空查询返回空列表（不显示「全部设置」）。</summary>
    public static IReadOnlyList<SettingsHit> Match(IEnumerable<SettingsEntry> entries, string? query, int max = MaxResults)
    {
        string normalized = Normalize(query);
        if (normalized.Length == 0) return Array.Empty<SettingsHit>();

        string[] terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SettingsHit>();
        foreach (SettingsEntry entry in entries)
        {
            int score = ScoreOf(entry, terms);
            if (score > 0) hits.Add(new SettingsHit(entry, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Order)
            .ThenBy(h => h.Entry.Page, StringComparer.Ordinal)
            // 同页同名（比如两组各有「启用」）只留第一条，别让结果列表出现两行一样的
            .DistinctBy(h => h.Entry.Tag + "\u0001" + h.Entry.Label)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>0 = 不命中；越大越相关。</summary>
    public static int ScoreOf(SettingsEntry entry, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        int total = 0;
        foreach (string term in terms)
        {
            int best = 0;
            if (entry.LabelKey.StartsWith(term, StringComparison.Ordinal)) best = 100;
            else if (entry.LabelKey.Contains(term, StringComparison.Ordinal)) best = 85;
            else if (entry.KeywordKey.Contains(term, StringComparison.Ordinal)) best = 70;
            else if (entry.GroupKey.Contains(term, StringComparison.Ordinal)) best = 55;
            else if (entry.PageKey.Contains(term, StringComparison.Ordinal)) best = 45;
            if (best == 0) return 0;   // 有一个词落空就不算命中（AND）
            total += best;
        }
        return total / terms.Count;
    }

    /// <summary>
    /// 少数「界面叫法 ≠ 用户口头说法」的条目补几个别名。**只补确实会搜错的**，
    /// 别给每条都写一遍——界面词本身已经能搜到。
    /// </summary>
    public static string AliasesFor(string label) => label switch
    {
        "停止运行热键" => "停止 暂停 中止 中断 abort stop",
        "录制 / 停止热键" => "录制 record",
        "左键双击" => "托盘 tray",
        _ => "",
    };
}
