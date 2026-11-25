using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Core.Link;
using LTools.Features;
using LTools.Hooks;
using LTools.Native;

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
        cases.Add(new("link", "宿主运行期状态：两个 ApplySettings 重载各管各的域", RuntimeStateOverloads));
    }

    /// <summary>
    /// 宿主写完配置就地刷「自己那份」（<c>DataHost.RefreshRuntime</c>）：<c>settings.json</c> 只管
    /// settings 域，超级面板那两个开关在 <c>panel.json</c> 里、由 <see cref="AppState.ApplySuperPanel"/> 单独同步。
    /// </summary>
    /// <remarks>
    /// 拆成两个重载是因为设置页改一个开关就写一次盘，整份 <c>AppConfig.Load</c> 会把 <c>actions/</c>
    /// 也解析一遍。拆的时候最容易漏的就是「超级面板开关不在 settings 域」——漏了界面上那个勾会自己跳回去。
    /// </remarks>
    private static void RuntimeStateOverloads()
    {
        bool capsLock = AppState.CapsLockModifier;
        int delay = AppState.ScreenshotDelaySeconds;
        bool panel = AppState.IsSuperPanelEnabled;
        int threshold = AppState.SuperPanelThresholdMs;
        try
        {
            AppState.ApplySuperPanel(new SuperPanelConfig { Enabled = false, LongPressThresholdMs = 420 });
            Check.False(AppState.IsSuperPanelEnabled, "超级面板开关同步到 AppState");
            Check.Equal(420, AppState.SuperPanelThresholdMs, "长按阈值同步到 AppState");

            AppState.ApplySettings(new SettingsFile
            {
                CapsLockModifier = false,
                Screenshot = new ScreenshotFile { DelaySeconds = 7 },
            });
            Check.False(AppState.CapsLockModifier, "settings 域生效");
            Check.Equal(7, AppState.ScreenshotDelaySeconds, "延时秒数生效");
            Check.False(AppState.IsSuperPanelEnabled, "settings 域不动超级面板开关");
            Check.Equal(420, AppState.SuperPanelThresholdMs, "settings 域不动长按阈值");

            AppState.ApplySettings(new AppConfig
            {
                SuperPanel = new SuperPanelConfig { Enabled = true, LongPressThresholdMs = 300 },
            });
            Check.True(AppState.IsSuperPanelEnabled, "整份配置连超级面板一起灌");
            Check.Equal(300, AppState.SuperPanelThresholdMs, "整份配置的长按阈值");
        }
        finally
        {
            AppState.CapsLockModifier = capsLock;
            AppState.ScreenshotDelaySeconds = delay;
            AppState.IsSuperPanelEnabled = panel;
            AppState.SuperPanelThresholdMs = threshold;
        }
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
            LinkMethods.PanelSnapshot, LinkMethods.ActionAdd,
            LinkMethods.ActionUpdate, LinkMethods.ActionDelete, LinkMethods.ActionDuplicate,
            LinkMethods.RunAction, LinkMethods.ActionOp,
            LinkMethods.EventPanelShow, LinkMethods.EventPanelToggle, LinkMethods.EventClientReady,
            LinkMethods.EventActionsChanged, LinkMethods.EventPanelStash, LinkMethods.EventPanelState,
            LinkMethods.EventPanelDismiss, LinkMethods.EventInfoHelper, LinkMethods.EventPickerActive,
            LinkMethods.EventInputSwallow, LinkMethods.CaptureRun, LinkMethods.CaptureDirect,
            LinkMethods.EventCaptureRecordDone, LinkMethods.EventCaptureWindow, ActionMethods.SaveAll,
            LinkMethods.EventInputSuspend, LinkMethods.EventPinState, LinkMethods.EventRecordKeys,
            LinkMethods.EventHotkeyCapture, SettingsMethods.Show, AppMethods.AutoStart,
            SubActionMethods.Save, SubActionMethods.Delete, LinkMethods.EventConfigReload,
            RunMethods.Program, RunMethods.Stop, RunMethods.EventTrace, RunMethods.EventSlow,
            RunMethods.EventDone, MacroMethods.Record, MacroMethods.Stop,
            MacroMethods.Play, MacroMethods.Rename, MacroMethods.Delete,
            MacroMethods.EventStarted, MacroMethods.EventStopped, MacroMethods.EventSaved,
            MacroMethods.EventPlayed, LinkMethods.EventConfigChanged,
            McpMethods.Status, McpMethods.Apply, McpMethods.ResetToken, McpMethods.Knowledge,
        };
        Check.Equal(names.Length, new HashSet<string>(names, StringComparer.Ordinal).Count, "方法名互不相同");
        Check.True(names.All(n => n.Length > 0), "方法名都不为空");

        // 面板状态：宿主把整份状态推给提权进程的钩子（开 / 钉住 / 菜单 / 前台 / 窗口句柄）。
        PanelStateEvent? state = RoundTrip(LinkMessage.Request(13, LinkMethods.EventPanelState,
            new PanelStateEvent(true, Pinned: true, Interacting: false, Active: true, Hwnd: 4242,
                Menus: new List<PanelMenuRect> { new(1, 2, 3, 4) }))).Read<PanelStateEvent>();
        Check.True(state?.Open == true, "面板状态往返：开着");
        Check.True(state?.Pinned == true && state.Active && state.Hwnd == 4242, "面板状态往返：钉住 / 前台 / 句柄");
        Check.True(state?.Menus is [{ Left: 1, Top: 2, Right: 3, Bottom: 4 }], "面板状态往返：菜单矩形");

        // 落到镜像上：钩子线程就按这份判点击外部、Esc、长按手势。
        PanelHostBridge.ApplyPanelState(state!);
        HookMirror.PanelSnapshot mirror = HookMirror.Panel;
        Check.True(mirror.Open && mirror.Pinned && mirror.Active, "镜像：开 / 钉住 / 前台");
        Check.True(mirror.Hwnd == new IntPtr(4242) && mirror.MenuRects is [{ Left: 1, Top: 2, Right: 3, Bottom: 4 }],
            "镜像：句柄与菜单矩形");

        // 钉住 = 工具盘（2026-11-26）：点外面不算「外面的那一下」，Esc 也不吞 / 不关。
        // 钩子那边只认这份镜像；真正关闭只有 ✕ / 菜单「关闭面板」/ CapsLock+T。
        Check.False(MouseHook.IsOutsidePanel(mirror, 0, 0), "钉住：点外面不关闭");
        Check.False(Features.SuperPanel.HandleKey(Win32.VkEscape, true), "钉住：Esc 不吞、不关");
        Check.False(Features.SuperPanel.ShouldDismissAfterUse(true), "钉住：用完即收不收");

        // 取消钉住：同一点又回到「外面」——钩子可以照常判「点外部关闭」。
        PanelHostBridge.ApplyPanelState(state! with { Pinned = false });
        Check.True(MouseHook.IsOutsidePanel(HookMirror.Panel, 0, 0), "未钉住：点外面算关闭");
        Check.True(Features.SuperPanel.ShouldDismissAfterUse(false), "未钉住：用完即收要收");

        PanelHostBridge.DropPanelState();
        Check.False(HookMirror.Panel.Open, "断开：面板按关处理");

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

        // —— 配置写入转交（执行器 → 宿主，M2-a）——
        Check.Equal("data.write", DataMethods.Write, "配置写入方法名");

        DataWriteRequest? write = RoundTrip(LinkMessage.Request(24, DataMethods.Write,
            new DataWriteRequest("settings.json", "{\"a\":1}"))).Read<DataWriteRequest>();
        Check.True(write is { Name: "settings.json", Json: "{\"a\":1}" }, "配置写入请求往返");

        DataWriteReply? wrote = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(25, DataMethods.Write), new DataWriteReply(true)))
            .Read<DataWriteReply>();
        Check.True(wrote?.Written == true, "配置写入结果往返");

        // —— 速记：窗口与 速记/ 的写者都在宿主（M3-1）——
        Check.Equal("note.toggle", NoteMethods.Toggle, "速记开关方法名");
        Check.Equal("note.write", NoteMethods.Write, "写速记方法名");

        NoteWriteRequest? noteWrite = RoundTrip(LinkMessage.Request(26, NoteMethods.Write,
            new NoteWriteRequest
            {
                Body = "正文",
                Title = "标题",
                Category = "分类",
                Append = true,
                Target = "x.md",
                Stamp = false,
                Images = new List<string> { @"C:\a.png" },
            })).Read<NoteWriteRequest>();
        Check.True(noteWrite is { Body: "正文", Title: "标题", Category: "分类", Append: true, Stamp: false },
            "写速记的请求往返（正文 / 标题 / 分类 / 追加 / 时间标题）");
        Check.Equal(1, noteWrite?.Images?.Count ?? -1, "写速记的请求往返（图片列表）");
        Check.Equal(@"C:\a.png", noteWrite?.Images?[0], "写速记的请求往返（图片路径原样）");

        NoteWriteReply? noteReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(27, NoteMethods.Write),
                new NoteWriteReply(true, "标题", "2026-11-24 12:00", @"C:\速记\a.md", null)))
            .Read<NoteWriteReply>();
        Check.True(noteReply is { Ok: true, Title: "标题", Time: "2026-11-24 12:00" },
            "写速记的结果往返");
        Check.True(noteReply?.File?.EndsWith("a.md", StringComparison.Ordinal) == true, "写速记的结果带上文件路径");

        NoteWriteReply? noteFail = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(28, NoteMethods.Write),
                new NoteWriteReply(false, null, null, null, "源图不存在")))
            .Read<NoteWriteReply>();
        Check.True(noteFail is { Ok: false } && noteFail.Error == "源图不存在", "写速记的失败原因往返");

        // —— 动作库：写者是宿主（M2-b）——
        Check.Equal("action.saveAll", ActionMethods.SaveAll, "动作库写入方法名");

        ActionSaveRequest? actionSave = RoundTrip(LinkMessage.Request(30, ActionMethods.SaveAll,
            new ActionSaveRequest(new List<ActionDto>
            {
                new() { Id = "a1", Name = "第一个", Icon = "app" },
                new() { Id = "a2", Name = "第二个", Icon = "folder" },
            }))).Read<ActionSaveRequest>();
        Check.Equal(2, actionSave?.Actions?.Count ?? -1, "动作库请求往返（条数）");
        Check.Equal("a2", actionSave?.Actions?[1].Id, "动作库请求往返（Id 原样）");
        Check.Equal("第二个", actionSave?.Actions?[1].Name, "动作库请求往返（名称原样）");

        ActionSaveReply? actionReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(31, ActionMethods.SaveAll), new ActionSaveReply(false, "磁盘满了")))
            .Read<ActionSaveReply>();
        Check.True(actionReply is { Ok: false } && actionReply.Error == "磁盘满了", "动作库写入的失败原因往返");

        // —— 动作级写入（宿主里的面板 / 编辑器 → 执行器：内容属主只有一个）——
        Check.True(ActionEditHost.IsWrite(LinkMethods.ActionAdd) && ActionEditHost.IsWrite(LinkMethods.ActionUpdate)
            && ActionEditHost.IsWrite(LinkMethods.ActionDelete) && ActionEditHost.IsWrite(LinkMethods.ActionDuplicate),
            "四条动作写入都由 ActionEditHost 接");
        Check.False(ActionEditHost.IsWrite(LinkMethods.PanelSnapshot), "面板读取不走动作写入");

        var editDto = new ActionDto { Id = "a9", Name = "容器里改的动作", Icon = "app" };
        editDto.Program = new ActionProgram { Name = "容器里改的动作" };
        editDto.Program.Steps.Add(new Step { Unit = "flow.set" });
        ActionEditRequest? editReq = RoundTrip(LinkMessage.Request(47, LinkMethods.ActionUpdate,
                new ActionEditRequest(editDto)))
            .Read<ActionEditRequest>();
        Check.Equal("a9", editReq?.Dto?.Id, "动作写入往返（Id）");
        Check.Equal("容器里改的动作", editReq?.Dto?.Name, "动作写入往返（名称）");
        Check.Equal("flow.set", editReq?.Dto?.Program?.Steps[0].Unit, "动作写入往返（步骤）");

        ActionEditReply? editReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(48, LinkMethods.ActionUpdate),
                new ActionEditReply(true, editDto, null)))
            .Read<ActionEditReply>();
        Check.True(editReply is { Ok: true, Action.Id: "a9" }, "动作写入答复往返");
        ActionEditReply? editFail = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(49, LinkMethods.ActionUpdate),
                new ActionEditReply(false, null, "动作已经不存在了")))
            .Read<ActionEditReply>();
        Check.True(editFail is { Ok: false } && editFail.Error == "动作已经不存在了", "动作写入失败原因往返");

        // —— 全局子动作库：写者是宿主（M3-4a，一个文件一个定义，所以按份转）——
        Check.Equal("subaction.save", SubActionMethods.Save, "子动作写入方法名");
        Check.Equal("subaction.delete", SubActionMethods.Delete, "子动作删除方法名");

        var subDef = new SubActionDef { Id = "s1", Name = "填表", Description = "把一行填进去" };
        subDef.Variables.Add(new VariableDef { Key = "行", Type = ValueKind.text, Direction = VariableDirection.input });
        SubActionSaveRequest? subSave = RoundTrip(LinkMessage.Request(41, SubActionMethods.Save,
            new SubActionSaveRequest(subDef, CreateNew: true))).Read<SubActionSaveRequest>();
        Check.Equal("s1", subSave?.Def.Id, "子动作写入往返（id）");
        Check.Equal("填表", subSave?.Def.Name, "子动作写入往返（名称）");
        Check.True(subSave?.CreateNew == true, "子动作写入往返（新建标记）");
        Check.Equal(1, subSave?.Def.Variables.Count ?? -1, "子动作写入往返（变量不丢）");

        SubActionReply? subReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(42, SubActionMethods.Save), new SubActionReply(false, "id 已存在")))
            .Read<SubActionReply>();
        Check.True(subReply is { Ok: false } && subReply.Error == "id 已存在", "子动作写入的失败原因往返");

        SubActionDeleteRequest? subDelete = RoundTrip(LinkMessage.Request(43, SubActionMethods.Delete,
            new SubActionDeleteRequest("s1"))).Read<SubActionDeleteRequest>();
        Check.Equal("s1", subDelete?.Id, "子动作删除往返");

        // 配置整份重读（执行器 → 宿主）：没有负载，方法名就是全部协议。
        Check.Equal("config.reload", LinkMethods.EventConfigReload, "配置重读方法名");

        // —— 跨进程试运行（宿主里的编辑器 → 执行器跑，轨迹 / 结果按事件回来）——
        Check.Equal("run.program", RunMethods.Program, "试运行方法名");
        Check.Equal("run.stop", RunMethods.Stop, "停止试运行方法名");
        Check.Equal("run.trace", RunMethods.EventTrace, "试运行轨迹方法名");
        Check.Equal("run.slow", RunMethods.EventSlow, "慢步骤方法名");
        Check.Equal("run.done", RunMethods.EventDone, "试运行结果方法名");

        var program = new ActionProgram { Name = "试运行冒烟" };
        program.Variables.Add(new VariableDef { Key = "文本", Type = ValueKind.text, Default = Value.FromText("你好") });
        program.Variables.Add(new VariableDef { Key = "数字", Type = ValueKind.number, Default = Value.FromNumber(42) });
        program.Variables.Add(new VariableDef
        {
            Key = "列表", Type = ValueKind.list,
            Default = Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") }),
        });
        program.Steps.Add(new Step { Unit = "flow.set" });

        RunProgramRequest? programReq = RoundTrip(LinkMessage.Request(44, RunMethods.Program,
                new RunProgramRequest("t1", "试运行冒烟", program, null, null, false, "Editor")))
            .Read<RunProgramRequest>();
        Check.Equal("t1", programReq?.Token, "试运行请求往返（token）");
        Check.Equal(3, programReq?.Program.Variables.Count ?? -1, "试运行请求往返（变量不丢）");
        Check.Equal("你好", programReq?.Program.Variables[0].Default?.AsText(), "试运行请求往返（文本默认值）");
        Check.Equal(42, programReq?.Program.Variables[1].Default?.AsNumber() ?? -1, "试运行请求往返（数字默认值）");
        Check.Equal(2, programReq?.Program.Variables[2].Default?.Items.Count ?? -1, "试运行请求往返（列表默认值）");
        Check.Equal("flow.set", programReq?.Program.Steps[0].Unit, "试运行请求往返（步骤）");

        var batch = new TraceBatchEvent("t1", new List<TraceEntryDto>
        {
            new("/steps/0", "第 1 行", 0, "flow.set", "设置变量", "ok", 12, null,
                new Dictionary<string, Value> { ["a"] = Value.FromText("甲") },
                new Dictionary<string, Value> { ["b"] = Value.FromNumber(7) }, 1, 1),
        });
        TraceBatchEvent? batchBack = RoundTrip(LinkMessage.Request(45, RunMethods.EventTrace, batch))
            .Read<TraceBatchEvent>();
        Check.Equal("t1", batchBack?.Token, "轨迹批次往返（token）");
        Check.Equal("甲", batchBack?.Entries[0].ToTrace().Outputs["a"].AsText(), "轨迹往返（输出值）");
        Check.Equal(7, batchBack?.Entries[0].ToTrace().Inputs["b"].AsNumber() ?? -1, "轨迹往返（参数值）");
        Check.True(batchBack?.Entries[0].ToTrace().Status == StepTraceStatus.ok, "轨迹往返（状态）");

        RunDoneEvent? doneBack = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(46, RunMethods.EventDone),
                new RunDoneEvent("t1", new RunResultDto(false, false, "第 1 行失败", "第 1 行", "notFound", 3, 99,
                    new Dictionary<string, Value> { ["x"] = Value.FromText("y") }))))
            .Read<RunDoneEvent>();
        Check.True(doneBack?.Result is { Steps: 3 } && doneBack.Result.FailedStepLabel == "第 1 行",
            "试运行结果往返（失败位置 / 步数）");
        Check.Equal("y", doneBack?.Result.Variables["x"].AsText(), "试运行结果往返（变量）");

        // —— 点选器占用输入（宿主 → 提权进程，让开「长按右键 = 面板」）——
        Check.Equal("input.picker", LinkMethods.EventPickerActive, "点选器状态方法名");
        PickerActiveEvent? pickerEvent = RoundTrip(LinkMessage.Request(29, LinkMethods.EventPickerActive,
            new PickerActiveEvent(true))).Read<PickerActiveEvent>();
        Check.True(pickerEvent?.Active == true, "点选器状态往返");

        // —— 关窗要吞掉的抬起（宿主采样 → 提权进程上膛）——
        Check.Equal("input.swallow", LinkMethods.EventInputSwallow, "吞掉退出按键的方法名");
        SwallowInputEvent? swallow = RoundTrip(LinkMessage.Request(32, LinkMethods.EventInputSwallow,
            new SwallowInputEvent(Right: true, Left: false, Middle: false, Key: 27))).Read<SwallowInputEvent>();
        Check.True(swallow is { Right: true, Left: false, Middle: false, Key: 27 }, "退出按键采样往返");

        // —— 截图（覆盖层在宿主里跑）——
        Check.Equal("capture.run", LinkMethods.CaptureRun, "交互截图方法名");
        Check.Equal("capture.direct", LinkMethods.CaptureDirect, "直接截图方法名");
        Check.Equal("capture.recordDone", LinkMethods.EventCaptureRecordDone, "录屏结束事件名");
        Check.Equal("capture.window", LinkMethods.EventCaptureWindow, "截图窗口事件名");
        CaptureWindowEvent? windowEvent = RoundTrip(LinkMessage.Request(37, LinkMethods.EventCaptureWindow,
            new CaptureWindowEvent("pinManager"))).Read<CaptureWindowEvent>();
        Check.Equal("pinManager", windowEvent?.Target, "截图窗口事件往返");

        // —— 画布 / 贴图：钩子要读的宿主状态（挂起快捷键、哪张贴图接管键盘）——
        Check.Equal("input.suspend", LinkMethods.EventInputSuspend, "快捷键挂起方法名");
        Check.Equal("input.pin", LinkMethods.EventPinState, "贴图接管方法名");
        InputSuspendEvent? suspend = RoundTrip(LinkMessage.Request(38, LinkMethods.EventInputSuspend,
            new InputSuspendEvent(true, "ltools.globalCanvas"))).Read<InputSuspendEvent>();
        Check.True(suspend is { Suspended: true, OnlyRef: "ltools.globalCanvas" }, "快捷键挂起状态往返");

        PinStateEvent? pinState = RoundTrip(LinkMessage.Request(39, LinkMethods.EventPinState,
            new PinStateEvent(0x1234, PinStates.Engage))).Read<PinStateEvent>();
        Check.True(pinState is { Hwnd: 0x1234, State: "engage" }, "贴图接管状态往返");

        // 录屏条快照（录屏条在宿主、钩子在执行器）：状态字段一个都不能丢。
        Check.Equal("record.keys", LinkMethods.EventRecordKeys, "录屏快捷键状态方法名");        var keys = new RecordKeysEvent(true, Recording: true, Paused: false, Busy: false,
            HasLastRecord: true, ExportRowVisible: false, AreaHistory: true,
            CanUndo: true, CanRedo: false, DrawingAllowed: true);
        RecordKeysEvent? recordKeys = RoundTrip(LinkMessage.Request(40, LinkMethods.EventRecordKeys, keys))
            .Read<RecordKeysEvent>();
        Check.True(recordKeys == keys, "录屏快捷键状态往返（含 Hot / 指针在条上）");

        Features.CapturePro.RecordShortcutRoute.ApplyState(keys);
        Check.True(Features.CapturePro.RecordShortcutRoute.IsPublished, "执行器：录屏条快照登记为已发布");
        Check.True(Features.CapturePro.RecordShortcutRoute.Handle(0x56, isDown: true, isUp: false),
            "执行器：指针在录屏条上时 V = 选择工具（吞掉并转给宿主）");
        Check.True(Features.CapturePro.RecordShortcutRoute.Handle(0x56, isDown: false, isUp: true),
            "执行器：吞掉的那个键抬起也吞");
        Features.CapturePro.RecordShortcutRoute.ApplyState(keys with { Hot = false });
        Check.False(Features.CapturePro.RecordShortcutRoute.Handle(0x56, isDown: true, isUp: false),
            "指针不在录屏条上：一个键都不碰");
        Features.CapturePro.RecordShortcutRoute.DropRemote();
        Check.False(Features.CapturePro.RecordShortcutRoute.IsPublished, "宿主断开：录屏条快照作废");

        // 提权进程本地（宿主没连上）：挂起状态直接落到钩子看的那个开关上。
        InputGuard.SetSuspended(true, "ltools.globalCanvas");
        Check.True(InputGuard.Suspended && HotkeyRegistry.Suspended, "挂起快捷键：钩子开关跟着走");
        Check.Equal("ltools.globalCanvas", InputGuard.OnlyRef, "挂起时只放行那一条");
        InputGuard.SetSuspended(false, null);
        Check.False(InputGuard.Suspended || HotkeyRegistry.Suspended, "取消挂起：钩子开关恢复");

        // 宿主推来的挂起状态：宿主断开就作废（否则它一崩，全局快捷键永远被吞）。
        InputGuard.Apply(new InputSuspendEvent(true, "ltools.globalCanvas"));
        Check.True(InputGuard.Suspended && HotkeyRegistry.Suspended, "宿主推来的挂起：钩子开关跟着走");
        InputGuard.Reset();
        Check.False(InputGuard.Suspended || InputGuard.OnlyRef != null || HotkeyRegistry.Suspended,
            "宿主断开：挂起状态作废");

        // 本地降级开的那份画布不受影响：Reset 只清「宿主推来的」。
        InputGuard.SetSuspended(true, "ltools.globalCanvas");
        InputGuard.Reset();
        Check.True(InputGuard.Suspended && HotkeyRegistry.Suspended, "本地挂起不被宿主断开清掉");
        InputGuard.SetSuspended(false, null);

        // 宿主的贴图影子：断开时只摘影子，本地贴图（降级时开在本进程）留着。
        IntPtr localPin = new(0x2222);
        Features.CapturePro.PinShortcutRoute.Register(localPin, _ => { });
        Features.CapturePro.PinShortcutRoute.ApplyState(0x3333, PinStates.Engage);
        Check.True(Features.CapturePro.PinShortcutRoute.IsEngaged(new IntPtr(0x3333)), "宿主贴图影子：已接管键盘");
        Check.Equal(2, Features.CapturePro.PinShortcutRoute.Count, "影子与本地图都登记着");
        Features.CapturePro.PinShortcutRoute.DropRemote();
        Check.Equal(1, Features.CapturePro.PinShortcutRoute.Count, "宿主断开：只摘影子");
        Check.True(Features.CapturePro.PinShortcutRoute.IsEngaged(localPin) == false, "本地贴图仍在（只是没点过）");
        Features.CapturePro.PinShortcutRoute.Clear();
        Check.Equal(0, Features.CapturePro.PinShortcutRoute.Count, "清空路由");

        CaptureRunRequest? runReq = RoundTrip(LinkMessage.Request(33, LinkMethods.CaptureRun,
            new CaptureRunRequest("Copy", WantImage: true))).Read<CaptureRunRequest>();
        Check.True(runReq is { Pending: "Copy", WantImage: true }, "交互截图请求往返");

        CaptureRunReply? runReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(34, LinkMethods.CaptureRun),
                new CaptureRunReply("captured", 10, 20, 300, 200, @"cache\screenshots\a.png")))
            .Read<CaptureRunReply>();
        Check.True(runReply is { Exit: "captured", X: 10, Y: 20, W: 300, H: 200 }, "交互截图结果往返（区域）");
        Check.Equal(@"cache\screenshots\a.png", runReply?.File, "交互截图结果往返（png 路径）");

        CaptureDirectRequest? directReq = RoundTrip(LinkMessage.Request(35, LinkMethods.CaptureDirect,
            new CaptureDirectRequest(1, 2, 800, 600, "前台窗口", "focusedWindow"))).Read<CaptureDirectRequest>();
        Check.True(directReq is { X: 1, Y: 2, W: 800, H: 600, Source: "前台窗口", Scenario: "focusedWindow" },
            "直接截图请求往返");
        Check.True(RoundTrip(LinkMessage.Response(LinkMessage.Request(36, LinkMethods.CaptureDirect),
                new CaptureDirectReply(false, "抓帧失败"))).Read<CaptureDirectReply>() is { Ok: false, Error: "抓帧失败" },
            "直接截图的失败原因往返");

        // 白名单：只认七个域文件名，且不许带路径（这条链路只能改配置，不能写任意文件）。
        Check.True(ConfigBundle.DomainFiles.All(ConfigBundle.IsDomainFile), "七个域文件名都在白名单里");
        Check.True(ConfigBundle.IsDomainFile("SETTINGS.JSON"), "白名单不分大小写");
        Check.True(!ConfigBundle.IsDomainFile("../settings.json"), "白名单拒绝相对路径");
        Check.True(!ConfigBundle.IsDomainFile(@"configs\settings.json"), "白名单拒绝带目录的名字");
        Check.True(!ConfigBundle.IsDomainFile(@"C:\settings.json"), "白名单拒绝绝对路径");
        Check.True(!ConfigBundle.IsDomainFile("actions"), "白名单拒绝非域文件");
        Check.True(!ConfigBundle.IsDomainFile(""), "白名单拒绝空名字");
        Check.True(!ConfigBundle.IsDomainFile(null), "白名单拒绝 null");

        // 锚点是一个文件路径（配置写在它同级的 configs/ 下），见 ConfigLocator。
        string root = Path.Combine(Path.GetTempPath(), "ltools-selftest-data-" + Guid.NewGuid().ToString("N")[..8]);
        string anchor = Path.Combine(root, "LTools.json");
        try
        {
            Check.True(!ConfigBundle.TryWriteDomainFile(anchor, "settings.json/../../evil.json", "{}"),
                "越界名字写不进任何文件");
            Check.True(ConfigBundle.TryWriteDomainFile(anchor, "ui.json", "{\"k\":2}"), "按域名写进 configs/<名字>");
            Check.Equal("{\"k\":2}", File.ReadAllText(Path.Combine(root, "configs", "ui.json")),
                "写进去的内容原样");
            Check.True(!File.Exists(Path.Combine(root, "configs", "settings.json")), "没点名的域不碰");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }

        // 面板操作名也互不相同（提权侧 switch 按字符串分发，重名会让一个操作永远到不了）。
        // 只剩「提权进程才做得了的」：建 / 改 / 删 / 复制动作已改成 action.* 那一组。
        var ops = new[]
        {
            PanelOps.Float, PanelOps.FloatGroup, PanelOps.ReplayMacro, PanelOps.RecordMacro,
            PanelOps.EditInStash, PanelOps.StashOpen, PanelOps.StashRun, PanelOps.StashPromote,
            PanelOps.StashDelete, PanelOps.StashClear,
        };
        Check.Equal(ops.Length, new HashSet<string>(ops, StringComparer.Ordinal).Count, "面板操作名互不相同");
        Check.True(ops.All(o => o.Length > 0), "面板操作名都不为空");

        // 新建 / 定位 / 显示面板的负载往返（四条动作写入共用一个请求形状）。
        ActionEditRequest? add = RoundTrip(LinkMessage.Request(1, LinkMethods.ActionAdd,
            new ActionEditRequest(new ActionDto { Name = "季度报告", Icon = "app" }))).Read<ActionEditRequest>();
        Check.Equal("季度报告", add?.Dto?.Name, "新建动作：名称原样");
        Check.Equal("app", add?.Dto?.Icon, "新建动作：图标原样");

        ActionEditRequest? id = RoundTrip(LinkMessage.Request(2, LinkMethods.ActionDelete,
            new ActionEditRequest(Id: "01abc"))).Read<ActionEditRequest>();
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

        SettingsOpenRequest? settings = RoundTrip(LinkMessage.Request(7, SettingsMethods.Show,
            new SettingsOpenRequest(SettingsActions.Open, "macros-list"))).Read<SettingsOpenRequest>();
        Check.Equal(SettingsActions.Open, settings?.Action, "打开设置：动作原样");
        Check.Equal("macros-list", settings?.Target, "打开设置：导航 tag 原样");

        SettingsOpenRequest? settingsToggle = RoundTrip(LinkMessage.Request(12, SettingsMethods.Show,
            new SettingsOpenRequest(SettingsActions.Toggle))).Read<SettingsOpenRequest>();
        Check.Equal(SettingsActions.Toggle, settingsToggle?.Action, "打开设置：开 / 关原样");
        Check.True(settingsToggle?.Target == null, "打开设置：不跳页时没有 tag");

        // 快捷键录入：宿主推「在录入」→ 执行器的钩子整体让路；断开链路要能清掉（否则快捷键全哑）。
        Features.HotkeyCapture.ApplyRemote(true);
        Check.True(Features.HotkeyCapture.IsCapturing, "录入状态：宿主推来后钩子让路");
        Features.HotkeyCapture.ResetRemote();
        Check.False(Features.HotkeyCapture.IsCapturing, "录入状态：断开后恢复");

        // 自启：只认三个动作，认不出的回失败（别假装做过）。
        AppAutoStartReply? badAuto = AppHost.Handle(LinkMessage.Request(13, AppMethods.AutoStart,
            new AppAutoStartRequest("erase"))).Read<AppAutoStartReply>();
        Check.False(badAuto?.Ok == true, "自启：未知动作回失败");
        Check.True(!string.IsNullOrEmpty(badAuto?.Error), "自启：失败带原因");

        AppAutoStartReply? auto = RoundTrip(LinkMessage.Request(14, AppMethods.AutoStart,
            new AppAutoStartReply(true,
                new AutoStartInfo(AutoStartState.Stale, @"D:\Tools\LTools.exe", true), null)))
            .Read<AppAutoStartReply>();
        Check.Equal(AutoStartState.Stale, auto?.Info.State, "自启：状态原样");
        Check.Equal(@"D:\Tools\LTools.exe", auto?.Info.RegisteredPath, "自启：注册路径原样");
        Check.True(auto?.Info.RegisteredPathExists == true, "自启：路径存在标记原样");

        ActionOpRequest? op = RoundTrip(LinkMessage.Request(8, LinkMethods.ActionOp,
            new ActionOpRequest(PanelOps.FloatGroup, GroupId: "g1"))).Read<ActionOpRequest>();
        Check.Equal(PanelOps.FloatGroup, op?.Op, "面板操作：操作名原样");
        Check.Equal("g1", op?.GroupId, "面板操作：分组原样");

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

        // 宏录制（编辑器在宿主里）：请求带 token，结束 / 存好的事件按同一个 token 回来。
        // 方法名放进上面的互不相同名单，这里只验负载。
        // 「宿主写过配置」（M3-5c-3a）：只带一句原因，执行器据此整份重读运行期状态。
        Check.Equal("config.changed", LinkMethods.EventConfigChanged, "配置变更通知方法名");
        ConfigChangedEvent? cfgChanged = RoundTrip(LinkMessage.Request(59, LinkMethods.EventConfigChanged,
            new ConfigChangedEvent("settings.json"))).Read<ConfigChangedEvent>();
        Check.Equal("settings.json", cfgChanged?.Reason, "配置变更通知：原因原样");

        MacroRecordRequest? macroReq = RoundTrip(LinkMessage.Request(60, MacroMethods.Record,
            new MacroRecordRequest("m1", "报表宏"))).Read<MacroRecordRequest>();
        Check.Equal("m1", macroReq?.Id, "宏录制请求：宏 Id 原样");
        Check.Equal("报表宏", macroReq?.Name, "宏录制请求：宏名原样");

        MacroRecordReply? macroReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(61, MacroMethods.Record), new MacroRecordReply(false, "宏功能已停用")))
            .Read<MacroRecordReply>();
        Check.True(macroReply is { Started: false } && macroReply.Error == "宏功能已停用", "宏录制答复往返");

        // 录制 / 试放的生命周期是广播：不带 token，谁开着宏相关窗口都该跟着让开 / 刷新。
        MacroSavedEvent? macroSaved = RoundTrip(LinkMessage.Request(62, MacroMethods.EventSaved,
            new MacroSavedEvent("m1"))).Read<MacroSavedEvent>();
        Check.True(macroSaved is { MacroId: "m1" }, "宏已保存通知：宏 Id 原样");

        MacroPlayRequest? macroPlay = RoundTrip(LinkMessage.Request(63, MacroMethods.Play,
            new MacroPlayRequest("m1"))).Read<MacroPlayRequest>();
        Check.Equal("m1", macroPlay?.Id, "试放请求：宏 Id 原样");

        MacroPlayReply? playReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(64, MacroMethods.Play), new MacroPlayReply(false, "已有宏正在回放")))
            .Read<MacroPlayReply>();
        Check.True(playReply is { Started: false } && playReply.Error == "已有宏正在回放", "试放答复往返");

        MacroPlayEvent? played = RoundTrip(LinkMessage.Request(65, MacroMethods.EventPlayed,
            new MacroPlayEvent("注入被拒绝"))).Read<MacroPlayEvent>();
        Check.Equal("注入被拒绝", played?.Error, "试放结束通知：失败原因原样");

        MacroRenameRequest? rename = RoundTrip(LinkMessage.Request(66, MacroMethods.Rename,
            new MacroRenameRequest("m1", "改名后"))).Read<MacroRenameRequest>();
        Check.True(rename is { Id: "m1", Name: "改名后" }, "宏改名请求往返");

        MacroDeleteReply? deleteReply = RoundTrip(LinkMessage.Response(
                LinkMessage.Request(67, MacroMethods.Delete), new MacroDeleteReply(2, null)))
            .Read<MacroDeleteReply>();
        Check.True(deleteReply is { RemovedActions: 2 }, "删宏答复：一起删掉的动作数原样");

        // AI 接入（M3-5c-3c）：MCP 服务与 ai/ 只有执行器一个写者，宿主看状态 / 存四项。
        Check.True(new[] { McpMethods.Status, McpMethods.Apply, McpMethods.ResetToken, McpMethods.Knowledge }
            .All(n => n.StartsWith("mcp.", StringComparison.Ordinal)), "AI 方法名都有 mcp. 前缀");

        McpApplyRequest? mcpApply = RoundTrip(LinkMessage.Request(68, McpMethods.Apply,
            new McpApplyRequest(true, 18766, true, AiApprovalMode.full))).Read<McpApplyRequest>();
        Check.True(mcpApply is { Enabled: true, Port: 18766, AllowWrites: true }, "AI 保存请求：开关 / 端口 / 写入原样");
        Check.True(mcpApply?.ApprovalMode == AiApprovalMode.full, "AI 保存请求：审批模式原样");

        McpState? mcpState = RoundTrip(LinkMessage.Request(69, McpMethods.Status, new McpState(
                Enabled: true, Port: 18766, AllowWrites: true, ApprovalMode: AiApprovalMode.sandbox,
                Token: "lt_smoke", Running: true, RunningPort: 18767, LastError: "端口被占用")))
            .Read<McpState>();
        Check.True(mcpState is { Port: 18766, Running: true, RunningPort: 18767 }, "AI 状态：端口 / 运行状态原样");
        Check.Equal("lt_smoke", mcpState?.Token, "AI 状态：令牌原样（宿主拿它拼提示词）");
        Check.Equal("端口被占用", mcpState?.LastError, "AI 状态：最近错误原样");
        Check.True(mcpState?.ApprovalMode == AiApprovalMode.sandbox, "AI 状态：审批模式原样");

        McpKnowledgeReply? mcpKnowledge = RoundTrip(LinkMessage.Request(70, McpMethods.Knowledge,
            new McpKnowledgeReply(93))).Read<McpKnowledgeReply>();
        Check.Equal(93, mcpKnowledge?.Units, "知识包重建结果：动作单元数");

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
