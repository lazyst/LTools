using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTools.Core.Actions;

/// <summary>步骤失败后的处理方式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FailMode
{
    /// <summary>中止整个动作（默认）。</summary>
    stop,

    /// <summary>记录错误后继续下一步。</summary>
    @continue,
}

/// <summary>一个参数值的来源：字面量 / 变量 / 内联取值单元。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepArgKind
{
    literal,
    variable,
    inline,
}

/// <summary>
/// 参数值。JSON 三态：
/// <list type="bullet">
/// <item><c>"文本"</c> / <c>42</c> / <c>true</c> / 数组 / 对象 — 字面量（文本支持 <c>${...}</c> 模板）</item>
/// <item><c>{"var": "name"}</c> — 取变量（保留类型）</item>
/// <item><c>{"unit": "screen.findImage", "in": {...}}</c> — 内联 Pure 单元</item>
/// </list>
/// </summary>
[JsonConverter(typeof(StepArgConverter))]
public sealed class StepArg
{
    private StepArg(StepArgKind kind, Value? literal = null, string? variable = null, Step? inline = null)
    {
        Kind = kind;
        Literal = literal ?? Value.Empty;
        VariablePath = variable ?? "";
        InlineStep = inline;
    }

    public StepArgKind Kind { get; }

    /// <summary>字面量值（<see cref="StepArgKind.literal"/>）。</summary>
    public Value Literal { get; }

    /// <summary>变量路径（<see cref="StepArgKind.variable"/>），支持 <c>a.b[0]</c>。</summary>
    public string VariablePath { get; }

    /// <summary>内联的取值步骤（<see cref="StepArgKind.inline"/>）。</summary>
    public Step? InlineStep { get; }

    public static StepArg FromText(string? text) => new(StepArgKind.literal, Value.FromText(text));

    public static StepArg FromNumber(double number) => new(StepArgKind.literal, Value.FromNumber(number));

    public static StepArg FromBool(bool flag) => new(StepArgKind.literal, Value.FromBool(flag));

    public static StepArg FromValue(Value value) => new(StepArgKind.literal, value);

    public static StepArg FromVar(string path) => new(StepArgKind.variable, variable: path);

    public static StepArg FromInline(Step step) =>
        new(StepArgKind.inline, inline: step ?? throw new ArgumentNullException(nameof(step)));

    /// <summary>深拷贝（内联步骤递归复制；字面量值本身不可变，直接共享）。</summary>
    public StepArg Clone() => Kind switch
    {
        StepArgKind.variable => new StepArg(StepArgKind.variable, variable: VariablePath),
        StepArgKind.inline => new StepArg(StepArgKind.inline,
            inline: InlineStep?.Clone() ?? throw new ActionException("内联取值步骤为空")),
        _ => new StepArg(StepArgKind.literal, literal: Literal),
    };

    /// <summary>求值。字面量文本会渲染 <c>${...}</c>（列表 / 对象里的文本也递归渲染）；其余保留原始类型。</summary>
    internal Value Resolve(StepContext ctx) => Resolve(ctx, renderLiteral: true);

    /// <summary>
    /// 求值；<paramref name="renderLiteral"/> = false 时字面量文本原样返回（代码类参数用，
    /// 脚本里的 <c>${…}</c> 不该被当模板求值）。
    /// </summary>
    internal Value Resolve(StepContext ctx, bool renderLiteral) => Kind switch
    {
        StepArgKind.literal => renderLiteral ? RenderLiteral(Literal, ctx.Scope) : Literal,
        StepArgKind.variable => ctx.Scope.Resolve(VariablePath),
        StepArgKind.inline => ctx.EvaluateInline(InlineStep
            ?? throw new ActionException("内联取值步骤为空")),
        _ => Value.Empty,
    };

    private static Value RenderLiteral(Value value, VariableScope scope) => value.Kind switch
    {
        ValueKind.text => Value.FromText(TextTemplate.Render(value.AsText(), scope)),
        ValueKind.list => Value.FromList(value.Items.Select(item => RenderLiteral(item, scope))),
        ValueKind.@object => Value.FromObject(value.Members.Select(kv =>
            new KeyValuePair<string, Value>(kv.Key, RenderLiteral(kv.Value, scope)))),
        _ => value,
    };

    public override string ToString() => Kind switch
    {
        StepArgKind.literal => $"\"{Literal.AsText()}\"",
        StepArgKind.variable => "${" + VariablePath + "}",
        _ => InlineStep?.Unit ?? "<inline>",
    };
}

