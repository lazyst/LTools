using System.Text.Json.Serialization;

namespace LTools.Core.Actions;

/// <summary>变量在子动作里的方向（普通动作里都是 <see cref="local"/>）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VariableDirection
{
    local,
    input,
    output,
}

/// <summary>变量声明：名字 + 类型 + 默认值。</summary>
public sealed class VariableDef
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("type")]
    public ValueKind Type { get; set; } = ValueKind.text;

    /// <summary>默认值；缺省时按 <see cref="Type"/> 给空值（"" / 0 / false / [] / {}）。</summary>
    [JsonPropertyName("default")]
    public Value? Default { get; set; }

    /// <summary>界面显示名（可选）。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("direction")]
    public VariableDirection Direction { get; set; }

    /// <summary>实际初始值（默认值按声明类型转换；缺失时给该类型的空值）。</summary>
    public Value Seed() => Default?.Coerce(Type) ?? DefaultFor(Type);

    /// <summary>复制声明（值不可变，直接共享）。</summary>
    public VariableDef Clone() => new()
    {
        Key = Key,
        Type = Type,
        Default = Default,
        Label = Label,
        Direction = Direction,
    };

    public static Value DefaultFor(ValueKind kind) => kind switch
    {
        ValueKind.text => Value.FromText(""),
        ValueKind.number => Value.FromNumber(0),
        ValueKind.@bool => Value.FromBool(false),
        ValueKind.list => Value.FromList(),
        ValueKind.@object => Value.FromObject(),
        _ => Value.Empty,
    };
}

/// <summary>动作内子动作（跟随动作保存）。全局子动作另存于数据根 <c>subactions/</c>。</summary>
public sealed class SubActionDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("variables")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<VariableDef> Variables { get; } = new();

    [JsonPropertyName("steps")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Step> Steps { get; } = new();

    [JsonPropertyName("subActions")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<SubActionDef> SubActions { get; } = new();

    /// <summary>深拷贝（变量、步骤、嵌套子动作全部复制）。</summary>
    public SubActionDef Clone()
    {
        var clone = new SubActionDef
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            Description = Description,
        };

        foreach (var variable in Variables) clone.Variables.Add(variable.Clone());
        foreach (var step in Steps) clone.Steps.Add(step.Clone());
        foreach (var child in SubActions) clone.SubActions.Add(child.Clone());

        return clone;
    }
}

/// <summary>
/// 一个动作：变量表 + 步骤列表（+ 元数据）。与子动作同构，差别只是子动作的变量带输入/输出标记。
/// </summary>
public sealed class ActionProgram
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("variables")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<VariableDef> Variables { get; } = new();

    [JsonPropertyName("steps")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Step> Steps { get; } = new();

    /// <summary>动作内子动作。</summary>
    [JsonPropertyName("subActions")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<SubActionDef> SubActions { get; } = new();

    /// <summary>深拷贝（变量、步骤、动作内子动作全部复制）。</summary>
    public ActionProgram Clone()
    {
        var clone = new ActionProgram
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            Description = Description,
        };

        foreach (var variable in Variables) clone.Variables.Add(variable.Clone());
        foreach (var step in Steps) clone.Steps.Add(step.Clone());
        foreach (var child in SubActions) clone.SubActions.Add(child.Clone());

        return clone;
    }
}
