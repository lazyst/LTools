using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LTools.Core.Actions;
using LTools.Features.Units.Files;

namespace LTools.Features.Units.Web;

/// <summary>
/// 发一个 HTTP 请求（<c>web.request</c>）：调接口、取网页、下载文件。
/// </summary>
/// <remarks>
/// <para>
/// 这是「取网上的数据」的唯一入口，够日常用即可：GET/POST/PUT/PATCH/DELETE、自定义请求头、
/// 把响应体存成文件、JSON 响应顺手解析成对象（<c>.成员</c> 直接取）。
/// </para>
/// <para>
/// 用一个静态 <see cref="HttpClient"/>（连接池复用），超时按次用 <see cref="CancellationTokenSource"/>
/// 控制，并且和流程的停止令牌串在一起——按停止热键能立刻中断请求，不用等它超时。
/// </para>
/// </remarks>
public sealed class WebRequestUnit : IStepUnit
{
    private static readonly string[] Methods = { "get", "post", "put", "patch", "delete" };
    private static readonly string[] Charsets = { "auto", "utf8", "gbk" };

    private static readonly HttpClient Client = CreateClient();

    public UnitSpec Spec { get; } = new()
    {
        Key = "web.request",
        Name = "HTTP 请求",
        Category = "网络",
        Icon = "globe",
        Description = "发 HTTP 请求（GET/POST/PUT/PATCH/DELETE）；返回文本 / 状态码，JSON 顺手解析，也能直接存成文件",
        Inputs = new[]
        {
            new InputSpec("url", "地址", ValueKind.text, InputEditor.text, Required: true,
                Description: "完整地址，要带 http:// 或 https://；可以拼变量"),
            new InputSpec("method", "方法", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("get"), Options: Methods,
                Labels: new Dictionary<string, string>
                {
                    ["get"] = "GET 取",
                    ["post"] = "POST 提交",
                    ["put"] = "PUT 覆盖",
                    ["patch"] = "PATCH 局部改",
                    ["delete"] = "DELETE 删除",
                }),
            new InputSpec("body", "请求体", ValueKind.text, InputEditor.multiline,
                VisibleWhen: "method == 'post' || method == 'put' || method == 'patch' || method == 'delete'",
                Description: "JSON / 表单原文；GET 不带请求体"),
            new InputSpec("contentType", "内容类型", ValueKind.text,
                VisibleWhen: "method == 'post' || method == 'put' || method == 'patch' || method == 'delete'",
                Default: Value.FromText("application/json"),
                Description: "请求体的 Content-Type；表单用 application/x-www-form-urlencoded"),
            new InputSpec("headers", "请求头", ValueKind.text, InputEditor.multiline,
                Description: "每行一条，写成「名字: 值」，如 Authorization: Bearer xxx"),
            new InputSpec("saveTo", "存成文件", ValueKind.text, InputEditor.file,
                Description: "填了就边下边写文件（下载大文件用），响应文本输出为空；留空 = 读进内存"),
            new InputSpec("charset", "响应字符集", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("auto"), Options: Charsets, Advanced: true,
                Description: "自动 = 按响应头；中文站点没声明字符集时手选 GBK"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(30000), Advanced: true,
                Description: "超过就失败（按停止热键可以更早中断）"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "响应文本", ValueKind.text, "response",
                Description: "响应体文本；存成文件时为空"),
            new OutputSpec("status", "状态码", ValueKind.number, "status",
                Description: "HTTP 状态码（200 / 404 / 500…）"),
            new OutputSpec("ok", "成功", ValueKind.@bool,
                Description: "状态码是 2xx"),
            new OutputSpec("json", "JSON", ValueKind.@object,
                Description: "响应能解析成 JSON 时的对象（用 .成员 取值）；否则是空对象"),
            new OutputSpec("file", "文件", ValueKind.text,
                Description: "存成文件时的完整路径；没存为空"),
            new OutputSpec("size", "字节数", ValueKind.number,
                Description: "响应体字节数"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string url = args.Text("url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ActionException($"地址要写成完整的 http(s)://…（当前：{url}）");

        string method = args.Text("method").Trim().ToLowerInvariant();
        if (!Methods.Contains(method)) throw new ActionException($"未知方法：{method}");

        int timeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 1000, 600_000);
        string saveTo = args.Text("saveTo").Trim();

        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);
        string body = args.Text("body");
        if (method != "get" && body.Length > 0)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            string type = args.Text("contentType").Trim();
            if (type.Length == 0) type = "application/json";
            try
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            }
            catch (FormatException)
            {
                throw new ActionException($"内容类型写错了：{type}");
            }
            request.Content = content;
        }

        ApplyHeaders(request, args.Text("headers"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        cts.CancelAfter(timeoutMs);

        using HttpResponseMessage response = Send(request, cts, ctx, timeoutMs, url);
        int status = (int)response.StatusCode;

        string file = "";
        long size;
        string text;

        if (saveTo.Length > 0)
        {
            string path = FileText.Resolve(saveTo, "");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (var source = response.Content.ReadAsStream(cts.Token))
            using (var target = File.Create(path))
            {
                source.CopyTo(target);
                size = target.Length;
            }
            file = path;
            text = "";
        }
        else
        {
            byte[] bytes;
            try
            {
                bytes = response.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested)
            {
                throw new ActionException($"读取响应超时（{timeoutMs} ms）：{url}");
            }

            size = bytes.Length;
            text = Decode(bytes, args.Text("charset"), response);
        }

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("status", Value.FromNumber(status));
        ctx.SetOutput("ok", Value.FromBool(status is >= 200 and <= 299));
        ctx.SetOutput("json", ParseJson(text));
        ctx.SetOutput("file", Value.FromText(file));
        ctx.SetOutput("size", Value.FromNumber(size));
    }

    private static HttpResponseMessage Send(HttpRequestMessage request, CancellationTokenSource cts,
        StepContext ctx, int timeoutMs, string url)
    {
        try
        {
            return Client.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (ctx.Token.IsCancellationRequested)
        {
            throw new ActionAbortedException("已停止", userCancelled: true);
        }
        catch (OperationCanceledException)
        {
            throw new ActionException($"请求超时（{timeoutMs} ms）：{url}");
        }
        catch (HttpRequestException ex)
        {
            throw new ActionException($"请求失败：{ex.Message}（{url}）", ex);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string headers)
    {
        foreach (string raw in headers.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) throw new ActionException($"请求头要写成「名字: 值」：{line}");

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length == 0) throw new ActionException($"请求头名字为空：{line}");

            // 内容相关的头（Content-Type 等）由请求体自己带，这里只放普通头。
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string Decode(byte[] bytes, string charset, HttpResponseMessage response)
    {
        string name = (charset ?? "").Trim().ToLowerInvariant();
        if (name is "utf8" or "gbk") return FileText.GetEncoding(name).GetString(StripBom(bytes));

        string? header = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(header))
        {
            try { return Encoding.GetEncoding(header).GetString(StripBom(bytes)); }
            catch (ArgumentException) { /* 不认识的字符集名：落到 UTF-8 */ }
        }

        return FileText.GetEncoding("utf8").GetString(StripBom(bytes));
    }

    /// <summary>去掉 BOM，否则响应文本开头会多一个不可见字符（拼 JSON 时很坑）。</summary>
    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes[3..];
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return bytes[2..];
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return bytes[2..];
        return bytes;
    }

    private static Value ParseJson(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return Value.FromObject();

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return ValueJsonConverter.FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            // 响应不是 JSON（HTML 错误页之类）：不报错，让调用方按 text 处理。
            return Value.FromObject();
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LTools/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }
}
