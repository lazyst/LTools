using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Features.Units.Ui;

/// <summary>
/// 选择器：给一串候选，让用户挑一项（<c>ui.select</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 「输入框」要用户打字，「选择器」只让他从已有项里点一个——列表来自变量 / 文件列表 / 正则结果时用它，
/// 省得手抄。候选是列表值（每行一项，或直接填一个列表变量）。
/// </para>
/// <para>
/// 取消 / 关窗 / 超时都是 <c>found = false</c>（不是失败），下标 -1；用户按 Enter 或双击直接确定。
/// </para>
/// </remarks>
public sealed class SelectUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.select",
        Name = "选择器",
        Category = "界面",
        Icon = "filter",
        Description = "列出候选让用户选一项；取消 / 超时返回 found = false",
        Inputs = new[]
        {
            new InputSpec("title", "标题", ValueKind.text, Default: Value.FromText("请选择"),
                Description: "窗口标题"),
            new InputSpec("text", "提示", null, InputEditor.multiline,
                Description: "列表上方的一句话；留空只显示列表"),
            new InputSpec("options", "候选", ValueKind.list, InputEditor.multiline, Required: true,
                Description: "每行一项，或填一个列表变量；不能为空"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true,
                Description: "0 = 一直等（停止热键可中断）；到点自动关窗并返回 found = false"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "选中项", ValueKind.text, "selected",
                Description: "用户选中的那一项文字；取消为空"),
            new OutputSpec("index", "下标", ValueKind.number,
                Description: "选中项在候选里的位置（0 起）；取消为 -1"),
            new OutputSpec("found", "选了", ValueKind.@bool,
                Description: "用户是否确认了选择（取消 / 超时 / 停止为假）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var options = args.List("options").Select(v => v.AsText()).ToList();
        if (options.Count == 0) throw new ActionException("候选列表是空的，没法选");

        int timeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 86_400_000);
        string text = ListText.Display(args.TryGet("text"));

        var window = ChoiceWindowService.Open(args.Text("title"), text,
            options, new[] { "确定", "取消" }, selectMode: true);

        var (accepted, index, value) = ChoiceWindowService.Wait(ctx, window, timeoutMs);

        ctx.SetOutput("found", Value.FromBool(accepted));
        ctx.SetOutput("index", Value.FromNumber(accepted ? index : -1));
        ctx.SetOutput("value", Value.FromText(accepted ? value : ""));
    }
}
