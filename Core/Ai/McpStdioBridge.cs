using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LTools.Core.Ai;

/// <summary>
/// stdio 桥：<c>LTools.exe --mcp-stdio</c> 把 stdin 上的 MCP JSON-RPC 逐行转发给本机 HTTP 服务。
/// </summary>
/// <remarks>
/// <para>
/// 只支持 stdio 的 MCP 客户端（部分 IDE / CLI）用这个入口；它<b>不复制</b>动作库、也不并发写草稿——
/// 所有状态仍在正在运行的 LTools 进程里，桥只做字节转发。因此必须先启用「本机 MCP 服务」。
/// </para>
/// <para>
/// MCP 的 stdio 传输是「一行一条 JSON」，响应是 SSE（<c>data: {...}</c>），这里把 <c>data:</c> 后的
/// 内容原样写到 stdout；通知（202、无正文）不回写。stdout 只能有协议数据，错误一律走 stderr。
/// </para>
/// </remarks>
internal static class McpStdioBridge
{
    public const string Switch = "--mcp-stdio";

    /// <summary>是否以 stdio 桥模式启动（不建托盘、不装钩子、不抢单实例互斥体）。</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(a, "mcp-stdio", StringComparison.OrdinalIgnoreCase));

    /// <summary>阻塞转发，返回进程退出码（0 = stdin 关闭）。</summary>
    public static int Run()
    {
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch { /* 没有控制台句柄时忽略 */ }

        var settings = AiSettings.Load();
        if (!settings.Enabled)
        {
            Console.Error.WriteLine("LTools: MCP 服务未启用。先在 设置 → AI 接入 打开「启用本机 MCP 服务」。");
            return 2;
        }

        string url = $"http://127.0.0.1:{settings.Port}/mcp";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(line, Encoding.UTF8, "application/json"),
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);

                using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    WriteError(line, $"{(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? payload;
                while ((payload = reader.ReadLine()) != null)
                {
                    if (payload.StartsWith("data:", StringComparison.Ordinal))
                    {
                        string json = payload[5..].Trim();
                        if (json.Length == 0) continue;
                        Console.Out.WriteLine(json);
                        Console.Out.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError(line, ex.Message);
            }
        }

        return 0;
    }

    /// <summary>发不出去时按请求 id 回一条 JSON-RPC 错误，客户端不会无限等；解析不出 id 就只写 stderr。</summary>
    private static void WriteError(string requestLine, string message)
    {
        Console.Error.WriteLine("LTools stdio 桥：" + message);
        try
        {
            using var doc = JsonDocument.Parse(requestLine);
            if (!doc.RootElement.TryGetProperty("id", out var id)) return;

            string response = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                error = new { code = -32000, message },
            });
            Console.Out.WriteLine(response);
            Console.Out.Flush();
        }
        catch { /* 不是合法 JSON 就不回执 */ }
    }
}
