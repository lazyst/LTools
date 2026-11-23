using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTools.Core.Link;

/// <summary>消息类别。</summary>
internal static class LinkKinds
{
    /// <summary>要对方做一件事，必须回一条 <see cref="Response"/>（同 <c>id</c>）。</summary>
    public const string Request = "request";

    /// <summary>对某条请求的答复。</summary>
    public const string Response = "response";

    /// <summary>单向通知（没有 <c>id</c>、不回）。</summary>
    public const string Event = "event";
}

/// <summary>两端必须一致的东西：管道名、协议版本。</summary>
internal static class LinkNames
{
    /// <summary>协议版本：帧格式 / 字段含义变了就加一，握手不一致直接拒绝。</summary>
    public const int Protocol = 1;

    public const string PipePrefix = "LToolsLink";

    /// <summary>会话内的管道名（**裸名**，系统自己加 <c>\\.\pipe\</c> 前缀）。</summary>
    public static string Pipe(int sessionId) => $"{PipePrefix}-{sessionId}";
}

/// <summary>
/// 界面进程 ↔ 提权执行器之间的一条消息（见 <c>docs/PROCESS_SPLIT.md</c> §5）。
/// </summary>
/// <remarks>
/// 线上形状是 <c>{"id":1,"kind":"request","method":"run","ok":true,"payload":{…}}</c>，
/// 字段名 camelCase、<c>null</c> 不写。**负载是任意 JSON**，具体请求 / 答复的 DTO 由调用方定义，
/// 这一层只管收发与配对。
/// </remarks>
internal sealed class LinkMessage
{
    public int Id { get; init; }

    public string Kind { get; init; } = LinkKinds.Request;

    public string Method { get; init; } = "";

    public bool Ok { get; init; } = true;

    public string? Error { get; init; }

    public JsonElement? Payload { get; init; }

    public static LinkMessage Request(int id, string method, object? payload = null) => new()
    {
        Id = id,
        Kind = LinkKinds.Request,
        Method = method,
        Payload = LinkJson.Payload(payload),
    };

    public static LinkMessage Response(LinkMessage request, object? payload = null, string? error = null) => new()
    {
        Id = request.Id,
        Kind = LinkKinds.Response,
        Ok = error == null,
        Error = error,
        Payload = LinkJson.Payload(payload),
    };

    public static LinkMessage Event(string method, object? payload = null) => new()
    {
        Kind = LinkKinds.Event,
        Method = method,
        Payload = LinkJson.Payload(payload),
    };

    /// <summary>把负载读成 <typeparamref name="T"/>（没有负载返回 default）。</summary>
    public T? Read<T>() => Payload is { } payload && payload.ValueKind != JsonValueKind.Null
        ? payload.Deserialize<T>(LinkJson.Options)
        : default;

    /// <summary>从 JSON 读一条消息；形状不对返回 null（调用方按坏帧处理）。</summary>
    internal static LinkMessage? From(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!TryString(root, "kind", out string kind)) return null;
        if (kind != LinkKinds.Request && kind != LinkKinds.Response && kind != LinkKinds.Event) return null;

        int id = 0;
        if (TryProperty(root, "id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            if (!idElement.TryGetInt32(out id)) return null;
        }

        TryString(root, "method", out string method);

        bool ok = true;
        if (TryProperty(root, "ok", out JsonElement okElement)
            && okElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            ok = okElement.GetBoolean();
        }

        TryString(root, "error", out string error);

        JsonElement? payload = null;
        if (TryProperty(root, "payload", out JsonElement payloadElement)) payload = payloadElement.Clone();

        // 请求 / 通知必须有方法名：没有就不知道要做什么，当坏帧。
        if (kind != LinkKinds.Response && method.Length == 0) return null;

        return new LinkMessage
        {
            Id = id,
            Kind = kind,
            Method = method,
            Ok = ok,
            Error = error.Length == 0 ? null : error,
            Payload = payload,
        };
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value) =>
        root.TryGetProperty(name, out value) || root.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value);

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!TryProperty(root, name, out JsonElement element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return true;
    }
}

/// <summary>消息的 JSON 选项与负载转换（收发、自测共用一份）。</summary>
internal static class LinkJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>任意负载 → <see cref="JsonElement"/>；没有负载给 null（写消息时这个字段省掉）。</summary>
    public static JsonElement? Payload(object? payload) => payload switch
    {
        null => null,
        JsonElement element => element.Clone(),
        _ => JsonSerializer.SerializeToElement(payload, payload.GetType(), Options),
    };
}

/// <summary>
/// 帧格式：<c>[4 字节小端长度][UTF-8 JSON]</c>。拆包 / 粘包都按长度切，别按换行切
/// （负载里的正文可能带换行）。
/// </summary>
internal static class LinkFraming
{
    public const int HeaderSize = 4;

    /// <summary>单帧上限 8 MB：再大就说明两端对不上（图片走文件路径，不往帧里塞）。</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static byte[] Frame(LinkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, LinkJson.Options);
        var frame = new byte[HeaderSize + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>
    /// 从缓冲区切一条消息。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 切出一条（<paramref name="consumed"/> 是这条占的字节数）；
    /// <c>false</c> + <paramref name="error"/> 为 null = 数据还不够，接着读；
    /// <c>false</c> + <paramref name="error"/> 非 null = 帧坏了，调用方应断开。
    /// </returns>
    public static bool TryRead(ReadOnlySpan<byte> buffer, out LinkMessage? message, out int consumed, out string? error)
    {
        message = null;
        consumed = 0;
        error = null;

        if (buffer.Length < HeaderSize) return false;

        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (length <= 0 || length > MaxFrameBytes)
        {
            error = $"帧长度不合法：{length}";
            return false;
        }

        if (buffer.Length < HeaderSize + length) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer.Slice(HeaderSize, length).ToArray());
            message = LinkMessage.From(document.RootElement);
            if (message == null)
            {
                error = "帧内容不是可识别的消息";
                return false;
            }

            consumed = HeaderSize + length;
            return true;
        }
        catch (JsonException ex)
        {
            error = "帧不是合法 JSON：" + ex.Message;
            return false;
        }
    }

    /// <summary>依次切出缓冲区里所有完整的帧（自测与批量处理用）。</summary>
    public static List<LinkMessage> ReadAll(ReadOnlySpan<byte> buffer, out string? error)
    {
        var messages = new List<LinkMessage>();
        error = null;

        while (TryRead(buffer, out LinkMessage? message, out int consumed, out error))
        {
            messages.Add(message!);
            buffer = buffer[consumed..];
        }

        return messages;
    }
}
