using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTools.Core.Actions;

/// <summary>
/// 变量值类型。只有五种——文本、数字、布尔、列表、对象。
/// </summary>
/// <remarks>
/// 这是新动作系统里<b>唯一的联合模型</b>：坐标、区域、匹配结果等结构化数据都装进对象，
/// 不再像旧的 <c>VarValue</c> 那样堆积可选字段。
/// 隐式转换只保留「文本 ⇄ 数字」「文本 ⇄ 布尔」「数字 ⇄ 布尔」，且发生在绑定参数时，
/// 运行时不做类型提升（避免 <c>1 + "1" = 11</c> 这类意外）。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValueKind
{
    text,
    number,
    @bool,
    list,
    @object,
}

/// <summary>一次运行中的变量值。不可变。</summary>
[JsonConverter(typeof(ValueJsonConverter))]
public sealed class Value
{
    private readonly string _text;
    private readonly double _number;
    private readonly bool _flag;
    private readonly List<Value>? _items;
    private readonly Dictionary<string, Value>? _members;

    private Value(ValueKind kind, string text = "", double number = 0, bool flag = false,
        List<Value>? items = null, Dictionary<string, Value>? members = null)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _flag = flag;
        _items = items;
        _members = members;
    }

    /// <summary>值类型。</summary>
    public ValueKind Kind { get; }

    /// <summary>空文本（变量未赋值时的默认值）。</summary>
    public static Value Empty { get; } = FromText("");

    public static Value FromText(string? text) =>
        new(ValueKind.text, text: text ?? "");

    public static Value FromNumber(double number) =>
        new(ValueKind.number, number: number);

    public static Value FromBool(bool flag) =>
        new(ValueKind.@bool, flag: flag);

    public static Value FromList(IEnumerable<Value>? items = null) =>
        new(ValueKind.list, items: items?.ToList() ?? new List<Value>());

    public static Value FromObject(IEnumerable<KeyValuePair<string, Value>>? members = null)
    {
        var map = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (members != null)
            foreach (var kv in members)
                map[kv.Key] = kv.Value;
        return new Value(ValueKind.@object, members: map);
    }

    /// <summary>用键值对构造对象（结构化结果的书写糖）。</summary>
    public static Value Object(params (string Key, Value Value)[] members) =>
        FromObject(members.Select(m => new KeyValuePair<string, Value>(m.Key, m.Value)));

    /// <summary>数字形式（内部值；类型不匹配时按文本解析）。</summary>
    public double NumberValue => Kind == ValueKind.number ? _number : AsNumber();

    /// <summary>布尔形式（内部值；类型不匹配时按文本解析）。</summary>
    public bool BoolValue => Kind == ValueKind.@bool ? _flag : AsBool();

    /// <summary>列表元素（仅 <see cref="ValueKind.list"/> 可用）。</summary>
    public IReadOnlyList<Value> Items => _items
        ?? throw new ActionException($"值不是列表（实际是 {Kind}）");

    /// <summary>对象成员（仅 <see cref="ValueKind.@object"/> 可用）。</summary>
    public IReadOnlyDictionary<string, Value> Members => _members
        ?? throw new ActionException($"值不是对象（实际是 {Kind}）");

    /// <summary>转为文本。数字用不变文化、最多 12 位小数；列表/对象做可读展开。</summary>
    public string AsText() => Kind switch
    {
        ValueKind.text => _text,
        ValueKind.number => _number.ToString("0.############", CultureInfo.InvariantCulture),
        ValueKind.@bool => _flag ? "true" : "false",
        ValueKind.list => "[" + string.Join(", ", Items.Select(v => v.AsText())) + "]",
        // 结构化结果的约定：对象里有 text 成员时，它就代表「直接当文本用」的样子
        // （区域是 "x,y,w,h"、匹配是 "x,y"、命令结果是 stdout），
        // 这样 ${匹配} 仍是紧凑文本，${匹配.x} 才拿坐标。
        ValueKind.@object => Members.TryGetValue("text", out var textMember)
            ? textMember.AsText()
            : "{" + string.Join(", ", Members.Select(kv => $"{kv.Key}={kv.Value.AsText()}")) + "}",
        _ => "",
    };

    /// <summary>转为数字。文本按不变文化解析；布尔 <c>true</c> = 1、<c>false</c> = 0。</summary>
    public double AsNumber()
    {
        if (Kind == ValueKind.number) return _number;
        if (Kind == ValueKind.@bool) return _flag ? 1 : 0;
        if (Kind == ValueKind.text)
        {
            string s = _text.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return n;
            throw new ActionException($"「{s}」不是数字");
        }
        throw new ActionException($"值不是数字（实际是 {Kind}）");
    }

    /// <summary>转为布尔。文本接受 true/false、1/0、yes/no、on/off（忽略大小写与首尾空白）。</summary>
    public bool AsBool()
    {
        if (Kind == ValueKind.@bool) return _flag;
        if (Kind == ValueKind.number) return Math.Abs(_number) > double.Epsilon;
        if (Kind == ValueKind.text)
        {
            string s = _text.Trim();
            if (s.Length == 0) return false;
            if (bool.TryParse(s, out bool b)) return b;
            return s.ToLowerInvariant() switch
            {
                "1" or "yes" or "y" or "on" => true,
                "0" or "no" or "n" or "off" => false,
                _ => throw new ActionException($"「{s}」不是布尔值"),
            };
        }
        throw new ActionException($"值不是布尔（实际是 {Kind}）");
    }

    /// <summary>转为列表；空文本视为空列表，其余类型报错。</summary>
    public IReadOnlyList<Value> AsList()
    {
        if (Kind == ValueKind.list) return Items;
        if (Kind == ValueKind.text && _text.Trim().Length == 0) return Array.Empty<Value>();
        throw new ActionException($"值不是列表（实际是 {Kind}）");
    }

    /// <summary>转为对象。</summary>
    public IReadOnlyDictionary<string, Value> AsObject()
    {
        if (Kind == ValueKind.@object) return Members;
        throw new ActionException($"值不是对象（实际是 {Kind}）");
    }

    /// <summary>按类型转换；<paramref name="kind"/> 为 null 时原样返回。</summary>
    public Value Coerce(ValueKind? kind) => kind switch
    {
        null => this,
        ValueKind.text => FromText(AsText()),
        ValueKind.number => FromNumber(AsNumber()),
        ValueKind.@bool => FromBool(AsBool()),
        ValueKind.list => Kind == ValueKind.list ? this : throw new ActionException($"值不是列表（实际是 {Kind}）"),
        ValueKind.@object => Kind == ValueKind.@object ? this : throw new ActionException($"值不是对象（实际是 {Kind}）"),
        _ => this,
    };

    /// <summary>
    /// 宽松相等：两边都能解析成数字时按数值比较（"3" == "3.0"），布尔与文本互认，
    /// 其余按文本忽略大小写比较。
    /// </summary>
    public bool LooseEquals(Value other)
    {
        if (other == null) return false;

        if (TryNumber(this, out double a) && TryNumber(other, out double b))
            return Math.Abs(a - b) < 1e-9;

        if (Kind == ValueKind.@bool || other.Kind == ValueKind.@bool)
        {
            try { return AsBool() == other.AsBool(); }
            catch (ActionException) { /* 落到文本比较 */ }
        }

        return string.Equals(AsText(), other.AsText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取成员（对象属性 / 列表长度 / 文本长度）。</summary>
    public Value GetMember(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ActionException("成员名为空");

        if (Kind == ValueKind.@object)
        {
            if (Members.TryGetValue(name, out var member)) return member;
            if (IsCountName(name)) return FromNumber(Members.Count);
            throw new ActionException($"对象没有成员「{name}」");
        }

        if (Kind == ValueKind.list)
        {
            if (IsCountName(name)) return FromNumber(Items.Count);
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return GetIndex(index);
            throw new ActionException($"列表没有成员「{name}」（只有 count / 下标）");
        }

        if (Kind == ValueKind.text && IsCountName(name))
            return FromNumber(_text.Length);

        throw new ActionException($"{Kind} 类型的值没有成员「{name}」");
    }

    /// <summary>取下标（列表；文本按字符）。</summary>
    public Value GetIndex(int index)
    {
        if (Kind == ValueKind.list)
        {
            if (index < 0 || index >= Items.Count)
                throw new ActionException($"列表下标越界：{index}（共 {Items.Count} 项）");
            return Items[index];
        }

        if (Kind == ValueKind.text)
        {
            if (index < 0 || index >= _text.Length)
                throw new ActionException($"文本下标越界：{index}（共 {_text.Length} 字符）");
            return FromText(_text[index].ToString());
        }

        throw new ActionException($"{Kind} 类型的值不能按下标访问");
    }

    private static bool IsCountName(string name) =>
        name.Equals("count", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("length", StringComparison.OrdinalIgnoreCase);

    private static bool TryNumber(Value value, out double number)
    {
        if (value.Kind == ValueKind.number) { number = value.NumberValue; return true; }
        if (value.Kind == ValueKind.text)
            return double.TryParse(value.AsText().Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number);
        number = 0;
        return false;
    }

    public override string ToString() => AsText();
}

/// <summary>
/// <see cref="Value"/> 的 JSON 读写：顶层是字符串 / 数字 / 布尔 / 数组 / 对象，
/// 不额外包 <c>{kind, value}</c>，手写配置更省事。
/// </summary>
public sealed class ValueJsonConverter : JsonConverter<Value>
{
    public override Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return FromElement(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, Value value, JsonSerializerOptions options)
        => WriteElement(writer, value);

    internal static Value FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => Value.FromText(element.GetString() ?? ""),
        JsonValueKind.Number => element.TryGetDouble(out double n)
            ? Value.FromNumber(n)
            : Value.FromText(element.GetRawText()),
        JsonValueKind.True => Value.FromBool(true),
        JsonValueKind.False => Value.FromBool(false),
        JsonValueKind.Array => Value.FromList(element.EnumerateArray().Select(FromElement)),
        JsonValueKind.Object => Value.FromObject(
            element.EnumerateObject().Select(p => new KeyValuePair<string, Value>(p.Name, FromElement(p.Value)))),
        _ => Value.Empty,
    };

    internal static void WriteElement(Utf8JsonWriter writer, Value value)
    {
        switch (value.Kind)
        {
            case ValueKind.text:
                writer.WriteStringValue(value.AsText());
                break;
            case ValueKind.number:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case ValueKind.@bool:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case ValueKind.list:
                writer.WriteStartArray();
                foreach (var item in value.Items) WriteElement(writer, item);
                writer.WriteEndArray();
                break;
            case ValueKind.@object:
                writer.WriteStartObject();
                foreach (var kv in value.Members)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteElement(writer, kv.Value);
                }
                writer.WriteEndObject();
                break;
        }
    }
}
