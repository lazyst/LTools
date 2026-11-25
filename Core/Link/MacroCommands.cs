using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// 宏录制的方法名（**宿主 → 执行器**）：录制要钩子，只能在提权进程里做。
/// </summary>
/// <remarks>
/// 编辑器现在开在宿主里（M3-5c-2），它的宏横幅上还留着「重新录制」——按钮不能是假的，
/// 所以录制这件事单独过链路；回放仍走 <c>panel.op</c> 的 <c>replayMacro</c>。
/// </remarks>
internal static class MacroMethods
{
    /// <summary>开始录制（<see cref="MacroRecordRequest.Id"/> 非空 = 重新录制到那个宏）。</summary>
    public const string Record = "macro.record";

    /// <summary>结束录制（关编辑器时兜底：录制中窗口本该是藏起来的）。</summary>
    public const string Stop = "macro.stop";

    /// <summary>录制结束（成功与否都在这里，宿主据此把藏起来的编辑器显示回来）。</summary>
    public const string EventStopped = "macro.stopped";

    /// <summary>宏已保存（<see cref="MacroRecordEvent.MacroId"/>）：宿主刷新横幅上的摘要。</summary>
    public const string EventSaved = "macro.saved";
}

/// <summary>开始录制的请求。<see cref="Token"/> 用来把「结束通知」对回发起它的那个编辑器。</summary>
internal sealed record MacroRecordRequest(string Token, string? Id = null, string? Name = null);

/// <summary>录制是否真的开始了；没开始（宏功能关了 / 正在回放 / 有动作在跑）给原因。</summary>
internal sealed record MacroRecordReply(bool Started, string? Error);

/// <summary>录制的开始 / 结束通知，带发起时的 <see cref="Token"/>。</summary>
internal sealed record MacroRecordEvent(string Token, string? MacroId = null);
