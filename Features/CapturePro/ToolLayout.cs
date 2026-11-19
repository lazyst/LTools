using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 工具栏的布局：一格可以放几个工具（再点一次在同一格里循环切换），不用的工具可以收起来。
/// </summary>
/// <remarks>
/// <para>
/// 纯数据 + 纯函数（<c>--selftest</c> 直接断言），**落盘只有两件事**：每格是哪些工具
/// （一行一格、格内逗号分隔工具键）+ 被收起来的工具键。别把「隐藏」写成「从布局里删掉」——
/// 那样分不清「用户藏了它」和「这个版本新加了一个工具」，新版工具会被老布局一直藏着。
/// </para>
/// <para>
/// 目录里**新增**、两处都没提到的工具由 <see cref="Parse"/> 自动补回（可见、按默认顺序落位），
/// 所以新增工具不用改配置文件；反过来，认不出的键（工具被删 / 改名）直接丢一边，
/// 不做兼容、也不留别名。
/// </para>
/// </remarks>
internal sealed class ToolLayout
{
    private readonly List<List<CaptureTool>> _groups;
    private readonly HashSet<CaptureTool> _hidden;

    private ToolLayout(List<List<CaptureTool>> groups, HashSet<CaptureTool> hidden)
    {
        _groups = groups;
        _hidden = hidden;
    }

    /// <summary>每格的工具（格内顺序就是循环切换的顺序）。</summary>
    public IReadOnlyList<IReadOnlyList<CaptureTool>> Groups => _groups;

    /// <summary>
    /// 默认布局（照 Snow Shot 的 <c>toolbar_layout::defaultOrder()</c>，一格一个按钮）：
    /// 选择 ｜ 形状、气泡、箭头、测距、测角、括号、直线、画笔、荧光（矩形 / 画笔）、聚光、放大镜、
    /// 文字、序号、滤镜（矩形 / 画笔 / 自动 / 马赛克）、橡皮（点选 / 矩形 / 画笔）、水印 ｜ 撤销、重做。
    /// </summary>
    /// <remarks>
    /// 上游把「同族变体」收在一个按钮里（<c>highlighter</c> = 矩形 / 画笔荧光、<c>filter</c> = 矩形 /
    /// 画笔 / 自动滤镜、<c>eraser</c> = 点选 / 矩形 / 画笔橡皮），这里用同一格循环切换对齐它；
    /// 其余工具**各自一格**（别把箭头和直线、聚光和放大镜合成一格——上游是分开的）。
    /// 上游没有的扩展工具（折线、取色）默认隐藏，仍能在「工具条」设置里加回来。
    /// </remarks>
    public static ToolLayout Default()
    {
        var groups = new List<List<CaptureTool>>
        {
            new() { CaptureTool.Select },
            new() { CaptureTool.Shape },
            new() { CaptureTool.SpeechBubble },
            new() { CaptureTool.Arrow },
            new() { CaptureTool.Distance },
            new() { CaptureTool.Angle },
            new() { CaptureTool.Bracket },
            new() { CaptureTool.Line },
            new() { CaptureTool.Pen },
            new() { CaptureTool.Highlighter, CaptureTool.HighlightRect },
            new() { CaptureTool.Spotlight },
            new() { CaptureTool.Magnifier },
            new() { CaptureTool.Text },
            new() { CaptureTool.Serial },
            new() { CaptureTool.Mosaic, CaptureTool.Filter, CaptureTool.BrushFilter, CaptureTool.AutoFilter },
            new() { CaptureTool.Eraser, CaptureTool.RectEraser, CaptureTool.BrushEraser },
            new() { CaptureTool.Watermark },
        };

        var hidden = new HashSet<CaptureTool> { CaptureTool.Polyline, CaptureTool.Picker };
        var seen = groups.SelectMany(g => g).ToHashSet();
        foreach (var info in ToolCatalog.All)
            if (seen.Add(info.Tool) && !hidden.Contains(info.Tool))
                groups.Add(new List<CaptureTool> { info.Tool });

        return new ToolLayout(groups, hidden);
    }

    /// <summary>被收起来的工具（按目录顺序，菜单里照着列）。</summary>
    public List<CaptureTool> HiddenTools =>
        ToolCatalog.All.Where(i => _hidden.Contains(i.Tool)).Select(i => i.Tool).ToList();

    public bool IsHidden(CaptureTool tool) => _hidden.Contains(tool);

    /// <summary>工具在第几格；-1 = 不在任何格子里（被收起来了）。</summary>
    public int CellOf(CaptureTool tool)
    {
        for (int i = 0; i < _groups.Count; i++)
            if (_groups[i].Contains(tool)) return i;
        return -1;
    }

    /// <summary>把工具收起来：从它所在的那一格移出，格空了整格消失。</summary>
    public ToolLayout Hide(CaptureTool tool)
    {
        var groups = Clone(_groups);
        var hidden = new HashSet<CaptureTool>(_hidden) { tool };

        int at = CellOf(tool);
        if (at >= 0)
        {
            groups[at].Remove(tool);
            if (groups[at].Count == 0) groups.RemoveAt(at);
        }

        return new ToolLayout(groups, hidden);
    }

