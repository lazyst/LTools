using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro;

/// <summary>一次翻译的配置（端点 / 模型 / 密钥 / 源与目标语言 / 超时）。</summary>
internal sealed record TranslationConfig(string Endpoint, string Model, string ApiKey, string Target,
    string Source, int TimeoutMs)
{
    /// <summary>端点与模型都填了才算能翻（密钥可以留空：本地网关常不带鉴权）。</summary>
    public bool IsReady => Endpoint.Length > 0 && Model.Length > 0;

    public static TranslationConfig Normalize(string? endpoint, string? model, string? apiKey,
        string? target, string? source, int timeoutMs) => new(
        (endpoint ?? "").Trim().TrimEnd('/'),
        (model ?? "").Trim(),
        (apiKey ?? "").Trim(),
        TranslationLanguages.Normalize(target),
        TranslationLanguages.NormalizeSource(source),
        Math.Clamp(timeoutMs <= 0 ? 30000 : timeoutMs, 1000, 120000));

    public static readonly TranslationConfig Empty = new("", "", "", TranslationLanguages.DefaultTarget,
        TranslationLanguages.Auto, 30000);
}

/// <summary>一次翻译的结果（失败时 <see cref="Error"/> 是能直接给人看的中文原因）。</summary>
internal sealed record TranslationResult(bool Ok, IReadOnlyList<string> Lines, string Error)
{
    public static readonly TranslationResult Failed = new(false, Array.Empty<string>(), "翻译失败");

    public static TranslationResult Fail(string error) => new(false, Array.Empty<string>(), error);

    public static TranslationResult Success(IReadOnlyList<string> lines) => new(true, lines, "");
}

/// <summary>
/// 识别结果的在线翻译：把 OCR 的每一行发给 OpenAI 兼容的 <c>/chat/completions</c>。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>translationservice</c> / <c>translationjob</c> 的语义：**按行翻译、行数对齐**，
/// 这样译文能摆回原来的行框里（<c>ScreenshotOcrTextLayer</c> 的 overlay translation）。
/// 上游自带 DeepL / 百度 / 有道等多套协议与私有云的快速翻译；这里只留一个 OpenAI 兼容端点
/// （自建网关、代理、本地模型都通），见 <c>docs/CAPTURE_PARITY.md</c> §5 的平台差异。
/// </para>
/// <para>
/// 请求 / 解析都是纯函数（<see cref="BuildBody"/> / <see cref="TryParseLines"/> /
/// <see cref="ExtractContent"/>），自测直接断言；网络那层只负责带超时与把状态码翻成人话。
/// 失败从不假装成功：调用方拿到 <c>Ok = false</c> 就把原因显示出来。
/// </para>
/// </remarks>
internal static class TranslationClient
{
    private const int MaxErrorText = 200;

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>请求体保留中文（默认编码器会把每个汉字转成 <c>\uXXXX</c>，既费字节也不便排查）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ============ 端点 / 请求 ============

    /// <summary>端点校验（照上游 <c>validTranslationEndpoint</c>：绝对 http(s)、有主机、不带 fragment）。</summary>
    public static bool ValidateEndpoint(string? endpoint, out string error)
    {
        error = "";
        string value = (endpoint ?? "").Trim();
        if (value.Length == 0)
        {
            error = "请填端点地址（例如 https://api.openai.com/v1）";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri == null)
        {
            error = "端点不是合法的网址";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "端点必须以 http:// 或 https:// 开头";
            return false;
        }

        if (uri.Host.Length == 0)
        {
            error = "端点缺少主机名";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = "端点不能带 #fragment";
            return false;
        }

        return true;
    }

    /// <summary><c>/chat/completions</c> 的完整地址：端点写没写这一段都认。</summary>
    public static string ChatUrl(string endpoint)
    {
        string value = (endpoint ?? "").Trim().TrimEnd('/');
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? value
            : value + "/chat/completions";
    }

