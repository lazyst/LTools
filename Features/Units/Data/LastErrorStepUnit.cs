using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 上一次被「失败继续」吞掉的错误发生在哪一行、哪个动作单元、属于哪一类。
/// </summary>
/// <remarks>
/// <see cref="LastErrorUnit"/> 只给错误文本（<c>第 2 行「点击文字」失败：没找到…</c> 一整串），
/// 想按「卡在哪个单元 / 是不是超时」分流还得自己切字符串。这一步把三样拆成独立输出，
/// 好处是配「如果」就能直接判断（例如 <c>${lastErrorKind} == 超时</c>），不用解析文本。
/// </remarks>
public sealed class LastErrorStepUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "flow.lastErrorStep",
        Name = "上一次失败的步骤",
        Category = "流程",
        Icon = "warning",
        Description = "读取最近一次被「失败继续」吞掉的错误：位置、动作单元名、失败性质",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("label", "位置", ValueKind.text, "lastErrorStep",
                Description: "第几行（嵌套时形如「第 2 行 › 满足时 › 第 1 行」）；没发生错误时为空"),
            new OutputSpec("unit", "动作单元", ValueKind.text, "lastErrorStepUnit",
                Description: "失败那一步的动作单元名（如「点击文字」）；没发生错误时为空"),
            new OutputSpec("kind", "失败性质", ValueKind.text, "lastErrorKind",
                Description: "超时 / 未找到 / 前置条件 / 未知错误 / 已停止"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        ctx.SetOutput("label", Value.FromText(ctx.LastErrorStep));
        ctx.SetOutput("unit", Value.FromText(ctx.LastErrorStepUnit));
        ctx.SetOutput("kind", Value.FromText(ActionErrors.Label(ctx.LastErrorKind)));
    }
}
