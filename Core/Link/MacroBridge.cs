using System;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// 宏录制的**宿主侧**：把「开录 / 停录」交给执行器，按 token 把结束 / 保存摊回编辑器。
/// </summary>
/// <remarks>
/// 只有宿主里的编辑器会调（设置页的宏页还在执行器里，直接走 <c>MacroRecorder</c>）。
/// 失败一律给可显示的原因；录制中不能重入（执行器也只接一个）。
/// </remarks>
internal static class MacroBridge
{
    private const int StartTimeoutMs = 3_000;

    private static LinkServer? _link;
    private static string? _token;
    private static bool _recording;

    /// <summary>录制结束（成功与否都算结束，编辑器据此把自己显示回来）。</summary>
    public static event Action? Stopped;

    /// <summary>宏已经存好（<c>MacroId</c>）：横幅上的时长 / 事件数要重读。</summary>
    public static event Action<string>? Saved;

    public static bool IsRecording => _recording;

    /// <summary>宿主启动链路服务端后挂上。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>开始录制（<paramref name="id"/> 非空 = 重新录制到那个宏）。</summary>
    public static bool StartInto(string? id, string? name, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上，没法录制";
            return false;
        }

        string token = Guid.NewGuid().ToString("N")[..8];
        try
        {
            LinkMessage reply = link.RequestAsync(MacroMethods.Record,
                    new MacroRecordRequest(token, id, name), timeoutMs: StartTimeoutMs)
                .GetAwaiter().GetResult();

            MacroRecordReply? result = reply.Read<MacroRecordReply>();
            if (!reply.Ok || result is not { Started: true })
            {
                error = result?.Error ?? reply.Error ?? "没能开始录制";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        _token = token;
        _recording = true;
        return true;
    }

    /// <summary>结束录制（关编辑器时兜底）。执行器那边会照常推 <c>macro.stopped</c>。</summary>
    public static void Stop()
    {
        LinkServer? link = _link;
        if (link == null || !link.IsConnected) return;
        _ = Task.Run(async () =>
        {
            try { await link.RequestAsync(MacroMethods.Stop, timeoutMs: StartTimeoutMs); }
            catch (Exception ex) { AppLog.Warn("Macro", "停止录制失败：" + ex.Message); }
        });
    }

    /// <summary>执行器推来的事件（只在 token 对得上时往外发，串台的通知直接丢）。</summary>
    public static void OnEvent(LinkMessage message)
    {
        MacroRecordEvent? e = message.Read<MacroRecordEvent>();
        if (e == null || _token == null || !string.Equals(e.Token, _token, StringComparison.Ordinal)) return;

        if (message.Method == MacroMethods.EventStopped)
        {
            _recording = false;
            Stopped?.Invoke();
            return;
        }

        // macro.saved 是这次录制的收尾：到这里才清 token（停止通知之后几毫秒才来）。
        _token = null;
        Saved?.Invoke(e.MacroId ?? "");
    }
}
