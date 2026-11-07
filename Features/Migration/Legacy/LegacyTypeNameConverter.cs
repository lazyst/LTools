using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTools.Features.Migration.Legacy;

/// <summary>
/// 动作 JSON 字段 <c>Type</c> 的读取：<see cref="ActionDto.RawType"/> 用它兜底旧配置。
/// </summary>
/// <remarks>
/// 正常配置里是字符串（<c>"runCommand"</c> / <c>"composite"</c> …），原样返回；
/// 极旧 / 手改的配置可能写成数字（v1 枚举序号），按当时的声明顺序映射回名字，
/// 免得一个数字让整份配置被判为损坏。写出一律是字符串。
/// </remarks>
internal sealed class LegacyTypeNameConverter : JsonConverter<string>
{
    /// <summary>v1 <c>ActionType</c> 的声明顺序（数字配置按它还原）。</summary>
    private static readonly string[] LegacyNames =
    [
        "launchApp",
        "openFile",
        "openFolder",
        "openUrl",
        "runCommand",
        "sendText",
        "setClipboard",
        "toast",
        "quickNote",
        "window",
        "clickText",
        "imageClick",
        "sendKeys",
        "mouse",
        "macro",
        "internal",
        "composite",
    ];

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return "";

            case JsonTokenType.String:
                return reader.GetString() ?? "";

            case JsonTokenType.Number:
                if (reader.TryGetInt32(out int index))
                {
                    if (index >= 0 && index < LegacyNames.Length) return LegacyNames[index];
                    throw new JsonException($"动作类型序号无效：{index}");
                }
                throw new JsonException("动作类型序号无效");

            default:
                throw new JsonException($"动作类型写法无效：{reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
