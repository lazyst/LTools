using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LTools.Core.Actions;

/// <summary>
/// 列表值在「文本」和「<see cref="ValueKind.list"/>」之间的唯一转换规则。
/// </summary>
/// <remarks>
/// <para>
/// 三个地方共用，别再各写一套：
/// 参数里内联写列表（<c>a,b,c</c> / 每行一项）、变量表的列表默认值、界面上的只读显示。
/// </para>
/// <para>
/// 解析规则：换行和逗号都算分隔符；每项去掉首尾空白，空项丢弃。
/// 项里要带逗号或换行、或者要嵌套结构时用 JSON 数组（<c>["a,b", 1]</c>），别在分隔符上猜。
/// </para>
/// </remarks>
internal static class ListText
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>按行 / 逗号拆成列表项（纯文本项）。</summary>
    public static IReadOnlyList<Value> Parse(string? text)
    {
        string normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Trim().Length == 0) return Array.Empty<Value>();

        string[] parts = normalized.Split(new[] { '\n', ',' });

        var items = new List<Value>(parts.Length);
        foreach (string part in parts)
        {
            string item = part.Trim();
            if (item.Length > 0) items.Add(Value.FromText(item));
        }

        return items;
    }

    /// <summary>把值写成可编辑 / 可读的文本（列表每行一项；对象与嵌套项用 JSON）。</summary>
    public static string Display(Value? value)
    {
        if (value == null) return "";

        return value.Kind switch
        {
            ValueKind.list => string.Join("\n", value.Items.Select(ItemText)),
            ValueKind.@object => JsonSerializer.Serialize(value, JsonOptions),
            _ => value.AsText(),
        };
    }

    private static string ItemText(Value item) => item.Kind switch
    {
        ValueKind.text or ValueKind.number or ValueKind.@bool => item.AsText(),
        _ => JsonSerializer.Serialize(item, JsonOptions),
    };

    /// <summary>
    /// 变量表里「默认值」文本框 → 值。
    /// </summary>
    /// <remarks>
    /// 列表默认值以前被 <c>ReadVariables</c> 直接丢掉（显示出来的 <c>[a, b]</c> 是
    /// <see cref="Value.AsText"/> 的临时文本，存不回去），这里补上往返：
    /// 列表按行 / 逗号拆，以 <c>[</c> 开头则按 JSON 数组解析；对象必须写 JSON。
    /// 解析失败返回 <c>false</c>，由界面提示并拦住保存，不静默丢值。
    /// </remarks>
    public static bool TryParseDefault(ValueKind kind, string? text, out Value? value, out string error)
    {
        value = null;
        error = "";

        string raw = text ?? "";
        if (raw.Trim().Length == 0) return true;

        try
        {
            switch (kind)
            {
                case ValueKind.list:
                    value = raw.TrimStart().StartsWith('[')
                        ? ParseJson(raw, expectList: true)
                        : Value.FromList(Parse(raw));
                    return true;

                case ValueKind.@object:
                    value = ParseJson(raw, expectList: false);
                    return true;

                case ValueKind.number:
                    if (!double.TryParse(raw.Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double number))
                    {
                        error = $"「{raw.Trim()}」不是数字";
                        return false;
                    }
                    value = Value.FromNumber(number);
                    return true;

                case ValueKind.@bool:
                    if (!bool.TryParse(raw.Trim(), out bool flag))
                    {
                        error = $"「{raw.Trim()}」不是布尔值（只能是 true / false）";
                        return false;
                    }
                    value = Value.FromBool(flag);
                    return true;

                default:
                    value = Value.FromText(raw);
                    return true;
            }
        }
        catch (ActionException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            error = $"JSON 写法不对：{ex.Message}";
            return false;
        }
    }

    private static Value ParseJson(string text, bool expectList)
    {
        Value parsed = JsonSerializer.Deserialize<Value>(text, JsonOptions)
            ?? throw new ActionException("JSON 是空的");

        ValueKind want = expectList ? ValueKind.list : ValueKind.@object;
        if (parsed.Kind != want)
            throw new ActionException(expectList
                ? "列表默认值要写成 JSON 数组，例如 [\"a\", \"b\"]"
                : "对象默认值要写成 JSON 对象，例如 {\"a\": 1}");

        return parsed;
    }
}
