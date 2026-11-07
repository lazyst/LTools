using System.Text;

namespace LTools.Core.Actions;

/// <summary>
/// 极简模板：把 <c>${name}</c> 替换成变量值，<c>$$</c> 转义为字面 <c>$</c>。
/// </summary>
/// <remarks>
/// 不提供表达式 / 函数调用——需要计算结果时用「计算」单元。
/// 路径支持对象成员与列表下标，见 <see cref="VariableScope.Resolve"/>。
/// </remarks>
public static class TextTemplate
{
    /// <summary>渲染模板；未定义变量 / 成员不存在时抛出 <see cref="ActionException"/>。</summary>
    public static string Render(string? template, VariableScope scope)
    {
        if (template == null) return "";
        if (template.Length == 0 || template.IndexOf('$') < 0) return template;

        var sb = new StringBuilder(template.Length + 16);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '$' || i + 1 >= template.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = template[i + 1];
            if (next == '$')                 // $$ → $
            {
                sb.Append('$');
                i++;
                continue;
            }

            if (next != '{')                 // 单独的 $ 原样保留
            {
                sb.Append(c);
                continue;
            }

            int end = template.IndexOf('}', i + 2);
            if (end < 0) throw new ActionException($"模板缺少右花括号：{template}");

            string path = template[(i + 2)..end];
            sb.Append(scope.Resolve(path).AsText());
            i = end;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 整段只是一个 <c>${变量}</c> 时取出里面的路径。
    /// 用于需要保留结构化类型（而不是压成文本）的场景。
    /// </summary>
    public static bool TrySingleVariable(string? text, out string path)
    {
        path = "";
        string value = (text ?? "").Trim();
        if (value.Length < 4 ||
            !value.StartsWith("${", StringComparison.Ordinal) ||
            !value.EndsWith('}'))
            return false;

        string inner = value[2..^1];
        if (inner.Length == 0 || inner.Contains('$') || inner.Contains('{') || inner.Contains('}'))
            return false;

        path = inner;
        return true;
    }
}
