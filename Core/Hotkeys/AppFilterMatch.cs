using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Native;

namespace LTools.Core;

/// <summary>
/// 快捷键的白 / 黑名单判定（纯字符串比较，可在钩子回调里直接跑）。
/// </summary>
/// <remarks>
/// <para>
/// 「取不到前台程序」时一律放行：宁可按了有效，也不要按了没反应。
/// LTools 自己的窗口（设置 / 面板 / 编辑器 / 速记…）一律不触发，免得在设置里按 Ctrl+S 跑动作。
/// </para>
/// <para>
/// 没写通配符的规则按「包含」算：进程填 <c>chrome</c> 能命中 <c>chrome.exe</c>，
/// 标题填「哔哩哔哩」能命中「【某某】 - 哔哩哔哩」。
/// </para>
/// </remarks>
internal static class AppFilterMatch
{
    public static bool Allows(HotkeyBinding binding, HotkeyFile file, ForegroundApp? app)
    {
        if (app == null) return true;
        // 自己的窗口不触发：在设置 / 编辑器里按 Ctrl+S 不该跑动作，也避免动作注入的按键回环。
        if (ForegroundApp.IsOwnWindow(app.Hwnd)) return false;

        return binding.Scope switch
        {
            HotkeyScope.all => true,
            HotkeyScope.whitelist => Matches(binding.Filter ?? file.DefaultFilter, app),
            HotkeyScope.blacklist => !Matches(binding.Filter ?? file.DefaultFilter, app),
            _ => file.DefaultScope switch
            {
                HotkeyScope.whitelist => Matches(file.DefaultFilter, app),
                HotkeyScope.blacklist => !Matches(file.DefaultFilter, app),
                _ => true,
            },
        };
    }

    static bool Matches(AppFilter? filter, ForegroundApp app)
    {
        if (filter == null || filter.IsEmpty) return false;

        foreach (string pattern in filter.Processes)
        {
            if (Wildcard(pattern, app.ExeName)) return true;
            if (app.ExePath.Length > 0 && Wildcard(pattern, app.ExePath)) return true;
        }
        foreach (string pattern in filter.Titles)
            if (Wildcard(pattern, app.Title)) return true;
        foreach (string pattern in filter.Classes)
            if (Wildcard(pattern, app.ClassName)) return true;
        return false;
    }

    /// <summary>通配符匹配（<c>*</c> 任意长度、<c>?</c> 单个字符，不区分大小写）；没通配符时按包含算。</summary>
    internal static bool Wildcard(string? pattern, string? text)
    {
        pattern = (pattern ?? "").Trim();
        text ??= "";
        if (pattern.Length == 0 || text.Length == 0) return false;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else return false;
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>把一条规则规整成列表里的展示文本（空规则返回 null）。</summary>
    public static string? Describe(AppFilter? filter)
    {
        if (filter == null || filter.IsEmpty) return null;
        var parts = new List<string>();
        parts.AddRange(filter.Processes);
        parts.AddRange(filter.Titles.Select(t => "标题:" + t));
        parts.AddRange(filter.Classes.Select(c => "类名:" + c));
        return string.Join("，", parts);
    }

    /// <summary>一条绑定的生效范围：列表里显示的短标签（长名单会被列宽截断，全文见 <see cref="DescribeScopeLong"/>）。</summary>
    public static string DescribeScope(HotkeyBinding binding, HotkeyFile file)
    {
        return binding.Scope switch
        {
            HotkeyScope.all => "全部窗口",
            HotkeyScope.whitelist => "仅 " + (Describe(binding.Filter) ?? "白名单（空）"),
            HotkeyScope.blacklist => "排除 " + (Describe(binding.Filter) ?? "黑名单（空）"),
            _ => file.DefaultScope switch
            {
                HotkeyScope.whitelist => "默认·白名单",
                HotkeyScope.blacklist => "默认·黑名单",
                _ => "跟随默认",
            },
        };
    }

    /// <summary>同一条范围说明的全文（列表里当 ToolTip，不截断）。</summary>
    public static string DescribeScopeLong(HotkeyBinding binding, HotkeyFile file)
    {
        return binding.Scope switch
        {
            HotkeyScope.all => "全部窗口：不受下方「全局默认名单」限制",
            HotkeyScope.whitelist => "仅在匹配的窗口生效：" + (Describe(binding.Filter) ?? "（规则为空，等于不生效）"),
            HotkeyScope.blacklist => "除匹配的窗口外都生效：" + (Describe(binding.Filter) ?? "（规则为空，等于全部窗口）"),
            _ => file.DefaultScope switch
            {
                HotkeyScope.whitelist => "跟随全局默认名单 → 仅在匹配的窗口生效："
                    + (Describe(file.DefaultFilter) ?? "（规则为空）"),
                HotkeyScope.blacklist => "跟随全局默认名单 → 除匹配的窗口外都生效："
                    + (Describe(file.DefaultFilter) ?? "（规则为空）"),
                _ => "跟随全局默认名单 → 全部窗口",
            },
        };
    }
}
