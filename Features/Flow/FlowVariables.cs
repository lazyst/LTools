using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features;

/// <summary>
/// 一条流程变量及它可用的属性后缀（如图像匹配的 <c>m.x</c> / <c>m.found</c> / <c>m.score</c>）。
/// </summary>
public sealed record VariableInfo(string Name, IReadOnlyList<string> Properties);

/// <summary>
/// 收集一条流程里用到的自定义变量名（「设置变量」的目标、条件「存回变量」、循环下标、动作输出变量），
/// 供编辑器变量面板展示。
/// </summary>
/// <remarks>
/// 只做静态遍历，不关心当前是否运行；同名（大小写不敏感）只保留第一次出现的写法。
/// 同时记录每个变量可能带的结构化属性，变量面板才能把 <c>${m.found}</c> 这类写法也列出来。
/// </remarks>
internal static class FlowVariables
{
    /// <summary>按流程顺序返回变量名（含嵌套块）。</summary>
    public static List<string> Collect(IEnumerable<FlowNode>? root) =>
        CollectInfos(root).Select(v => v.Name).ToList();

    /// <summary>按流程顺序返回变量名及其可用属性（可插入的写法见 <see cref="ExpandPaths"/>）。</summary>
    public static List<VariableInfo> CollectInfos(IEnumerable<FlowNode>? root)
    {
        var names = new List<string>();
        var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Walk(root);

        return names.Select(n => new VariableInfo(n, properties[n])).ToList();

        void Add(string? name, IReadOnlyList<string>? extra = null)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (!properties.TryGetValue(name, out var list))
            {
                list = new List<string>();
                properties[name] = list;
                names.Add(name);
            }
            if (extra == null) return;
            foreach (string prop in extra)
                if (!list.Contains(prop, StringComparer.OrdinalIgnoreCase))
                    list.Add(prop);
        }

        void AddProbe(string? name, Probe? probe) => Add(name, ProbeProperties(probe));

        void Walk(IEnumerable<FlowNode>? nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                switch (node.Kind)
                {
                    case FlowNodeKind.action:
                        Add(node.Action?.OutputVar,
                            node.Action != null ? ActionResults.Properties(node.Action.Type) : null);
                        break;

                    case FlowNodeKind.set:
                        AddProbe(node.Set?.Var, node.Set?.Probe);
                        break;

                    case FlowNodeKind.@if when node.If != null:
                        AddProbe(node.If.Condition?.Bind, node.If.Condition?.Probe);
                        Walk(node.If.Then);
                        Walk(node.If.Else);
                        break;

                    case FlowNodeKind.loop when node.Loop != null:
                        Add(node.Loop.IndexVar);
                        AddProbe(node.Loop.While?.Bind, node.Loop.While?.Probe);
                        Walk(node.Loop.Body);
                        break;
                }
            }
        }
    }

    /// <summary>把变量展开成可插入的写法：先变量本身，再各属性（<c>m</c>、<c>m.x</c>、<c>m.found</c>…）。</summary>
    public static List<string> ExpandPaths(IEnumerable<VariableInfo> infos)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            if (seen.Add(info.Name)) paths.Add(info.Name);
            foreach (string prop in info.Properties)
            {
                string path = info.Name + "." + prop;
                if (seen.Add(path)) paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>某类取值器产出的结构化属性（与 <see cref="VarValue.Property"/> 支持的名称一致）。</summary>
    private static IReadOnlyList<string> ProbeProperties(Probe? probe) => probe?.Kind switch
    {
        ProbeKind.image => new[] { "x", "y", "found", "score" },
        ProbeKind.ocr => new[] { "found", "score" },
        ProbeKind.textSearch => new[] { "x", "y", "found", "text" },
        ProbeKind.note => new[] { "title", "time", "path", "count", "found" },
        ProbeKind.screenshot => new[] { "x", "y", "w", "h", "cx", "cy", "file", "found" },
        ProbeKind.user => new[] { "found" },
        ProbeKind.cursor => new[] { "x", "y" },
        ProbeKind.window => new[] { "found", "number", "text" },
        ProbeKind.process => new[] { "found", "number" },
        _ => Array.Empty<string>(),
    };
}
