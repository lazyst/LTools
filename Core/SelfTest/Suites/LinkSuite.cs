using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using LTools.Config;
using LTools.Core.Link;

namespace LTools.Core.SelfTest;

/// <summary>
/// 进程间链路的纯逻辑：消息形状、帧切分（拆包 / 粘包 / 坏帧）、管道名。
/// 真管道收发靠 <c>tests/LinkSmoke</c>。
/// </summary>
internal static class LinkSuite
{
    private sealed record Demo(string Name, int Count);

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("link", "请求 / 答复 / 通知的 JSON 形状", Shapes));
        cases.Add(new("link", "负载往返：中文 / 引号 / 嵌套 / 强类型读取", Payloads));
        cases.Add(new("link", "粘包：一个缓冲区里两条帧逐条切出", Sticky));
        cases.Add(new("link", "拆包：头部 / 半帧都不算完整", Split));
        cases.Add(new("link", "坏帧：长度非法 / 不是 JSON / 缺方法名", BadFrames));
        cases.Add(new("link", "帧长度前缀就是负载字节数", Prefix));
        cases.Add(new("link", "管道名按会话区分", PipeName));
        cases.Add(new("link", "面板命令的负载形状（快照 / 新建 / 定位 / 显示）", PanelCommands));
    }

    private static void Shapes()
    {
        LinkMessage request = LinkMessage.Request(7, "run", new { actionId = "a1" });
        Check.Equal(7, request.Id, "请求带 id");
        Check.Equal(LinkKinds.Request, request.Kind, "kind=request");
        Check.Equal("run", request.Method, "method 原样");
        Check.True(request.Ok, "请求默认 ok");

        LinkMessage ok = LinkMessage.Response(request, new { started = true });
        Check.Equal(7, ok.Id, "答复沿用请求 id");
        Check.Equal(LinkKinds.Response, ok.Kind, "kind=response");
        Check.Equal("", ok.Method, "答复不重复方法名");
        Check.True(ok.Ok, "成功答复 ok=true");
        Check.True(ok.Error == null, "成功不带 error");

        LinkMessage failed = LinkMessage.Response(request, error: "动作不存在");
        Check.False(failed.Ok, "失败答复 ok=false");
        Check.Equal("动作不存在", failed.Error, "错误原文带回");

        LinkMessage evt = LinkMessage.Event("trace", new { step = 2 });
        Check.Equal(0, evt.Id, "通知不带 id");
        Check.Equal(LinkKinds.Event, evt.Kind, "kind=event");
    }

    private static void Payloads()
    {
        var payload = new Demo("季度「报表」\\x", 3);
        LinkMessage message = RoundTrip(LinkMessage.Request(1, "load", payload));

        Demo? back = message.Read<Demo>();
        Check.True(back != null, "读回对象");
        Check.Equal(payload.Name, back!.Name, "中文 / 引号 / 反斜杠原样");
        Check.Equal(payload.Count, back.Count, "数字原样");

        LinkMessage empty = RoundTrip(LinkMessage.Request(2, "ping"));
        Check.True(empty.Payload == null, "没有负载时不带 payload 字段");
        Check.True(empty.Read<Demo>() == null, "读空负载返回 null");
    }

    private static void Sticky()
    {
        byte[] one = LinkFraming.Frame(LinkMessage.Request(1, "a"));
        byte[] two = LinkFraming.Frame(LinkMessage.Request(2, "b"));

        var buffer = new byte[one.Length + two.Length];
        one.CopyTo(buffer, 0);
        two.CopyTo(buffer, one.Length);

        List<LinkMessage> messages = LinkFraming.ReadAll(buffer, out string? error);
        Check.True(error == null, "没有坏帧");
        Check.Equal(2, messages.Count, "两条都切出来");
        Check.Equal(1, messages[0].Id, "第一条 id");
        Check.Equal("b", messages[1].Method, "第二条方法名");
    }

    private static void Split()
    {
        byte[] frame = LinkFraming.Frame(LinkMessage.Request(9, "run", new { text = new string('x', 200) }));

        Check.False(LinkFraming.TryRead(frame.AsSpan(0, 2), out _, out int consumed, out string? error), "只有 2 字节：不够");
        Check.True(error == null, "不够时不算坏帧");
        Check.Equal(0, consumed, "不够时没消费");

        Check.False(LinkFraming.TryRead(frame.AsSpan(0, frame.Length - 1), out _, out consumed, out error), "少 1 字节：不够");
        Check.True(error == null, "少 1 字节也不算坏");

        Check.True(LinkFraming.TryRead(frame, out LinkMessage? message, out consumed, out error), "完整帧能切");
        Check.True(message != null && message.Id == 9, "id 对");
        Check.Equal(frame.Length, consumed, "消费整帧");
    }

    private static void BadFrames()
    {
        byte[] zero = Prefix(0);
        Check.False(LinkFraming.TryRead(zero, out _, out _, out string? error), "长度 0 拒绝");
        Check.True(error != null, "长度 0 报错");

        byte[] negative = Prefix(-5);
        Check.False(LinkFraming.TryRead(negative, out _, out _, out error), "负长度拒绝");
        Check.True(error != null, "负长度报错");

        byte[] huge = Prefix(LinkFraming.MaxFrameBytes + 1);
        Check.False(LinkFraming.TryRead(huge, out _, out _, out error), "超过上限拒绝");
        Check.True(error != null, "超过上限报错");

        byte[] notJson = Frame("这不是 JSON");
        Check.False(LinkFraming.TryRead(notJson, out _, out _, out error), "非 JSON 拒绝");
        Check.True(error != null, "非 JSON 报错");

        byte[] noMethod = Frame("""{"kind":"request","id":1}""");
        Check.False(LinkFraming.TryRead(noMethod, out _, out _, out error), "请求没有方法名拒绝");
        Check.True(error != null, "缺方法名报错");

        byte[] unknownKind = Frame("""{"kind":"weird","id":1,"method":"x"}""");
        Check.False(LinkFraming.TryRead(unknownKind, out _, out _, out error), "未知 kind 拒绝");
        Check.True(error != null, "未知 kind 报错");
    }

    private static void Prefix()
    {
        byte[] frame = LinkFraming.Frame(LinkMessage.Request(1, "x", new { k = "v" }));
        int declared = BitConverter.ToInt32(frame, 0);
        Check.Equal(frame.Length - LinkFraming.HeaderSize, declared, "前缀 = 负载字节数");
    }

    private static void PipeName()
    {
        string one = LinkNames.Pipe(1);
        string two = LinkNames.Pipe(2);
        Check.True(one.StartsWith(LinkNames.PipePrefix, StringComparison.Ordinal), "带固定前缀（裸名，系统加 \\\\.\\pipe\\）");
        Check.True(one != two, "不同会话不同管道");
    }

    private static void PanelCommands()
    {
        // 方法名互不相同、都不为空（拼错一个常量会让两端对不上）。
        var names = new[]
        {
            LinkMethods.PanelSnapshot, LinkMethods.PanelReplace, LinkMethods.ActionAdd,
            LinkMethods.ActionDelete, LinkMethods.ActionDuplicate,
            LinkMethods.RunAction, LinkMethods.OpenWindow, LinkMethods.ActionOp,
            LinkMethods.EventPanelShow, LinkMethods.EventPanelToggle, LinkMethods.EventClientReady,
            LinkMethods.EventActionsChanged, LinkMethods.EventPanelStash, LinkMethods.EventPanelState,
            LinkMethods.EventPanelClose,
        };
        Check.Equal(names.Length, new HashSet<string>(names, StringComparer.Ordinal).Count, "方法名互不相同");
        Check.True(names.All(n => n.Length > 0), "方法名都不为空");

        // 面板状态：宿主把「开着 / 关了」推给提权进程的钩子。
        PanelHostBridge.SetRemotePanelOpen(true);
        Check.True(PanelHostBridge.RemotePanelOpen, "宿主面板状态：开");
        PanelHostBridge.SetRemotePanelOpen(false);
        Check.False(PanelHostBridge.RemotePanelOpen, "宿主面板状态：关");
        PanelStateEvent? state = RoundTrip(LinkMessage.Request(13, LinkMethods.EventPanelState,
            new PanelStateEvent(true))).Read<PanelStateEvent>();
        Check.True(state?.Open == true, "面板状态往返：开着");

        // —— 交互服务（执行器 → 宿主）——
        var uiNames = new[] { UiMethods.Input, UiMethods.Choice, UiMethods.Cancel,
            UiMethods.TextWindow, UiMethods.SelectFile, UiMethods.PickPoint };
        Check.Equal(uiNames.Length, new HashSet<string>(uiNames, StringComparer.Ordinal).Count, "交互方法名互不相同");
        Check.True(uiNames.All(n => n.StartsWith("ui.", StringComparison.Ordinal)), "交互方法名都有 ui. 前缀");

        UiInputRequest? input = RoundTrip(LinkMessage.Request(14, UiMethods.Input,
            new UiInputRequest("d1", "标题", "请输入名字", "默认", Multiline: true))).Read<UiInputRequest>();
        Check.Equal("d1", input?.DialogId, "要文本：对话 Id 原样");
        Check.Equal("请输入名字", input?.Prompt, "要文本：提示原样");
        Check.True(input?.Multiline == true, "要文本：多行原样");

        UiChoiceRequest? choice = RoundTrip(LinkMessage.Request(15, UiMethods.Choice,
            new UiChoiceRequest("d2", "确认", "继续？", new List<string>(), new List<string> { "确定", "取消" },
                SelectMode: false, TimeoutMs: 5000))).Read<UiChoiceRequest>();
        Check.Equal(2, choice?.Buttons.Count ?? -1, "选择框：按钮个数原样");
        Check.Equal(5000, choice?.TimeoutMs ?? -1, "选择框：超时原样");

        UiInputReply? inputReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(16, UiMethods.Input), new UiInputReply(true, "张三")))
            .Read<UiInputReply>();
        Check.True(inputReply is { Ok: true, Text: "张三" }, "要文本的结果往返");

        UiChoiceReply? choiceReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(17, UiMethods.Choice), new UiChoiceReply(true, 2, "第三项")))
            .Read<UiChoiceReply>();
        Check.True(choiceReply is { Accepted: true, Index: 2, Value: "第三项" }, "选择框的结果往返");

        UiTextWindowRequest? textWindow = RoundTrip(LinkMessage.Request(18, UiMethods.TextWindow,
            new UiTextWindowRequest("d3", "read", "log", "标题", "内容", Append: false, Wait: true,
                Monospace: true))).Read<UiTextWindowRequest>();
        Check.True(textWindow is { Mode: "read", Key: "log", Wait: true, Monospace: true },
            "文本窗口：方式 / 复用键 / 等关闭 / 等宽原样");

        UiTextWindowReply? textReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(19, UiMethods.TextWindow), new UiTextWindowReply(true, "改过的内容", true)))
            .Read<UiTextWindowReply>();
        Check.True(textReply is { Found: true, Text: "改过的内容", Changed: true }, "文本窗口的结果往返");

        UiSelectFileRequest? selectFile = RoundTrip(LinkMessage.Request(20, UiMethods.SelectFile,
            new UiSelectFileRequest("saveFile", "存到哪", "文本|*.txt", @"C:\"))).Read<UiSelectFileRequest>();
        Check.True(selectFile is { Kind: "saveFile", Filter: "文本|*.txt", InitialDir: @"C:\" },
            "选择文件：方式 / 类型 / 起始目录原样");

        UiSelectFileReply? selectReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(21, UiMethods.SelectFile), new UiSelectFileReply(false, "")))
            .Read<UiSelectFileReply>();
        Check.True(selectReply is { Ok: false, Path: "" }, "选择文件的结果往返（取消）");

        UiPickPointRequest? pickPoint = RoundTrip(LinkMessage.Request(22, UiMethods.PickPoint,
            new UiPickPointRequest("d4", "点一下那个按钮"))).Read<UiPickPointRequest>();
        Check.Equal("点一下那个按钮", pickPoint?.Hint, "取点：提示语原样");

        UiPickPointReply? pickReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(23, UiMethods.PickPoint), new UiPickPointReply(true, 1280, 720)))
            .Read<UiPickPointReply>();
        Check.True(pickReply is { Ok: true, X: 1280, Y: 720 }, "取点的坐标往返");

        // 面板操作名也互不相同（提权侧 switch 按字符串分发，重名会让一个操作永远到不了）。
        var ops = new[]
        {
            PanelOps.AddNew, PanelOps.AddExisting, PanelOps.Edit, PanelOps.Delete, PanelOps.Paste,
            PanelOps.Export, PanelOps.ToSubAction, PanelOps.DropPaths, PanelOps.Float,
            PanelOps.FloatGroup, PanelOps.ReplayMacro, PanelOps.RecordMacro, PanelOps.EditInStash,
            PanelOps.StashOpen, PanelOps.StashRun, PanelOps.StashPromote, PanelOps.StashDelete,
            PanelOps.StashClear,
        };
        Check.Equal(ops.Length, new HashSet<string>(ops, StringComparer.Ordinal).Count, "面板操作名互不相同");
        Check.True(ops.All(o => o.Length > 0), "面板操作名都不为空");

        // 新建 / 定位 / 显示面板的负载往返。
        ActionAddRequest? add = RoundTrip(LinkMessage.Request(1, LinkMethods.ActionAdd,
            new ActionAddRequest("季度报告", "app"))).Read<ActionAddRequest>();
        Check.Equal("季度报告", add?.Name, "新建动作：名称原样");
        Check.Equal("app", add?.Icon, "新建动作：图标原样");

        ActionIdRequest? id = RoundTrip(LinkMessage.Request(2, LinkMethods.ActionDelete,
            new ActionIdRequest("01abc"))).Read<ActionIdRequest>();
        Check.Equal("01abc", id?.Id, "定位动作：Id 原样");

        PanelShowRequest? show = RoundTrip(LinkMessage.Request(3, LinkMethods.EventPanelShow,
            new PanelShowRequest(0x1_0000_0000L, 1280, 720))).Read<PanelShowRequest>();
        Check.Equal(0x1_0000_0000L, show?.Hwnd, "显示面板：64 位句柄不丢精度");
        Check.Equal(1280, show?.X ?? -1, "显示面板：光标 X 原样");
        Check.Equal(720, show?.Y ?? -1, "显示面板：光标 Y 原样");

        RunActionRequest? run = RoundTrip(LinkMessage.Request(6, LinkMethods.RunAction,
            new RunActionRequest("common.capture", 10, 20, "Panel"))).Read<RunActionRequest>();
        Check.Equal("common.capture", run?.Reference, "转交执行：引用原样");
        Check.Equal("Panel", run?.Source, "转交执行：来源原样");

        OpenWindowRequest? open = RoundTrip(LinkMessage.Request(7, LinkMethods.OpenWindow,
            new OpenWindowRequest(LinkTargets.PanelScene, "s1"))).Read<OpenWindowRequest>();
        Check.Equal(LinkTargets.PanelScene, open?.Target, "打开窗口：目标原样");
        Check.Equal("s1", open?.Arg, "打开窗口：参数原样");

        OpenWindowRequest? ai = RoundTrip(LinkMessage.Request(12, LinkMethods.OpenWindow,
            new OpenWindowRequest(LinkTargets.Ai, null))).Read<OpenWindowRequest>();
        Check.Equal(LinkTargets.Ai, ai?.Target, "打开窗口：AI 接入目标原样");

        ActionOpRequest? op = RoundTrip(LinkMessage.Request(8, LinkMethods.ActionOp,
            new ActionOpRequest(PanelOps.Paste, GroupId: "g1", Kind: "copy", Reference: "01abc", Name: "报表")))
            .Read<ActionOpRequest>();
        Check.Equal(PanelOps.Paste, op?.Op, "面板操作：操作名原样");
        Check.Equal("g1", op?.GroupId, "面板操作：分组原样");
        Check.Equal("copy", op?.Kind, "面板操作：粘贴类别原样");
        Check.Equal("01abc", op?.Reference, "面板操作：引用原样");
        Check.Equal("报表", op?.Name, "面板操作：名字原样");

        ActionOpRequest? drop = RoundTrip(LinkMessage.Request(9, LinkMethods.ActionOp,
            new ActionOpRequest(PanelOps.DropPaths, GroupId: "g1",
                Paths: new[] { @"C:\a.txt", @"C:\b" }))).Read<ActionOpRequest>();
        Check.Equal(2, drop?.Paths?.Length ?? -1, "面板操作：路径列表原样");
        Check.Equal(@"C:\b", drop?.Paths is { Length: 2 } p ? p[1] : null, "面板操作：路径内容原样");

        ActionOpRequest? macro = RoundTrip(LinkMessage.Request(10, LinkMethods.ActionOp,
            new ActionOpRequest(PanelOps.RecordMacro, Id: "m1", Name: "报表宏"))).Read<ActionOpRequest>();
        Check.Equal(PanelOps.RecordMacro, macro?.Op, "面板操作：录制宏原样");
        Check.Equal("m1", macro?.Id, "面板操作：宏 Id 原样");
        Check.Equal("报表宏", macro?.Name, "面板操作：宏名原样");

        ActionOpRequest? stash = RoundTrip(LinkMessage.Request(11, LinkMethods.ActionOp,
            new ActionOpRequest(PanelOps.StashPromote, Id: "d1", Kind: "replace"))).Read<ActionOpRequest>();
        Check.Equal(PanelOps.StashPromote, stash?.Op, "面板操作：暂存区采纳原样");
        Check.Equal("d1", stash?.Id, "面板操作：草稿 Id 原样");
        Check.Equal("replace", stash?.Kind, "面板操作：采纳方式原样");

        // 快照：整份面板配置 + 动作摘要都要原样回来。
        var panel = new PanelConfig
        {
            Blocked = new List<string> { "game.exe" },
            Global = new PanelAreaDto
            {
                Scenes = new List<PanelSceneDto>
                {
                    new()
                    {
                        Id = "s1", Name = "全局", Groups = new List<PanelGroupDto>
                        {
                            new() { Id = "g1", Name = "常用", Actions = new List<string> { "a1", "a2" } },
                        },
                    },
                },
            },
        };

        PanelSnapshot? snapshot = RoundTrip(LinkMessage.Request(4, LinkMethods.PanelSnapshot,
            new PanelSnapshot(panel, new List<ActionSummary> { new("a1", "报表", "app", 3) },
                new List<string> { "a1" }))).Read<PanelSnapshot>();

        Check.Equal(1, snapshot?.Panel.Global.Scenes.Count ?? -1, "快照：场景数");
        Check.Equal("常用", snapshot?.Panel.Global.Scenes[0].Groups[0].Name, "快照：分组名");
        Check.Equal(2, snapshot?.Panel.Global.Scenes[0].Groups[0].Actions.Count ?? -1, "快照：磁贴 Id 列表");
        Check.Equal("game.exe", snapshot?.Panel.Blocked[0], "快照：禁止弹出名单");
        Check.Equal(3, snapshot?.Actions[0].Steps ?? -1, "快照：动作步数");
        Check.Equal("报表", snapshot?.Actions[0].Name, "快照：动作名");
        Check.Equal("a1", snapshot?.Floating.FirstOrDefault(), "快照：正在悬浮的引用");

        // 未知命令：失败答复（不抛异常、不碰数据）。
        LinkMessage unknown = PanelHost.Handle(LinkMessage.Request(5, "panel.nope"));
        Check.False(unknown.Ok, "未知命令回失败答复");
        Check.True((unknown.Error ?? "").Contains("未知命令"), unknown.Error ?? "");
    }

    // ============ 小工具 ============
    private static LinkMessage RoundTrip(LinkMessage message)
    {
        byte[] frame = LinkFraming.Frame(message);
        Check.True(LinkFraming.TryRead(frame, out LinkMessage? back, out int consumed, out string? error), "帧能读回");
        Check.True(error == null, "读回不报错");
        Check.Equal(frame.Length, consumed, "读回消费整帧");
        return back!;
    }

    private static byte[] Prefix(int length) => BitConverter.GetBytes(length);

    private static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[LinkFraming.HeaderSize + body.Length];
        BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
        body.CopyTo(frame, LinkFraming.HeaderSize);
        return frame;
    }
}
