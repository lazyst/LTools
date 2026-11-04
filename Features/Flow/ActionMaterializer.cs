namespace LTools.Features;

/// <summary>
/// 执行前把叶子动作里的字符串字段做一次模板替换（<c>${var}</c>）。
/// </summary>
/// <remarks>
/// 只替换「会被执行引擎直接使用」的字段；Id / Name / Icon / Type 等元数据保持原样。
/// 替换结果是动作的深拷贝，不会回写流程定义。
/// </remarks>
internal static class ActionMaterializer
{
    /// <summary>返回替换后的动作快照。</summary>
    public static ActionDto Materialize(ActionDto action, RunContext ctx)
    {
        if (action.Type == ActionType.composite)
            throw new FlowException("流程不能嵌套流程");

        var a = action.Clone();
        a.Target = Template.RenderOpt(a.Target, ctx);
        a.Args = Template.RenderOpt(a.Args, ctx);
        a.Workdir = Template.RenderOpt(a.Workdir, ctx);
        a.Path = Template.RenderOpt(a.Path, ctx);
        a.Url = Template.RenderOpt(a.Url, ctx);
        a.Cmd = Template.RenderOpt(a.Cmd, ctx);
        a.Text = Template.RenderOpt(a.Text, ctx);
        a.X = Template.RenderOpt(a.X, ctx);
        a.Y = Template.RenderOpt(a.Y, ctx);

        if (a.Items != null)
        {
            foreach (var item in a.Items)
            {
                if (item.Kind == KeyItemKind.text && item.Text != null)
                    item.Text = Template.Render(item.Text, ctx);
            }
        }

        return a;
    }
}
