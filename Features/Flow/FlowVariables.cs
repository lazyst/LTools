using System;
using System.Collections.Generic;

namespace LTools.Features;

/// <summary>
/// 收集一条流程里用到的自定义变量名（「设置变量」的目标、条件「存回变量」、循环下标、动作输出变量），
/// 供编辑器变量面板展示。
/// </summary>
/// <remarks>只做静态遍历，不关心当前是否运行；同名（大小写不敏感）只保留第一次出现的写法。</remarks>
internal static class FlowVariables
{
    /// <summary>按流程顺序返回变量名（含嵌套块）。</summary>
    public static List<string> Collect(IEnumerable<FlowNode>? root)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(root);
        return names;

        void Add(string? name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) return;
            names.Add(name);
        }

        void Walk(IEnumerable<FlowNode>? nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                switch (node.Kind)
                {
                    case FlowNodeKind.action:
                        Add(node.Action?.OutputVar);
                        break;

                    case FlowNodeKind.set:
                        Add(node.Set?.Var);
                        break;

                    case FlowNodeKind.@if when node.If != null:
                        Add(node.If.Condition?.Bind);
                        Walk(node.If.Then);
                        Walk(node.If.Else);
                        break;

                    case FlowNodeKind.loop when node.Loop != null:
                        Add(node.Loop.IndexVar);
                        Add(node.Loop.While?.Bind);
                        Walk(node.Loop.Body);
                        break;
                }
            }
        }
    }
}
