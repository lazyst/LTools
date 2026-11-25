using System.Threading.Tasks;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 宏录制的**执行器侧**：录制要钩子，只有这里能真录。
/// </summary>
/// <remarks>
/// <para>
/// 编辑器搬进宿主之后（M3-5c-2），宏横幅上的「重新录制」仍要能用，所以录制单独过链路：
/// 宿主发 <c>macro.record</c>（带 token），这里调 <see cref="MacroRecorder"/>，结束 / 存好后按
/// 同一个 token 推 <c>macro.stopped</c> / <c>macro.saved</c> 回去。
/// </para>
/// <para>
/// 本机自己发起的录制（托盘热键 / 宏页 / 面板）**不往回推**：没有 token 就没有人在等。
/// </para>
/// </remarks>
internal static class MacroHost
{
    /// <summary>一次由宿主发起的录制（<see cref="Channel"/> 是那个宿主连接）。</summary>
    private sealed record Pending(LinkChannel Channel, string Token);

    private static Pending? _recording;   // 正在录
    private static Pending? _saving;      // 刚停、等宏存好（只用来推 macro.saved）

    static MacroHost()
    {
        // 录制由谁发起都可能，这里统一接住结束 / 保存两个时机。
        MacroRecorder.RecordingStopped += OnStopped;
        MacroRecorder.MacroSaved += OnSaved;
    }

    /// <summary>处理录制相关的请求；不是这两条时返回 null。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkChannel channel, LinkMessage request) =>
        request.Method switch
        {
            MacroMethods.Record => Task.FromResult(Record(channel, request)),
            MacroMethods.Stop => Task.FromResult(Stop(request)),
            _ => null,
        };

    private static LinkMessage Record(LinkChannel channel, LinkMessage request)
    {
        MacroRecordRequest? req = request.Read<MacroRecordRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        // 一次只服务一个等待者：多按一次不该把上一个的结束通知对丢。
        if (_recording != null)
            return LinkMessage.Response(request, new MacroRecordReply(false, "已经在录制了"));

        if (string.IsNullOrWhiteSpace(req.Id)) MacroRecorder.Start();
        else MacroRecorder.StartInto(req.Id, req.Name);

        // 被拒绝时 MacroRecorder 自己会弹托盘提示（宏功能关了 / 正在回放 / 有动作在跑）。
        if (!MacroRecorder.IsRecording)
            return LinkMessage.Response(request, new MacroRecordReply(false, "没能开始录制"));

        _recording = new Pending(channel, req.Token);
        return LinkMessage.Response(request, new MacroRecordReply(true, null));
    }

    private static LinkMessage Stop(LinkMessage request)
    {
        MacroRecorder.Stop(force: true);
        return LinkMessage.Response(request);
    }

    private static void OnStopped()
    {
        _saving = _recording;
        _recording = null;

        if (_saving is { } pending)
            _ = pending.Channel.NotifyAsync(MacroMethods.EventStopped, new MacroRecordEvent(pending.Token));
    }

    private static void OnSaved(string id)
    {
        if (_saving is not { } pending) return;
        _saving = null;
        _ = pending.Channel.NotifyAsync(MacroMethods.EventSaved, new MacroRecordEvent(pending.Token, id));
    }
}
