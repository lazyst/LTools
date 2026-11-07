using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.Panel;

/// <summary>上下文区解析结果。</summary>
/// <param name="Scene">命中的场景；null 表示使用兜底分组。</param>
/// <param name="ShowHint">是否显示「当前程序还没有配置场景」提示。</param>
/// <param name="Blocked">是否命中「禁止弹出」列表。</param>
internal sealed record PanelResolveResult(PanelSceneDto? Scene, bool ShowHint, bool Blocked);

/// <summary>
/// 按前台窗口选择上下文场景（规则见 <c>docs/PANEL_REDESIGN.md</c> §4）。
/// 只在面板唤起时调用一次；钉住期间不跟随前台变化。
/// </summary>
internal static class PanelSceneResolver
{
    private const string LogSource = "Panel";

    public static PanelResolveResult Resolve(PanelConfig panel, ForegroundApp? fg)
    {
        if (fg == null) return new PanelResolveResult(null, false, false);

        if (PanelLibrary.IsBlocked(fg.ExeName, fg.ExePath))
            return new PanelResolveResult(null, false, true);

        var scene = Match(panel, fg);
        bool hint = scene == null && !panel.HintDismissed;
        return new PanelResolveResult(scene, hint, false);
    }

    private static PanelSceneDto? Match(PanelConfig panel, ForegroundApp fg)
    {
        var roots = panel.Context.Scenes
            .Where(s => s.Enabled && string.IsNullOrEmpty(s.ParentId))
            .ToList();
        if (roots.Count == 0) return null;

        // 1) 完整路径完全相等
        var hits = roots.Where(s => PathEquals(s.Match.Path, fg.ExePath)).ToList();
        // 2) 进程名相等
        if (hits.Count == 0)
            hits = roots.Where(s => ExeEquals(s.Match.Exe, fg.ExeName, fg.ExePath)).ToList();
        // 3) 只带窗口条件的根场景
        if (hits.Count == 0)
            hits = roots.Where(s => WindowMatches(s.Match, fg)).ToList();

        if (hits.Count == 0) return null;

        var root = hits[0];
        if (hits.Count > 1)
            AppLog.Warn(LogSource, $"同时匹配多个程序场景：{string.Join("、", hits.Select(x => x.Name))}，已使用「{root.Name}」");

        // 4) 父场景命中后匹配窗口子场景；多个命中回退父场景
        var children = panel.Context.Scenes
            .Where(s => s.Enabled && s.ParentId == root.Id && WindowMatches(s.Match, fg))
            .ToList();
        if (children.Count == 1) return children[0];
        if (children.Count > 1)
        {
            AppLog.Warn(LogSource,
                $"同时匹配多个窗口条件子场景：{string.Join("、", children.Select(x => x.Name))}，已回退父场景「{root.Name}」");
            return root;
        }
        return root;
    }

    private static bool PathEquals(string? path, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(exePath)) return false;
        return string.Equals(path.Trim(), exePath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ExeEquals(string? exe, string? exeName, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        string v = exe.Trim();
        if (!string.IsNullOrEmpty(exeName) && v.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(exePath) && v.Equals(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>窗口条件：标题包含（忽略大小写）+ 类名精确；两者都填时需同时满足。</summary>
    private static bool WindowMatches(PanelMatchDto match, ForegroundApp fg)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(match.TitleContains);
        bool hasClass = !string.IsNullOrWhiteSpace(match.ClassName);
        if (!hasTitle && !hasClass) return false;

        if (hasTitle && !fg.Title.Contains(match.TitleContains!.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (hasClass && !string.Equals(match.ClassName!.Trim(), fg.ClassName, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>给设置页显示一行匹配条件摘要。</summary>
    public static string DescribeMatch(PanelSceneDto scene)
    {
        var m = scene.Match;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Path)) parts.Add("路径 = " + m.Path!.Trim());
        if (!string.IsNullOrWhiteSpace(m.Exe)) parts.Add("进程 = " + m.Exe!.Trim());
        if (!string.IsNullOrWhiteSpace(m.TitleContains)) parts.Add("标题含 " + m.TitleContains!.Trim());
        if (!string.IsNullOrWhiteSpace(m.ClassName)) parts.Add("类名 = " + m.ClassName!.Trim());
        return parts.Count == 0 ? "未设置匹配条件" : string.Join(" · ", parts);
    }
}
