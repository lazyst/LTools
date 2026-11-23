using System.Collections.Concurrent;
using LTools.Core.Link;

namespace LinkSmoke;

/// <summary>
/// 进程拆分第一步的环回冒烟：真命名管道上的握手、请求 / 答复、事件、超时、大负载、断开重连。
/// </summary>
internal static class Program
{
    private sealed record Echo(string Text);

    private static int _pass, _fail;
    private static readonly ConcurrentQueue<string> _serverEvents = new();
    private static readonly ConcurrentQueue<string> _clientEvents = new();

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string pipe = "LToolsLink-test-" + Guid.NewGuid().ToString("N")[..8];
        var server = new LinkServer(pipe, "token-1", Handle, message => _serverEvents.Enqueue(message.Method));
        server.Start();

        LinkClient? client = null;
        try
        {
            // ---- 令牌不对（服务端此刻空闲）：被拒绝，连接不成立 ----
            bool rejected = false;
            try { await LinkClient.ConnectAsync(pipe, "wrong-token", "host", timeoutMs: 2000); }
            catch (LinkException ex) { rejected = (ex.Message ?? "").Contains("令牌"); }
            Check("令牌不对被拒绝", rejected);

            await Wait(() => !server.IsConnected);

            client = await LinkClient.ConnectWithRetryAsync(pipe, "token-1", "host",
                message => _clientEvents.Enqueue(message.Method), totalMs: 4000);

            Check("握手成功、服务端认为已连接", server.IsConnected);

            // ---- 请求 / 答复 ----
            LinkMessage reply = await client.RequestAsync("echo", new Echo("你好「世界」\\x"), timeoutMs: 2000);
            Check("请求拿到答复", reply.Ok, reply.Error);
            Check("答复负载原样", reply.Read<Echo>()?.Text == "你好「世界」\\x", reply.Read<Echo>()?.Text);

            // ---- 服务端 → 客户端事件 ----
            await server.NotifyAsync("trace", new { step = 3 });
            Check("客户端收到服务端事件", await Wait(() => _clientEvents.Contains("trace")), string.Join(",", _clientEvents));

            // ---- 客户端 → 服务端事件 ----
            await client.NotifyAsync("panel", new { pinned = true });
            Check("服务端收到客户端事件", await Wait(() => _serverEvents.Contains("panel")), string.Join(",", _serverEvents));

            // ---- 服务端抛错 → 答复 ok=false ----
            LinkMessage boom = await client.RequestAsync("boom", timeoutMs: 2000);
            Check("服务端异常变成失败答复", !boom.Ok && (boom.Error ?? "").Contains("故意失败"), boom.Error);

            // ---- 超时 ----
            var started = Environment.TickCount64;
            bool timedOut = false;
            try { await client.RequestAsync("slow", timeoutMs: 150); }
            catch (TimeoutException) { timedOut = true; }
            Check("请求超时抛 TimeoutException", timedOut);
            Check("超时按设定时间返回（没被拖住）", Environment.TickCount64 - started < 2000, $"{Environment.TickCount64 - started} ms");

            // 超时之后连接还能用（在等的请求表清干净了）
            LinkMessage after = await client.RequestAsync("echo", new Echo("after"), timeoutMs: 2000);
            Check("超时之后连接照常", after.Ok && after.Read<Echo>()?.Text == "after");

            // ---- 1 MB 负载：缓冲会涨、帧要完整 ----
            string big = new string('a', 1024 * 1024);
            LinkMessage huge = await client.RequestAsync("echo", new Echo(big), timeoutMs: 8000);
            Check("1 MB 负载往返完整", huge.Ok && huge.Read<Echo>()?.Text.Length == big.Length,
                (huge.Read<Echo>()?.Text.Length ?? -1).ToString());

            // ---- 服务端断开 ----
            bool closedRaised = false;
            client.Closed += _ => closedRaised = true;

            await server.StopAsync();
            Check("服务端断开后客户端看到关闭", await Wait(() => !client.IsOpen));
            Check("客户端断开事件触发", await Wait(() => closedRaised));

            // ---- 重启后重连 ----
            var restarted = new LinkServer(pipe, "token-1", Handle);
            restarted.Start();
            LinkClient? again = null;
            try
            {
                again = await LinkClient.ConnectWithRetryAsync(pipe, "token-1", "host", totalMs: 4000);
                LinkMessage ok = await again.RequestAsync("echo", new Echo("again"), timeoutMs: 2000);
                Check("服务端重启后能重连并请求", ok.Ok && ok.Read<Echo>()?.Text == "again");
            }
            finally
            {
                if (again != null) await again.DisposeAsync();
                await restarted.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Check("冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            await server.DisposeAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task<LinkMessage> Handle(LinkChannel channel, LinkMessage request)
    {
        switch (request.Method)
        {
            case "echo":
                return LinkMessage.Response(request, request.Read<Echo>());

            case "slow":
                await Task.Delay(600);
                return LinkMessage.Response(request, new Echo("slow"));

            case "boom":
                throw new InvalidOperationException("故意失败");

            default:
                return LinkMessage.Response(request, error: "未知请求：" + request.Method);
        }
    }

    private static async Task<bool> Wait(Func<bool> condition, int timeoutMs = 2000)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
    }
}