/// <summary>
/// 步骤：单元的一次使用。纯数据，可序列化。
/// </summary>
/// <remarks>
/// 控制流不是节点图：<c>if</c> / 循环就是普通步骤，分支体放在 <see cref="Then"/> /
/// <see cref="Else"/> / <see cref="Body"/> 里，编辑器和执行器都只处理一种递归。
/// </remarks>
public sealed class Step
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("in")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, StepArg> In { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>输出键 → 变量名（不写则丢弃该输出）。</summary>
    [JsonPropertyName("out")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> Out { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>循环体。</summary>
    [JsonPropertyName("body")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Step> Body { get; } = new();

    /// <summary>条件为真。</summary>
    [JsonPropertyName("then")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Step> Then { get; } = new();

    /// <summary>条件为假。</summary>
    [JsonPropertyName("else")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<Step> Else { get; } = new();

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("onFail")]
    public FailMode OnFail { get; set; }

    /// <summary>执行完本步后等待的毫秒数（可取消）。</summary>
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; }

    public static Step Of(string unit) => new() { Unit = unit };

    /// <summary>设置一个参数（链式写法，便于测试与迁移器构造）。</summary>
    public Step With(string key, StepArg arg)
    {
        In[key] = arg;
        return this;
    }

    /// <summary>设置一个文本参数。</summary>
    public Step WithText(string key, string? text) => With(key, StepArg.FromText(text));

    /// <summary>设置一个变量参数。</summary>
    public Step WithVar(string key, string variable) => With(key, StepArg.FromVar(variable));

    /// <summary>把某个输出写到变量。</summary>
    public Step Output(string key, string variable)
    {
        Out[key] = variable;
        return this;
    }

    /// <summary>递归枚举所有子步骤（含自身）。</summary>
    public IEnumerable<Step> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Body.Concat(Then).Concat(Else))
            foreach (var step in child.SelfAndDescendants())
                yield return step;
    }

    /// <summary>深拷贝（参数、输出映射、子步骤全部复制）。</summary>
    public Step Clone()
    {
        var clone = new Step
        {
            Unit = Unit,
            Note = Note,
            Disabled = Disabled,
            OnFail = OnFail,
            DelayMs = DelayMs,
        };

        foreach (var pair in In) clone.In[pair.Key] = pair.Value.Clone();
        foreach (var pair in Out) clone.Out[pair.Key] = pair.Value;

        foreach (var child in Body) clone.Body.Add(child.Clone());
        foreach (var child in Then) clone.Then.Add(child.Clone());
        foreach (var child in Else) clone.Else.Add(child.Clone());

        return clone;
    }
}

/// <summary><see cref="StepArg"/> 的三态 JSON 转换。</summary>
public sealed class StepArgConverter : JsonConverter<StepArg>
{
    public override StepArg Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return StepArg.FromText(reader.GetString());

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetDouble(out double number)) return StepArg.FromNumber(number);
            using var numberDoc = JsonDocument.ParseValue(ref reader);
            return StepArg.FromText(numberDoc.RootElement.GetRawText());
        }

        if (reader.TokenType == JsonTokenType.True) return StepArg.FromBool(true);
        if (reader.TokenType == JsonTokenType.False) return StepArg.FromBool(false);
        if (reader.TokenType == JsonTokenType.Null) return StepArg.FromText("");
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var array = JsonDocument.ParseValue(ref reader);
            return StepArg.FromValue(ValueJsonConverter.FromElement(array.RootElement));
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"参数值写法无效：{reader.TokenType}");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("var", out var varElement) ||
            root.TryGetProperty("variable", out varElement))
        {
            string? path = varElement.GetString();
            if (string.IsNullOrWhiteSpace(path))
                throw new JsonException("{\"var\": ...} 缺少变量名");
            return StepArg.FromVar(path);
        }

        if (root.TryGetProperty("unit", out _))
        {
            var step = root.Deserialize<Step>(options);
            if (step == null) throw new JsonException("内联取值步骤为空");
            return StepArg.FromInline(step);
        }

        // 兼容旧写法 / 手写便利：{"value": ...} 当作字面量
        if (root.TryGetProperty("value", out var valueElement))
            return StepArg.FromValue(ValueJsonConverter.FromElement(valueElement));

        // 其余对象整体当字面量对象
        return StepArg.FromValue(ValueJsonConverter.FromElement(root));
    }

    public override void Write(Utf8JsonWriter writer, StepArg value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case StepArgKind.variable:
                writer.WriteStartObject();
                writer.WriteString("var", value.VariablePath);
                writer.WriteEndObject();
                break;

            case StepArgKind.inline:
                JsonSerializer.Serialize(writer, value.InlineStep, options);
                break;

            default:
                ValueJsonConverter.WriteElement(writer, value.Literal);
                break;
        }
    }
}
