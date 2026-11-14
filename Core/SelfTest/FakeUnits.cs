using LTools.Core.Actions;
using LTools.Features.Units.Data;

namespace LTools.Core.SelfTest;

/// <summary>
/// 自测用的假动作单元：只记调用、按传入的 lambda 执行，不碰真实键鼠 / 屏幕。
/// </summary>
internal sealed class FakeUnit : IStepUnit
{
    private readonly List<StepArgs> _received = new();

    public FakeUnit(string key, string name, UnitKind kind = UnitKind.action,
        InputSpec[]? inputs = null, OutputSpec[]? outputs = null,
        Action<StepContext, StepArgs>? run = null)
    {
        Spec = new UnitSpec
        {
            Key = key,
            Name = name,
            Category = "测试",
            Icon = "test",
            Kind = kind,
            Inputs = inputs ?? Array.Empty<InputSpec>(),
            Outputs = outputs ?? Array.Empty<OutputSpec>(),
        };
        Run = run;
    }

    public UnitSpec Spec { get; }

    public Action<StepContext, StepArgs>? Run { get; }

    public int Calls { get; private set; }

    public IReadOnlyList<StepArgs> Received => _received;

    public void Execute(StepContext ctx, StepArgs args)
    {
        Calls++;
        _received.Add(args);
        Run?.Invoke(ctx, args);
    }
}

/// <summary>测试用注册表：流程控制单元 + 全套假单元；传进来的同名实例会替换默认实例（为了拿到计数）。</summary>
internal static class TestRegistry
{
    public static StepRegistry Create(params IStepUnit[] units)
    {
        var registry = new StepRegistry();
        FlowUnits.RegisterAll(registry);
        registry.Register(new LastErrorUnit());

        var byKey = new Dictionary<string, IStepUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (IStepUnit unit in new IStepUnit[]
                 {
                     Echo(), Fail(), Mark(), WriteScope(), Pure(), Slow(), Flaky(0),
                 })
            byKey[unit.Spec.Key] = unit;

        foreach (IStepUnit unit in units) byKey[unit.Spec.Key] = unit;

        foreach (IStepUnit unit in byKey.Values) registry.Register(unit);
        return registry;
    }

    /// <summary>回声：<c>text</c> 原样写回；<c>value</c> 输出参数的**原值**（断言类型用）。</summary>
    public static FakeUnit Echo() => new("test.echo", "回声测试",
        inputs: new[]
        {
            new InputSpec("text", "文本"),
            new InputSpec("number", "数字", ValueKind.number, Default: Value.FromNumber(0)),
        },
        outputs: new[]
        {
            new OutputSpec("text", "文本", ValueKind.text, "v"),
            new OutputSpec("value", "原值"),
            new OutputSpec("count", "数字", ValueKind.number),
        },
        run: (ctx, args) =>
        {
            Value text = args.TryGet("text") ?? Value.Empty;
            ctx.SetOutput("text", text);
            ctx.SetOutput("value", text);
            ctx.SetOutput("count", Value.FromNumber(args.Has("number") ? args.Number("number") : 0));
        });

    /// <summary>总是失败：消息来自 <c>message</c>。</summary>
    public static FakeUnit Fail() => new("test.fail", "失败测试",
        inputs: new[] { new InputSpec("message", "消息") },
        run: (_, args) => throw new ActionException(args.Text("message")));

    /// <summary>只是记一次调用（<see cref="FakeUnit.Calls"/>）。</summary>
    public static FakeUnit Mark(string key = "test.mark", string name = "计数测试") =>
        new(key, name, inputs: new[] { new InputSpec("label", "标签") });

    /// <summary>前 <paramref name="failures"/> 次失败、之后成功（步骤重试用）。</summary>
    public static FakeUnit Flaky(int failures)
    {
        FakeUnit? self = null;
        var unit = new FakeUnit("test.flaky", "时好时坏测试",
            inputs: new[] { new InputSpec("message", "消息") },
            run: (_, args) =>
            {
                if (self!.Calls <= failures)
                    throw new ActionException(args.TryGet("message")?.AsText() ?? "一会儿再试");
            });

        self = unit;
        return unit;
    }

    /// <summary>直接写当前作用域（验证作用域隔离 / 循环条件用，不依赖任何真实单元）。</summary>
    public static FakeUnit WriteScope() => new("test.scope", "写作用域测试",
        inputs: new[] { new InputSpec("name", "变量名", Required: true), new InputSpec("value", "值") },
        run: (ctx, args) => ctx.Scope.Set(args.Text("name"), args.TryGet("value") ?? Value.Empty));

    /// <summary>取值动作单元（<c>Kind = pure</c>）：干跑时照跑。</summary>
    public static FakeUnit Pure() => new("test.pure", "取值测试", UnitKind.pure,
        outputs: new[] { new OutputSpec("text", "文本", ValueKind.text, "pureValue") },
        run: (ctx, _) => ctx.SetOutput("text", Value.FromText("pure")));

    /// <summary>睡一会儿（只看返回值时可用；自测里不依赖它计时）。</summary>
    public static FakeUnit Slow() => new("test.slow", "慢步骤测试",
        inputs: new[] { new InputSpec("ms", "毫秒", ValueKind.number, Required: true) },
        run: (_, args) => Thread.Sleep((int)Math.Clamp(args.Number("ms"), 0, 60_000)));
}
