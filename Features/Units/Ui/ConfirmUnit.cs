using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Features.Units.Ui;

/// <summary>
/// 确认框：问一句话，等用户按按钮（<c>ui.confirm</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 按钮可以自定义（<c>确定|取消</c>、<c>保存|不保存|取消</c>），<b>第一个按钮算「肯定」</b>——
/// <c>confirmed</c> 就是「按的是不是第一个」，三选一时按 <c>value</c> / <c>index</c> 分支。
/// </para>
/// <para>
/// 非模态 + 轮询（见 <see cref="ChoiceWindowService"/>）：停止热键能把它关掉并中止流程。
/// 超时不是失败，只是 <c>found = false</c>；关窗（X / Esc）也算没作答。
/// </para>
/// </remarks>
public sealed class ConfirmUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.confirm",
        Name = "确认框",
        Category = "界面",
        Icon = "help",
        Description = "弹一个确认框等用户按按钮；按钮可自定义，第一个算「肯定」，超时 / 关窗返回 found = false",
        Inputs = new[]
        {
            new InputSpec("title", "标题", ValueKind.text, Default: Value.FromText("确认"),
                Description: "窗口标题"),
            new InputSpec("text", "内容", null, InputEditor.multiline, Required: true,
                Description: "要问的话；列表按每行一项显示"),
            new InputSpec("buttons", "按钮", ValueKind.text, Default: Value.FromText("确定|取消"),
                Description: "用 | 分开，第一个是肯定按钮；如「保存|不保存|取消」"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true,
                Description: "0 = 一直等（停止热键可中断）；到点自动关窗并返回 found = false"),
        },
        Outputs = new[]
        {
            new OutputSpec("confirmed", "按了肯定按钮", ValueKind.@bool,
                Description: "按的是第一个按钮（默认「确定」）"),
            new OutputSpec("value", "按钮文字", ValueKind.text, "choice",
                Description: "实际按下的按钮文字；没作答为空"),
            new OutputSpec("index", "按钮下标", ValueKind.number,
                Description: "按下的是第几个按钮（0 起）；没作答为 -1"),
            new OutputSpec("found", "作答了", ValueKind.@bool,
                Description: "用户是否按了按钮（超时 / 关窗 / 停止为假）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var buttons = ParseButtons(args.Text("buttons"));
        string text = ListText.Display(args.TryGet("text"));
        int timeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 86_400_000);

        var window = ChoiceWindowService.Open(args.Text("title"), text,
            Array.Empty<string>(), buttons, selectMode: false);

        var (accepted, index, value) = ChoiceWindowService.Wait(ctx, window, timeoutMs);

        ctx.SetOutput("found", Value.FromBool(accepted));
        ctx.SetOutput("confirmed", Value.FromBool(accepted && index == 0));
        ctx.SetOutput("index", Value.FromNumber(accepted ? index : -1));
        ctx.SetOutput("value", Value.FromText(value));
    }

    private static IReadOnlyList<string> ParseButtons(string text)
    {
        var labels = (text ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return labels.Count == 0 ? new[] { "确定", "取消" } : labels;
    }
}
