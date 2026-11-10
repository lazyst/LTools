using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 写一条跨动作状态（<c>state.set</c>）。键名自己定，所有动作共享同一张表。
/// </summary>
/// <remarks>
/// 与「设置变量」的区别：变量活一次运行，状态活到下次开机；与「写速记」的区别：
/// 速记是给人看的文档，状态是给流程读的值（保留类型，列表 / 对象原样存）。
/// </remarks>
public sealed class StateSetUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "state.set",
        Name = "存状态",
        Category = "文本与数据",
        Icon = "pin",
        Description = "把值存进跨动作状态表（state/values.json）；下次运行、重启后还能读到",
        Inputs = new[]
        {
            new InputSpec("key", "键", ValueKind.text, InputEditor.text, Required: true,
                Description: "自己定的名字，如 lastRun / counter；同一个键就是同一格"),
            new InputSpec("value", "值", null, InputEditor.multiline,
                Description: "要存的值；填变量时保留列表 / 对象类型，直接写文本就是文本"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "存入的值", null,
                Description: "原样带回来，方便接着用"),
            new OutputSpec("replaced", "覆盖了旧值", ValueKind.@bool,
                Description: "这个键原来就有值时为真"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string key = args.Text("key").Trim();
        var value = args.TryGet("value") ?? Value.Empty;
        var old = ActionStateStore.Set(key, value);

        ctx.SetOutput("value", value);
        ctx.SetOutput("replaced", Value.FromBool(old != null));
    }
}

/// <summary>
/// 读一条跨动作状态（<c>state.get</c>）：纯取值，可以直接内联在条件 / 参数里。
/// </summary>
public sealed class StateGetUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "state.get",
        Name = "读状态",
        Category = "文本与数据",
        Icon = "pin",
        Description = "从跨动作状态表里取值；键不存在时 found = false，并给出默认值",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("key", "键", ValueKind.text, InputEditor.text, Required: true,
                Description: "要读的名字，与「存状态」填的键一致"),
            new InputSpec("defaultValue", "默认值", null, InputEditor.text,
                Description: "键不存在时用它；留空就是空文本"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "值", null, "state",
                Description: "存进去的值（类型保留）；不存在时是默认值"),
            new OutputSpec("found", "存在", ValueKind.@bool,
                Description: "这个键是否有值"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string key = args.Text("key").Trim();
        if (ActionStateStore.TryGet(key, out var value))
        {
            ctx.SetOutput("value", value);
            ctx.SetOutput("found", Value.FromBool(true));
            return;
        }

        ctx.SetOutput("value", args.TryGet("defaultValue") ?? Value.Empty);
        ctx.SetOutput("found", Value.FromBool(false));
    }
}