    /// <summary>把一个被收起来的工具放回去：单独一格，落在默认顺序里它该在的位置上。</summary>
    public ToolLayout Insert(CaptureTool tool)
    {
        if (CellOf(tool) >= 0) return this;

        var groups = Clone(_groups);
        var hidden = new HashSet<CaptureTool>(_hidden);
        hidden.Remove(tool);

        groups.Insert(InsertAt(groups, tool), new List<CaptureTool> { tool });
        return new ToolLayout(groups, hidden);
    }

    /// <summary>把这一格并进左边那格（已经在第一格就不动）；格内顺序保持原样。</summary>
    public ToolLayout MergeLeft(CaptureTool tool)
    {
        int at = CellOf(tool);
        if (at <= 0) return this;

        var groups = Clone(_groups);
        groups[at - 1].AddRange(groups[at]);
        groups.RemoveAt(at);
        return new ToolLayout(groups, new HashSet<CaptureTool>(_hidden));
    }

    /// <summary>把一个工具从多工具的那格里拆出来，单独成一格（插在原格后面）。</summary>
    public ToolLayout Split(CaptureTool tool)
    {
        int at = CellOf(tool);
        if (at < 0 || _groups[at].Count < 2) return this;

        var groups = Clone(_groups);
        groups[at].Remove(tool);
        groups.Insert(at + 1, new List<CaptureTool> { tool });
        return new ToolLayout(groups, new HashSet<CaptureTool>(_hidden));
    }

    /// <summary>落盘形状：一行一格、格内逗号分隔工具键。</summary>
    public List<string> ToLines() =>
        _groups.Select(g => string.Join(",", g.Select(t => t.ToString()))).ToList();

    /// <summary>落盘形状：被收起来的工具键（按目录顺序）。</summary>
    public List<string> ToHidden() =>
        ToolCatalog.All.Where(i => _hidden.Contains(i.Tool)).Select(i => i.Tool.ToString()).ToList();

    /// <summary>两份布局是不是同一件事（自检的往返断言用）。</summary>
    public bool SameAs(ToolLayout other) =>
        _groups.Count == other._groups.Count &&
        _groups.Zip(other._groups).All(pair => pair.First.SequenceEqual(pair.Second)) &&
        _hidden.SetEquals(other._hidden);

    /// <summary>
    /// 读回布局：<paramref name="lines"/> 为空 = 全新安装（按目录默认）。
    /// 未知 / 重复的工具丢掉；目录里新增、两处都没提到的工具补成单独一格（可见）。
    /// </summary>
    public static ToolLayout Parse(IEnumerable<string>? lines, IEnumerable<string>? hiddenLines)
    {
        if (lines == null) return Default();

        var groups = new List<List<CaptureTool>>();
        var seen = new HashSet<CaptureTool>();

        foreach (string line in lines)
        {
            var group = new List<CaptureTool>();
            foreach (string token in (line ?? "").Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParseTool(token, out var tool) || !seen.Add(tool)) continue;
                group.Add(tool);
            }

            if (group.Count > 0) groups.Add(group);
        }

        var hidden = new HashSet<CaptureTool>();
        if (hiddenLines != null)
            foreach (string line in hiddenLines)
                if (TryParseTool((line ?? "").Trim(), out var tool) && !seen.Contains(tool))
                    hidden.Add(tool);

        // 目录里新增的工具：两处都没提到就补成单独一格（否则新功能会被老布局一直藏着）
        var layout = new ToolLayout(groups, hidden);
        foreach (var info in ToolCatalog.All)
            if (layout.CellOf(info.Tool) < 0 && !layout._hidden.Contains(info.Tool))
                layout = layout.Insert(info.Tool);

        return layout;
    }

    /// <summary>按工具名解析（大小写不敏感）；不认数值写法（<c>Enum.TryParse("5")</c> 会给出枚举值）。</summary>
    private static bool TryParseTool(string text, out CaptureTool tool)
    {
        tool = default;
        if (text.Length == 0) return false;

        foreach (string name in Enum.GetNames<CaptureTool>())
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                tool = Enum.Parse<CaptureTool>(name);
                return true;
            }

        return false;
    }

    /// <summary>新的一格该插在哪：默认顺序里第一个排在它后面的工具之前。</summary>
    private static int InsertAt(List<List<CaptureTool>> groups, CaptureTool tool)
    {
        int rank = Rank(tool);
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Count > 0 && Rank(groups[i][0]) > rank) return i;
        return groups.Count;
    }

    private static int Rank(CaptureTool tool)
    {
        for (int i = 0; i < ToolCatalog.All.Length; i++)
            if (ToolCatalog.All[i].Tool == tool) return i;
        return int.MaxValue;
    }

    private static List<List<CaptureTool>> Clone(List<List<CaptureTool>> groups) =>
        groups.Select(g => new List<CaptureTool>(g)).ToList();
}