    /// <summary>请求体：系统提示要求「同长度 JSON 字符串数组」，用户内容就是行数组本身。</summary>
    public static string BuildBody(TranslationConfig config, IReadOnlyList<string> lines)
    {
        var array = new JsonArray();
        foreach (string line in lines) array.Add(line ?? "");

        string target = TranslationLanguages.PromptOf(config.Target);
        string source = config.Source == TranslationLanguages.Auto
            ? "源语言请你按内容判断"
            : "源语言是 " + TranslationLanguages.PromptOf(config.Source);

        string system =
            "你是专业的翻译引擎。把用户给的 JSON 字符串数组里的每一项翻译成" + target + "；" + source +
            "。只输出一个 JSON 字符串数组：元素个数与输入相同、顺序不变、每项是一行译文；" +
            "不要解释、不要加任何前后缀、不要代码块。输入里的空字符串原样返回空字符串。";

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = array.ToJsonString(JsonOptions) },
        };

        var root = new JsonObject
        {
            ["model"] = config.Model,
            ["temperature"] = 0,
            ["stream"] = false,
            ["messages"] = messages,
        };

        return root.ToJsonString(JsonOptions);
    }

    // ============ 响应解析 ============

    /// <summary>从 OpenAI 兼容响应里取出正文（<c>choices[0].message.content</c>，兼容分段与旧式 <c>text</c>）。</summary>
    public static string? ExtractContent(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;

        try
        {
            JsonNode? root = JsonNode.Parse(responseBody);
            if (root is not JsonObject obj) return null;
            if (obj["choices"] is not JsonArray { Count: > 0 } choices) return null;
            if (choices[0] is not JsonObject choice) return null;

            if (choice["message"] is JsonObject message)
            {
                JsonNode? content = message["content"];
                if (content is JsonValue value && value.TryGetValue(out string? text)) return text;
                if (content is JsonArray parts)
                {
                    var sb = new StringBuilder();
                    foreach (JsonNode? part in parts)
                    {
                        if (part is JsonObject item && item["text"] is JsonValue partText &&
                            partText.TryGetValue(out string? piece))
                        {
                            sb.Append(piece);
                        }
                    }

                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }

            if (choice["text"] is JsonValue legacy && legacy.TryGetValue(out string? plain)) return plain;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>去掉 ```json 围栏（模型经常忍不住加）。</summary>
    public static string StripFence(string content)
    {
        string text = (content ?? "").Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        int firstLine = text.IndexOf('\n');
        if (firstLine < 0) return text.TrimStart('`').Trim();
        text = text[(firstLine + 1)..];
        int closing = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0) text = text[..closing];
        return text.Trim();
    }

    /// <summary>
    /// 把模型返回的正文解成 <paramref name="count"/> 行。先按 JSON 数组，再按行切（去掉「1. 」这类序号）；
    /// 行数对不上就是失败——宁可让人看见「行数不是 N」，也不把译文错位塞进别的行框。
    /// </summary>
    public static bool TryParseLines(string? content, int count, out IReadOnlyList<string> lines,
        out string error)
    {
        lines = Array.Empty<string>();
        error = "";
        if (count <= 0)
        {
            error = "没有要翻译的内容";
            return false;
        }

        string text = StripFence(content ?? "");
        if (text.Length == 0)
        {
            error = "模型返回了空内容";
            return false;
        }

        if (TryParseArray(text, count, out lines, out error)) return true;
        if (error.Length > 0) return false;

        // 只有一行：整段就是译文（模型有时直接回纯文本）
        if (count == 1)
        {
            lines = new[] { text.Replace("\r", "").Replace("\n", " ") };
            return true;
        }

        // 按行切，去掉行首的编号 / 列表记号
        var parts = new List<string>();
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = StripNumbering(raw);
            if (line.Length == 0 && parts.Count == 0) continue;
            parts.Add(line);
        }

        while (parts.Count > count && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        if (parts.Count == count)
        {
            lines = parts;
            return true;
        }

        error = string.Format(CultureInfo.CurrentCulture,
            "模型返回的译文是 {0} 行，与输入的 {1} 行对不上", parts.Count, count);
        return false;
    }

    private static bool TryParseArray(string text, int count, out IReadOnlyList<string> lines,
        out string error)
    {
        lines = Array.Empty<string>();
        error = "";
        if (text[0] != '[') return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            error = "模型返回的不是合法的 JSON 数组";
            return false;
        }

        if (node is not JsonArray array)
        {
            error = "模型返回的不是 JSON 数组";
            return false;
        }

        var result = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? line))
                result.Add(line.Replace("\r", "").Replace("\n", " "));
            else result.Add(item?.ToJsonString() ?? "");
        }

        if (result.Count != count)
        {
            error = string.Format(CultureInfo.CurrentCulture,
                "模型返回的译文是 {0} 项，与输入的 {1} 行对不上", result.Count, count);
            return false;
        }

        lines = result;
        return true;
    }

    /// <summary>行首的「1. 」「2、」「- 」这类序号去掉（按行切时的兜底）。</summary>
    public static string StripNumbering(string line)
    {
        string text = (line ?? "").Trim();
        int index = 0;
        while (index < text.Length && (char.IsDigit(text[index]))) index++;
        if (index > 0 && index < text.Length && (text[index] is '.' or '、' or ')' or '．'))
        {
            int start = index + 1;
            while (start < text.Length && text[start] == ' ') start++;
            return text[start..].Trim();
        }

        if (text.Length > 1 && (text[0] is '-' or '*' or '•') && text[1] == ' ')
            return text[2..].Trim();

        return text;
    }

    // ============ 网络 ============

    /// <summary>翻译一组行；取消（用户按取消 / 关窗）返回 <c>Failed</c> 且原因写「已取消」。</summary>
    public static async Task<TranslationResult> TranslateAsync(TranslationConfig config,
        IReadOnlyList<string> lines, string sourceKey, CancellationToken token)
    {
        if (!config.IsReady) return TranslationResult.Fail("还没配置翻译端点或模型");
        if (!ValidateEndpoint(config.Endpoint, out string endpointError))
            return TranslationResult.Fail(endpointError);
        if (lines.Count == 0) return TranslationResult.Fail("没有要翻译的内容");

        TranslationConfig request = config with
        {
            Source = TranslationLanguages.NormalizeSource(sourceKey),
        };

        string body = BuildBody(request, lines);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(config.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ChatUrl(config.Endpoint))
            {
                Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
            };

            if (config.ApiKey.Length > 0)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            using HttpResponseMessage response =
                await Http.SendAsync(message, HttpCompletionOption.ResponseContentRead, linked.Token)
                    .ConfigureAwait(false);

            string responseBody = await response.Content.ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return TranslationResult.Fail(Describe(response.StatusCode, responseBody));

            string? content = ExtractContent(responseBody);
            if (content == null)
                return TranslationResult.Fail("翻译端点返回的内容里没有 choices[0].message.content");

            return TryParseLines(content, lines.Count, out IReadOnlyList<string> translated,
                out string parseError)
                ? TranslationResult.Success(translated)
                : TranslationResult.Fail(parseError);
        }
        catch (OperationCanceledException)
        {
            return TranslationResult.Fail(token.IsCancellationRequested
                ? "已取消翻译"
                : string.Format(CultureInfo.CurrentCulture, "翻译超时（{0} 秒）",
                    Math.Max(1, config.TimeoutMs / 1000)));
        }
        catch (HttpRequestException ex)
        {
            return TranslationResult.Fail("连不上翻译端点：" + ex.Message);
        }
        catch (Exception ex)
        {
            return TranslationResult.Fail("翻译失败：" + ex.Message);
        }
    }

    /// <summary>状态码 → 人话（带一小段响应体，便于看出是键错了还是路径错了）。</summary>
    public static string Describe(HttpStatusCode status, string? body)
    {
        int code = (int)status;
        string snippet = Snippet(body);
        string reason = code switch
        {
            400 => "翻译请求被拒绝（400）",
            401 or 403 => "翻译端点拒绝了这个密钥（" + code + "）",
            404 => "端点路径不对（404），地址应填到 …/v1",
            429 => "翻译端点限流（429），稍后再试",
            >= 500 => "翻译端点出错（" + code + "）",
            _ => "翻译端点返回 " + code,
        };

        return snippet.Length > 0 ? reason + "：" + snippet : reason;
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        string text = body.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > MaxErrorText) text = text[..MaxErrorText] + "…";
        return text;
    }
}
