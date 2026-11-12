using LTools.Features.Units;

namespace LTools.Core.Actions;

/// <summary>
/// 动作单元注册表。键大小写不敏感；重复注册直接失败，避免静默覆盖。
/// </summary>
public sealed class StepRegistry
{
    private readonly Dictionary<string, IStepUnit> _units = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>内置注册表（流程动作单元 + 随程序分发的动作单元）。</summary>
    public static StepRegistry Default { get; } = CreateDefault();

    /// <summary>已注册的全部动作单元。</summary>
    public IReadOnlyCollection<IStepUnit> Units => _units.Values;

    public void Register(IStepUnit unit)
    {
        if (unit == null) throw new ArgumentNullException(nameof(unit));

        unit.Spec.Validate();

        if (_units.ContainsKey(unit.Spec.Key))
            throw new ActionException($"动作单元「{unit.Spec.Key}」重复注册");

        _units[unit.Spec.Key] = unit;
    }

    public bool TryGet(string key, out IStepUnit unit) =>
        _units.TryGetValue((key ?? "").Trim(), out unit!);

    public IStepUnit Get(string key)
    {
        key = (key ?? "").Trim();
        if (_units.TryGetValue(key, out var unit)) return unit;

        throw new ActionException(key.Length == 0
            ? "步骤缺少动作单元键"
            : $"未知的动作单元：{key}");
    }

    private static StepRegistry CreateDefault()
    {
        var registry = new StepRegistry();
        FlowUnits.RegisterAll(registry);
        BuiltInUnits.RegisterAll(registry);
        return registry;
    }
}
