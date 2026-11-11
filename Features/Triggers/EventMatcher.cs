using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LTools.Config;

namespace LTools.Features;

/// <summary>
/// 事件规则的条件匹配（§13.3 / §13.5）：事件类型 + <c>Params</c>（键之间 AND，
/// 值内分号多值 OR，单项支持 <c>regex:</c> 前缀，否则按包含匹配）。
/// 三个事件源共用这一套，别各写一份。作用范围不在这里判——统一由
/// <see cref="TriggerService"/> 的投递检查处理（避免两套语义）。
/// </summary>
internal static class EventMatcher
{
    /// <summary>事件类型是否一致，且 <c>Params</c> 里每个非空条件都命中。</summary>
    public static bool Matches(TriggerRule rule, TriggerEvent ev)
    {
        if (rule.Kind != TriggerKind.@event) return false;
        if (!string.Equals(rule.EventType, ev.EventType, StringComparison.OrdinalIgnoreCase))
            return false;

        // 默认排除 LTools 自己引起的事件（excludeOwn / ignoreOwnWrites 默认 true）。
        // 提到 Params 循环之外：用户**没填参数**时也要生效，不能只在写了键时才排除。
        if (ev.OwnEvent && !AllowsOwn(rule)) return false;

        if (rule.Params == null || rule.Params.Count == 0) return true;

        foreach (var (key, pattern) in rule.Params)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;   // 空值 = 该项不限制

            // 这些键由事件源消费（或上面的 AllowsOwn 判定），不参与条件匹配；
            // 拿它们去 ev.Fields 里找对应值，规则会永远匹配不上。
            if (IsControlKey(key)) continue;

            // 不依赖事件源传入字典的比较器：键按忽略大小写找。
            string actual = "";
            foreach (var (k, v) in ev.Fields)
                if (Eq(k, key)) { actual = v ?? ""; break; }

            // §13.5：剪贴板非文本格式时 text 条件「不参与」（不阻止命中）；
            // 想只监听图片 / 文件就用 formats 条件。
            if (Eq(rule.EventType ?? "", "clipboard.change") && Eq(key, "text")
                && actual.Length == 0) continue;

            if (!MatchValue(pattern, actual)) return false;
        }
        return true;
    }

    /// <summary>
    /// 由事件源消费、不参与条件匹配的 <c>Params</c> 键：三个布尔开关，加上文件事件源的
    /// <c>path</c>（监听目录）/ <c>filter</c>（watcher.Filter）。它们不是「事件自身的条件」。
    /// </summary>
    private static bool IsControlKey(string key) =>
        Eq(key, "excludeOwn") || Eq(key, "ignoreOwnWrites")
        || Eq(key, "includeSubdirectories") || Eq(key, "path") || Eq(key, "filter");

    /// <summary>规则是否**显式**允许自己引起的事件；没写参数 = 默认排除。</summary>
    private static bool AllowsOwn(TriggerRule rule)
    {
        string type = rule.EventType ?? "";
        string? key = Eq(type, "window.foreground") ? "excludeOwn"
            : Eq(type, "clipboard.change") ? "ignoreOwnWrites"
            : null;
        if (key == null || rule.Params == null) return false;
        foreach (var (k, v) in rule.Params)
            if (Eq(k, key))
                return string.Equals((v ?? "").Trim(), "false", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>分号多值 = OR；<c>regex:</c> 走正则（带超时），其余按包含（忽略大小写）。</summary>
    private static bool MatchValue(string pattern, string actual)
    {
        foreach (string raw in pattern.Split(';'))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;

            if (p.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                string expr = p["regex:".Length..];
                if (expr.Length == 0) continue;
                try
                {
                    if (Regex.IsMatch(actual, expr,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100)))
                        return true;
                }
                catch (ArgumentException) { /* 非法正则按「不匹配」处理，不让一条坏规则炸掉事件源 */ }
                catch (RegexMatchTimeoutException) { /* 超时同样按不匹配 */ }
            }
            else if (actual.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
