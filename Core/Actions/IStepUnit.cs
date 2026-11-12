namespace LTools.Core.Actions;

/// <summary>
/// 动作单元：一类操作的声明 + 执行函数。
/// </summary>
/// <remarks>
/// 新增一个动作单元 = 新建一个实现类 + 在 <c>BuiltInUnits</c> 里注册一行；
/// 参数表单、校验、摘要全部由 <see cref="Spec"/> 派生，不需要改 UI 的按类型分支。
/// </remarks>
public interface IStepUnit
{
    /// <summary>静态声明。</summary>
    UnitSpec Spec { get; }

    /// <summary>执行一次；结果通过 <see cref="StepContext.SetOutput"/> 写出。</summary>
    void Execute(StepContext ctx, StepArgs args);
}

/// <summary>已解析的参数集合（字面量模板已渲染、变量已取值、内联取值已求值）。</summary>
public sealed class StepArgs
{
    private readonly Dictionary<string, Value> _values;
    private readonly HashSet<string> _provided;

    internal StepArgs(Dictionary<string, Value> values, IEnumerable<string> provided)
    {
        _values = values;
        _provided = new HashSet<string>(provided, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, Value> Values => _values;

    /// <summary>该参数是否由步骤提供（或带默认值）；仅用于可选参数的「填了没有」判断。</summary>
    public bool Has(string key) => _provided.Contains(key);

    public Value? TryGet(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public Value Get(string key) =>
        _values.TryGetValue(key, out var value)
            ? value
            : throw new ActionException($"缺少参数：{key}");

    public string Text(string key) => Get(key).AsText();

    public double Number(string key) => Get(key).AsNumber();

    public bool Bool(string key) => Get(key).AsBool();

    public IReadOnlyList<Value> List(string key) => Get(key).AsList();
}
