using System.Text;

namespace LTools.Features;

/// <summary>
/// 极简模板：把 <c>${name}</c> 替换为变量值，<c>$$</c> 转义为字面 <c>$</c>。
/// 不提供表达式 / 函数调用——复杂计算用 set 节点与条件组合表达。
/// </summary>
internal static class Template
{
    /// <summary>渲染模板；null 原样返回 null（便于可选字段保持省略）。</summary>
    public static string? RenderOpt(string? template, RunContext ctx) =>
        template == null ? null : Render(template, ctx);

    /// <summary>渲染模板；未定义变量抛 <see cref="FlowException"/>。</summary>
    public static string Render(string template, RunContext ctx)
    {
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
            if (end < 0) throw new FlowException($"模板缺少右花括号：{template}");
            string path = template[(i + 2)..end];
            sb.Append(ctx.ResolvePath(path).Text);
            i = end;
        }
        return sb.ToString();
    }
}
