using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using LTools.Config;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Panel;

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
                message => _clientEvents.Enqueue(message.Method), totalMs: 4000,
                handler: (_, request) => Task.FromResult(
                    LinkMessage.Response(request, new Echo("client:" + request.Method))));

            Check("握手成功、服务端认为已连接", server.IsConnected);

            // ---- 请求 / 答复 ----
            LinkMessage reply = await client.RequestAsync("echo", new Echo("你好「世界」\\x"), timeoutMs: 2000);
            Check("请求拿到答复", reply.Ok, reply.Error);
            Check("答复负载原样", reply.Read<Echo>()?.Text == "你好「世界」\\x", reply.Read<Echo>()?.Text);

            // ---- 反向请求：服务端向客户端要东西（生产里「宿主做服务端、提权进程做客户端」正是这样）----
            LinkMessage reverse = await server.RequestAsync("panel.snapshot", timeoutMs: 2000);
            Check("服务端能向客户端请求（反向角色）", reverse.Ok && reverse.Read<Echo>()?.Text == "client:panel.snapshot",
                reverse.Error ?? reverse.Read<Echo>()?.Text);

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

        await PanelPhaseAsync();

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 面板命令（提权侧的写属主） ============

    private static async Task PanelPhaseAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-linkpanel-" + Guid.NewGuid().ToString("N")[..8]);
        string configPath = Path.Combine(root, "LTools.json");
        string pipe = "LToolsLink-panel-" + Guid.NewGuid().ToString("N")[..8];
        LinkServer? server = null;
        LinkClient? client = null;

        Console.WriteLine();
        Console.WriteLine("== 面板命令 ==");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "configs"));
            ConfigStore.Initialize(configPath);

            // 先放一个动作 + 一个分组，快照才有东西。
            var original = new ActionDto { Name = "面板冒烟动作", Icon = "app" };
            Check("提权侧能加动作", ConfigStore.AddAction(original), original.Id);
            ConfigStore.Save();

            var panel = PanelLibrary.Current;
            panel.Global.Scenes.Clear();
            panel.Global.Scenes.Add(new PanelSceneDto
            {
                Id = "s1", Name = "全局", Groups =
                {
                    new PanelGroupDto { Id = "g1", Name = "常用", Actions = { original.Id } },
                },
            });
            Check("面板配置落盘", PanelLibrary.Save());

            // 生产方向：宿主（服务端）把请求发给提权进程（客户端），提权进程用 PanelHost 应答。
            server = new LinkServer(pipe, "panel-token",
                static (_, request) => Task.FromResult(LinkMessage.Response(request, error: "宿主暂不支持该命令")));
            server.Start();
            client = await LinkClient.ConnectWithRetryAsync(pipe, "panel-token", "worker", totalMs: 4000,
                handler: PanelHost.HandleAsync);
            await client.NotifyAsync(LinkMethods.EventClientReady);

            // ---- 快照 ----
            LinkMessage snapMessage = await server.RequestAsync(LinkMethods.PanelSnapshot, timeoutMs: 2000);
            PanelSnapshot? snap = snapMessage.Read<PanelSnapshot>();
            Check("快照答复成功", snapMessage.Ok, snapMessage.Error);
            Check("快照带动作", snap?.Actions.Any(a => a.Id == original.Id && a.Name == "面板冒烟动作") == true,
                string.Join(",", snap?.Actions.Select(a => a.Name) ?? new List<string>()));
            Check("快照带分组", snap?.Panel.Global.Scenes.FirstOrDefault()?.Groups.FirstOrDefault()?.Name == "常用");

            // ---- 整份替换（宿主改分组名）----
            PanelConfig edited = snap!.Panel;
            edited.Global.Scenes[0].Groups[0].Name = "改名后";
            LinkMessage replaced = await server.RequestAsync(LinkMethods.PanelReplace, edited, timeoutMs: 2000);
            Check("面板替换成功", replaced.Ok, replaced.Error);
            Check("替换写进了内存", PanelLibrary.Current.Global.Scenes[0].Groups[0].Name == "改名后");
            Check("替换写进了磁盘",
                File.ReadAllText(Path.Combine(root, "configs", "panel.json")).Contains("改名后"));

            // ---- 新建动作 ----
            LinkMessage added = await server.RequestAsync(LinkMethods.ActionAdd,
                new ActionAddRequest("宿主新建的动作", "star"), timeoutMs: 2000);
            ActionDto? created = added.Read<ActionDto>();
            Check("新建动作成功", added.Ok && !string.IsNullOrEmpty(created?.Id), added.Error);
            Check("新建动作落盘", File.Exists(Path.Combine(root, "actions", created!.Id, "action.json")));
            Check("新建动作默认图标沿用传入", created.Icon == "star");

            // ---- 复制动作 ----
            LinkMessage duplicated = await server.RequestAsync(LinkMethods.ActionDuplicate,
                new ActionIdRequest(original.Id), timeoutMs: 2000);
            ActionDto? copy = duplicated.Read<ActionDto>();
            Check("复制动作成功", duplicated.Ok && copy?.Id != original.Id, duplicated.Error);
            Check("副本名字带「副本」", (copy?.Name ?? "").Contains("副本"), copy?.Name);

            // ---- 删除动作（连面板引用一起清）----
            LinkMessage deleted = await server.RequestAsync(LinkMethods.ActionDelete,
                new ActionIdRequest(original.Id), timeoutMs: 2000);
            Check("删除动作成功", deleted.Ok, deleted.Error);
            Check("删除后动作文件没了", !File.Exists(Path.Combine(root, "actions", original.Id, "action.json")));
            Check("删除后面板引用也没了",
                PanelLibrary.Current.Global.Scenes[0].Groups[0].Actions.Contains(original.Id) == false);

            // ---- 未知命令 ----
            LinkMessage unknown = await server.RequestAsync("panel.nope", timeoutMs: 2000);
            Check("未知命令回失败答复", !unknown.Ok && (unknown.Error ?? "").Contains("未知命令"), unknown.Error);
        }
        catch (Exception ex)
        {
            Check("面板阶段的冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            if (client != null) await client.DisposeAsync();
            if (server != null) await server.DisposeAsync();

            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* 临时目录 */ }
        }
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
