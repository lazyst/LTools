using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Floating;
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

        await ForwardingPhaseAsync();

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 磁贴转交（宿主里不执行动作） ============

    private static async Task ForwardingPhaseAsync()
    {
        Console.WriteLine();
        Console.WriteLine("== 磁贴转交 ==");

        string pipe = "LToolsLink-fwd-" + Guid.NewGuid().ToString("N")[..8];
        var recorded = new TaskCompletionSource<RunActionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource<OpenWindowRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operated = new TaskCompletionSource<ActionOpRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = new TaskCompletionSource<PanelConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subSaved = new TaskCompletionSource<SubActionSaveRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runStarted = new TaskCompletionSource<RunProgramRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTrace = new TaskCompletionSource<TraceBatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runDone = new TaskCompletionSource<RunDoneEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var added = new TaskCompletionSource<ActionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var macroStarted = new TaskCompletionSource<MacroRecordRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opsSeen = new ConcurrentQueue<ActionOpRequest>();

        var server = new LinkServer(pipe, "fwd-token",
            (_, request) =>
            {
                // 反向：提权进程 → 宿主的交互请求（宿主替它弹窗）。
                if (request.Method == UiMethods.Choice)
                    return Task.FromResult(LinkMessage.Response(request, new UiChoiceReply(true, 2, "第三项")));
                if (request.Method == UiMethods.TextWindow)
                    return Task.FromResult(LinkMessage.Response(request, new UiTextWindowReply(true, "窗口内容", false)));
                if (request.Method == UiMethods.SelectFile)
                    return Task.FromResult(LinkMessage.Response(request, new UiSelectFileReply(true, @"C:\a.txt")));
                if (request.Method == UiMethods.PickPoint)
                    return Task.FromResult(LinkMessage.Response(request, new UiPickPointReply(true, 1280, 720)));
                if (request.Method == DataMethods.Write)
                    return Task.FromResult(LinkMessage.Response(request, new DataWriteReply(true)));
                if (request.Method == NoteMethods.Write)
                    return Task.FromResult(LinkMessage.Response(request,
                        new NoteWriteReply(true, "标题", "2026-11-24 12:00", @"C:\速记\a.md", null)));
                if (request.Method == ActionMethods.SaveAll)
                    return Task.FromResult(LinkMessage.Response(request, new ActionSaveReply(true, null)));
                if (request.Method == SubActionMethods.Save)
                {
                    if (request.Read<SubActionSaveRequest>() is { } sub) subSaved.TrySetResult(sub);
                    return Task.FromResult(LinkMessage.Response(request, new SubActionReply(true, null)));
                }
                if (request.Method == SubActionMethods.Delete)
                    return Task.FromResult(LinkMessage.Response(request, new SubActionReply(true, null)));
                if (request.Method == LinkMethods.EventConfigReload)
                {
                    reloaded.TrySetResult(true);
                    return Task.FromResult(LinkMessage.Response(request));
                }
                return Task.FromResult(LinkMessage.Response(request, error: "宿主不支持该请求"));
            },
            message =>
            {
                // 通知（事件）走 onEvent，不走请求处理器：配置重读是一条「不答复」的广播。
                if (message.Method == LinkMethods.EventConfigReload) reloaded.TrySetResult(true);
                if (message.Method == RunMethods.EventTrace && message.Read<TraceBatchEvent>() is { } batch)
                    runTrace.TrySetResult(batch);
                if (message.Method == RunMethods.EventDone && message.Read<RunDoneEvent>() is { } done)
                    runDone.TrySetResult(done);
                if (message.Method is MacroMethods.EventStopped or MacroMethods.EventSaved)
                    MacroBridge.OnEvent(message);
            });
        server.Start();
        LinkClient? client = null;

        try
        {
            client = await LinkClient.ConnectWithRetryAsync(pipe, "fwd-token", "worker", totalMs: 4000,
                handler: (channel, request) =>
                {
                    if (request.Method == RunMethods.Program && request.Read<RunProgramRequest>() is { } runProgram)
                    {
                        runStarted.TrySetResult(runProgram);
                        // 执行器这边「跑完」之后：先攒一批轨迹、再发结果（与产品同一个顺序）。
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await channel.NotifyAsync(RunMethods.EventTrace, new TraceBatchEvent(
                                    runProgram.Token, new List<TraceEntryDto>
                                    {
                                        new("/steps/0", "第 1 行", 0, "flow.set", "设置变量", "ok", 8, null,
                                            new Dictionary<string, Value> { ["a"] = Value.FromText("甲") },
                                            new Dictionary<string, Value>(), 1, 1),
                                    }));
                                await channel.NotifyAsync(RunMethods.EventDone, new RunDoneEvent(
                                    runProgram.Token, new RunResultDto(true, false, null, null, "unknown", 1, 8,
                                        new Dictionary<string, Value>())));
                            }
                            catch (Exception ex) { Console.WriteLine("推试运行事件失败：" + ex.Message); }
                        });
                        return Task.FromResult(LinkMessage.Response(request, new RunProgramReply(true, null)));
                    }

                    // 宏录制（宿主 → 执行器）：录制要钩子，结束 / 存好按同一个 token 推回来。
                    if (request.Method == MacroMethods.Record
                        && request.Read<MacroRecordRequest>() is { } macroRecord)
                    {
                        macroStarted.TrySetResult(macroRecord);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await channel.NotifyAsync(MacroMethods.EventStopped,
                                    new MacroRecordEvent(macroRecord.Token));
                                await channel.NotifyAsync(MacroMethods.EventSaved,
                                    new MacroRecordEvent(macroRecord.Token, macroRecord.Id));
                            }
                            catch (Exception ex) { Console.WriteLine("推宏录制事件失败：" + ex.Message); }
                        });
                        return Task.FromResult(LinkMessage.Response(request, new MacroRecordReply(true, null)));
                    }

                    // 动作级写入（宿主 → 执行器）：内容属主在执行器，回一份保存后的动作。
                    if (request.Method == LinkMethods.ActionAdd)
                    {
                        ActionDto? dto = request.Read<ActionEditRequest>()?.Dto;
                        if (dto != null)
                        {
                            added.TrySetResult(dto);
                            dto.Id = "a-new";   // 执行器会给新动作补 Id
                        }
                        return Task.FromResult(LinkMessage.Response(request, new ActionEditReply(true, dto, null)));
                    }
                    if (request.Method == LinkMethods.ActionUpdate)
                        return Task.FromResult(LinkMessage.Response(request,
                            new ActionEditReply(false, null, "这个动作已经不存在了")));

                    if (request.Method == LinkMethods.RunAction && request.Read<RunActionRequest>() is { } run)
                        recorded.TrySetResult(run);
                    if (request.Method == LinkMethods.OpenWindow && request.Read<OpenWindowRequest>() is { } open)
                        opened.TrySetResult(open);
                    if (request.Method == LinkMethods.ActionOp && request.Read<ActionOpRequest>() is { } op)
                    {
                        opsSeen.Enqueue(op);
                        operated.TrySetResult(op);
                    }
                    if (request.Method == LinkMethods.PanelReplace && request.Read<PanelConfig>() is { } panel)
                        pushed.TrySetResult(panel);
                    return Task.FromResult(LinkMessage.Response(request));
                });

            PanelHostBridge.Attach(server);
            Check("宿主侧接管（不本地执行）", ActionExecutor.RunRef("01nothing", 11, 22, RunSource.Panel));

            Task done = await Task.WhenAny(recorded.Task, Task.Delay(2000));
            Check("请求转给了提权进程", ReferenceEquals(done, recorded.Task),
                ReferenceEquals(done, recorded.Task) ? null : "2 秒内没收到");

            RunActionRequest? got = recorded.Task.IsCompletedSuccessfully ? recorded.Task.Result : null;
            Check("引用 / 坐标 / 来源原样",
                got is { Reference: "01nothing", X: 11, Y: 22, Source: "Panel" }, got?.ToString());

            Check("打开窗口被接管", PanelHostBridge.TryOpen(LinkTargets.Settings));
            Task openedDone = await Task.WhenAny(opened.Task, Task.Delay(2000));
            Check("打开目标转给了提权进程", ReferenceEquals(openedDone, opened.Task),
                ReferenceEquals(openedDone, opened.Task) ? null : "2 秒内没收到");
            Check("打开目标原样", opened.Task.IsCompletedSuccessfully && opened.Task.Result.Target == LinkTargets.Settings,
                opened.Task.IsCompletedSuccessfully ? opened.Task.Result.ToString() : "");

            // ---- 悬浮 / 宏 / 暂存区：宿主里也统一走 panel.op（不在宿主里碰钩子 / 悬浮窗 / staging）----
            Check("悬浮被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.Float, Id: "01nothing")));
            Check("悬浮整组被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.FloatGroup, GroupId: "g1")));
            Check("试放宏被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.ReplayMacro, Id: "m1")));
            Check("录制宏被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.RecordMacro, Id: "m1", Name: "报表宏")));
            Check("在暂存区编辑被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.EditInStash, Id: "01nothing")));
            Check("暂存区打开草稿被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashOpen, Id: "d1")));
            Check("暂存区试运行被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashRun, Id: "d1")));
            Check("暂存区采纳被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashPromote, Id: "d1", Kind: "replace")));
            Check("暂存区删除被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashDelete, Id: "d1")));
            Check("暂存区清空被接管", PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashClear)));
            Check("AI 接入设置被接管", PanelHostBridge.TryOpen(LinkTargets.Ai));

            // 五项都真的到了对端（顺序不保证，按操作名收）。
            var seen = new List<string>();
            for (int i = 0; i < 40 && seen.Count < 10; i++)
            {
                await Task.Delay(50);
                seen = opsSeen.Select(o => o.Op).ToList();
            }

            Check("十个操作全部到达提权进程",
                seen.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(
                    new[]
                    {
                        PanelOps.Float, PanelOps.FloatGroup, PanelOps.ReplayMacro, PanelOps.RecordMacro,
                        PanelOps.EditInStash, PanelOps.StashOpen, PanelOps.StashRun, PanelOps.StashPromote,
                        PanelOps.StashDelete, PanelOps.StashClear,
                    }.OrderBy(s => s, StringComparer.Ordinal)),
                string.Join(",", seen));
            Check("宏 Id / 名字原样",
                opsSeen.Any(o => o.Op == PanelOps.RecordMacro && o.Id == "m1" && o.Name == "报表宏"));
            Check("采纳方式原样",
                opsSeen.Any(o => o.Op == PanelOps.StashPromote && o.Id == "d1" && o.Kind == "replace"));

            // ---- 悬浮状态：宿主从快照灌进来（菜单标签据此显示「取消悬浮」）----
            PanelHostBridge.SetFloating(new[] { "01nothing" });
            Check("宿主记住悬浮引用", PanelHostBridge.IsFloating("01nothing") && !PanelHostBridge.IsFloating("other"));
            PanelHostBridge.SetFloating(null);
            Check("悬浮引用可清空", !PanelHostBridge.IsFloating("01nothing"));

            // ---- 反方向：执行器找宿主弹窗（交互类动作单元）----
            LinkMessage choiceReply = await client.RequestAsync(UiMethods.Choice,
                new UiChoiceRequest("d1", "确认", "继续？", new List<string>(), new List<string> { "确定", "取消" },
                    SelectMode: false, TimeoutMs: 0), timeoutMs: 2000);
            UiChoiceReply? picked = choiceReply.Read<UiChoiceReply>();
            Check("交互请求发得出去（执行器 → 宿主）", choiceReply.Ok, choiceReply.Error);
            Check("交互结果能回来", picked is { Accepted: true, Index: 2, Value: "第三项" }, picked?.ToString());

            LinkMessage textReply = await client.RequestAsync(UiMethods.TextWindow,
                new UiTextWindowRequest("d2", "read", "k", "标题", "原文", Append: false, Wait: true,
                    Monospace: false), timeoutMs: 2000);
            Check("文本窗口请求发得出去", textReply.Ok, textReply.Error);
            Check("文本窗口内容能回来", textReply.Read<UiTextWindowReply>()?.Text == "窗口内容",
                textReply.Read<UiTextWindowReply>()?.ToString());

            LinkMessage fileReply = await client.RequestAsync(UiMethods.SelectFile,
                new UiSelectFileRequest("openFile", "", "", ""), timeoutMs: 2000);
            Check("选择文件请求发得出去", fileReply.Ok, fileReply.Error);
            Check("选择文件路径能回来", fileReply.Read<UiSelectFileReply>()?.Path == @"C:\a.txt",
                fileReply.Read<UiSelectFileReply>()?.ToString());

            LinkMessage pointReply = await client.RequestAsync(UiMethods.PickPoint,
                new UiPickPointRequest("d3", "点一下"), timeoutMs: 2000);
            Check("取点请求发得出去", pointReply.Ok, pointReply.Error);
            Check("取点坐标能回来", pointReply.Read<UiPickPointReply>() is { Ok: true, X: 1280, Y: 720 },
                pointReply.Read<UiPickPointReply>()?.ToString());

            LinkMessage dataReply = await client.RequestAsync(DataMethods.Write,
                new DataWriteRequest("settings.json", "{}"), timeoutMs: 2000);
            Check("配置写入请求发得出去（物理写者是宿主）", dataReply.Ok, dataReply.Error);
            Check("配置写入结果能回来", dataReply.Read<DataWriteReply>()?.Written == true,
                dataReply.Read<DataWriteReply>()?.ToString());

            LinkMessage noteReply = await client.RequestAsync(NoteMethods.Write,
                new NoteWriteRequest { Body = "正文", Title = "标题" }, timeoutMs: 2000);
            Check("写速记请求发得出去（写者是宿主）", noteReply.Ok, noteReply.Error);
            Check("写速记结果能回来", noteReply.Read<NoteWriteReply>()?.Title == "标题",
                noteReply.Read<NoteWriteReply>()?.ToString());

            LinkMessage actionReply = await client.RequestAsync(ActionMethods.SaveAll,
                new ActionSaveRequest(new List<ActionDto> { new() { Id = "a1", Name = "n" } }),
                timeoutMs: 2000);
            Check("动作库写入请求发得出去（写者是宿主）", actionReply.Ok, actionReply.Error);
            Check("动作库写入结果能回来", actionReply.Read<ActionSaveReply>()?.Ok == true,
                actionReply.Read<ActionSaveReply>()?.ToString());

            LinkMessage subSaveReply = await client.RequestAsync(SubActionMethods.Save,
                new SubActionSaveRequest(new SubActionDef { Id = "s1", Name = "填表" }, CreateNew: true),
                timeoutMs: 2000);
            Check("子动作写入请求发得出去（写者是宿主）", subSaveReply.Ok, subSaveReply.Error);
            Check("子动作写入结果能回来", subSaveReply.Read<SubActionReply>()?.Ok == true,
                subSaveReply.Read<SubActionReply>()?.ToString());
            Task subSavedDone = await Task.WhenAny(subSaved.Task, Task.Delay(2000));
            Check("子动作整份定义都送到了对端", ReferenceEquals(subSavedDone, subSaved.Task),
                ReferenceEquals(subSavedDone, subSaved.Task) ? null : "2 秒内没收到");
            Check("子动作 id / 名称 / 新建标记原样",
                subSaved.Task.IsCompletedSuccessfully && subSaved.Task.Result is { CreateNew: true } sent
                    && sent.Def.Id == "s1" && sent.Def.Name == "填表",
                subSaved.Task.IsCompletedSuccessfully ? subSaved.Task.Result?.ToString() : null);

            LinkMessage subDeleteReply = await client.RequestAsync(SubActionMethods.Delete,
                new SubActionDeleteRequest("s1"), timeoutMs: 2000);
            Check("子动作删除请求发得出去（写者是宿主）", subDeleteReply.Ok, subDeleteReply.Error);
            Check("子动作删除结果能回来", subDeleteReply.Read<SubActionReply>()?.Ok == true,
                subDeleteReply.Read<SubActionReply>()?.ToString());

            // 配置整份重读：导入备份 / 「重新加载」之后宿主那份只读副本要跟着换。
            await client.NotifyAsync(LinkMethods.EventConfigReload);
            Task reloadDone = await Task.WhenAny(reloaded.Task, Task.Delay(2000));
            Check("配置重读通知送到了宿主", ReferenceEquals(reloadDone, reloaded.Task),
                ReferenceEquals(reloadDone, reloaded.Task) ? null : "2 秒内没收到");

            // 跨进程试运行：宿主发程序 → 执行器跑 → 轨迹与结果按事件回来。
            LinkMessage runReply = await server.RequestAsync(RunMethods.Program,
                new RunProgramRequest("t9", "冒烟试运行", new ActionProgram(), null, null, false, "Editor"),
                timeoutMs: 3000);
            Check("试运行请求送到执行器", runReply.Ok && runReply.Read<RunProgramReply>()?.Started == true,
                runReply.Ok ? runReply.Read<RunProgramReply>()?.Error : runReply.Error);

            Task startedDone = await Task.WhenAny(runStarted.Task, Task.Delay(2000));
            Check("执行器收到了程序本体（token 原样）", ReferenceEquals(startedDone, runStarted.Task)
                && runStarted.Task.IsCompletedSuccessfully && runStarted.Task.Result.Token == "t9",
                ReferenceEquals(startedDone, runStarted.Task) ? null : "2 秒内没收到");

            Task traceArrived = await Task.WhenAny(runTrace.Task, Task.Delay(2000));
            Check("轨迹推回了宿主", ReferenceEquals(traceArrived, runTrace.Task),
                ReferenceEquals(traceArrived, runTrace.Task) ? null : "2 秒内没收到");
            Check("轨迹里的值原样",
                runTrace.Task.IsCompletedSuccessfully
                && runTrace.Task.Result.Entries[0].ToTrace().Outputs["a"].AsText() == "甲",
                runTrace.Task.IsCompletedSuccessfully ? runTrace.Task.Result.Entries[0].Path : null);

            Task runDoneArrived = await Task.WhenAny(runDone.Task, Task.Delay(2000));
            Check("结果推回了宿主", ReferenceEquals(runDoneArrived, runDone.Task)
                && runDone.Task.IsCompletedSuccessfully && runDone.Task.Result.Token == "t9",
                ReferenceEquals(runDoneArrived, runDone.Task) ? null : "2 秒内没收到");

            // 宏录制：编辑器在宿主里，录制要在执行器（钩子那侧）做，结束 / 存好按 token 回来。
            MacroBridge.Attach(server);
            var macroStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var macroSaved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            MacroBridge.Stopped += () => macroStopped.TrySetResult(true);
            MacroBridge.Saved += id => macroSaved.TrySetResult(id);

            bool macroOk = MacroBridge.StartInto("m1", "冒烟宏", out string? macroError);
            Check("宏录制请求送到了执行器", macroOk, macroError);
            Check("宿主记着「正在录制」", MacroBridge.IsRecording);

            Task macroStartDone = await Task.WhenAny(macroStarted.Task, Task.Delay(2000));
            Check("执行器收到宏 Id / 名字", ReferenceEquals(macroStartDone, macroStarted.Task)
                && macroStarted.Task.Result.Id == "m1" && macroStarted.Task.Result.Name == "冒烟宏",
                ReferenceEquals(macroStartDone, macroStarted.Task) ? null : "2 秒内没收到");

            Task macroStopDone = await Task.WhenAny(macroStopped.Task, Task.Delay(2000));
            Check("录制结束通知回来了", ReferenceEquals(macroStopDone, macroStopped.Task),
                ReferenceEquals(macroStopDone, macroStopped.Task) ? null : "2 秒内没收到");
            Check("结束之后不再算录制中", !MacroBridge.IsRecording);

            Task macroSavedDone = await Task.WhenAny(macroSaved.Task, Task.Delay(2000));
            Check("宏已保存通知回来了", ReferenceEquals(macroSavedDone, macroSaved.Task)
                && macroSaved.Task.Result == "m1",
                ReferenceEquals(macroSavedDone, macroSaved.Task) ? null : "2 秒内没收到");

            // 动作级写入：宿主里的编辑器保存 → 执行器改内存 + 落盘（回一个带 Id 的动作）。
            ActionEditBridge.Attach(server);
            bool addOk = ActionEditBridge.Add(new ActionDto { Name = "宿主建的动作" },
                out ActionDto? addBack, out string? addError);
            Check("动作新建请求送到了执行器", addOk, addError);
            Check("执行器回的动作带 Id", addOk && addBack?.Id == "a-new", addBack?.Id);

            Task addedDone = await Task.WhenAny(added.Task, Task.Delay(2000));
            Check("执行器收到的是整份动作（名称原样）", ReferenceEquals(addedDone, added.Task)
                && added.Task.IsCompletedSuccessfully && added.Task.Result.Name == "宿主建的动作",
                ReferenceEquals(addedDone, added.Task) ? null : "2 秒内没收到");

            bool updateOk = ActionEditBridge.Update(new ActionDto { Id = "a-new", Name = "改过" }, out string? updateError);
            Check("执行器拒绝时宿主拿得到原因", !updateOk && updateError == "这个动作已经不存在了", updateError);

            // ---- 面板结构保存：宿主 Save 变成整份替换 ----
            Check("面板结构保存被接管", PanelHostBridge.TryPushPanel(new PanelConfig()));
            Task pushDone = await Task.WhenAny(pushed.Task, Task.Delay(2000));
            Check("整份面板配置转给了提权进程", ReferenceEquals(pushDone, pushed.Task),
                ReferenceEquals(pushDone, pushed.Task) ? null : "2 秒内没收到");
        }
        catch (Exception ex)
        {
            Check("转交阶段的冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            PanelHostBridge.Detach();
            if (client != null) await client.DisposeAsync();
            await server.DisposeAsync();
        }
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

            // ---- 快照带「正在悬浮的引用」（宿主菜单的「悬浮 / 取消悬浮」靠它）----
            FloatingLibrary.Items.Add(new FloatingItemDto
            {
                Id = "f1",
                Members = { new FloatingMemberDto { ActionId = original.Id } },
            });
            try
            {
                LinkMessage floatSnap = await server.RequestAsync(LinkMethods.PanelSnapshot, timeoutMs: 2000);
                Check("快照带悬浮引用", floatSnap.Read<PanelSnapshot>()?.Floating.Contains(original.Id) == true);
            }
            finally
            {
                FloatingLibrary.Items.Clear();
            }

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
                new ActionEditRequest(new ActionDto { Name = "宿主新建的动作", Icon = "star" }), timeoutMs: 2000);
            ActionDto? created = added.Read<ActionEditReply>()?.Action;
            Check("新建动作成功", added.Ok && !string.IsNullOrEmpty(created?.Id), added.Error);
            Check("新建动作落盘", File.Exists(Path.Combine(root, "actions", created!.Id, "action.json")));
            Check("新建动作默认图标沿用传入", created.Icon == "star");

            // ---- 复制动作 ----
            LinkMessage duplicated = await server.RequestAsync(LinkMethods.ActionDuplicate,
                new ActionEditRequest(Id: original.Id), timeoutMs: 2000);
            ActionDto? copy = duplicated.Read<ActionEditReply>()?.Action;
            Check("复制动作成功", duplicated.Ok && copy?.Id != original.Id, duplicated.Error);
            Check("副本名字带「副本」", (copy?.Name ?? "").Contains("副本"), copy?.Name);

            // ---- 删除动作（连面板引用一起清）----
            LinkMessage deleted = await server.RequestAsync(LinkMethods.ActionDelete,
                new ActionEditRequest(Id: original.Id), timeoutMs: 2000);
            Check("删除动作成功", deleted.Ok && deleted.Read<ActionEditReply>()?.Ok == true, deleted.Error);
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
