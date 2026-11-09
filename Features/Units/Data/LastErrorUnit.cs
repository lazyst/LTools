using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>上一次被「失败继续」吞掉的错误文本。</summary>
public sealed class LastErrorUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "flow.lastError",
        Name = "上一步的错误",
        Category = "文本与数据",
        Icon = "warning",
        Description = "取本次运行里最近一次被「失败继续」吞掉的错误文本",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("text", "错误文本", ValueKind.text, "lastError", Description: "最近一次失败步骤的错误文本；没有错误时为空"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args) =>
        ctx.SetOutput("text", Value.FromText(ctx.LastError));
}
