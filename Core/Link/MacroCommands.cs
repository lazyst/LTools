namespace LTools.Core.Link;

/// <summary>
/// 键鼠宏的方法名（**宿主 → 执行器**）：录制与回放都要钩子 / 键鼠注入，只能在提权进程里做；
/// 改名与删宏会连带改动作，动作的内容属主也在那里。
/// </summary>
/// <remarks>
/// <para>
/// 设置 →「键鼠宏」页开在宿主里（M3-5c-3b），按钮不能是假的：列表与「回放参数」是纯
/// <c>configs/macros.json</c> 读写，两边各读各写（宿主是物理写者）；剩下三件事过链路。
/// </para>
/// <para>
/// **通知不带 token**：录制是进程内唯一的，事件本身就是「现在是什么状态」，谁开着宏相关窗口都该
/// 跟着让开 / 刷新。token 那层是为了「多个编辑器里找出发起它的那一个」，而现在只有一个宿主进程，
/// 徒增一层过滤，还会让「用热键开始录制」时设置页收不到通知。
/// </para>
/// </remarks>
internal static class MacroMethods
{
    /// <summary>开始录制（<see cref="MacroRecordRequest.Id"/> 非空 = 重新录制到那个宏）。</summary>
    public const string Record = "macro.record";

    /// <summary>结束录制（关编辑器时兜底：录制中窗口本该是藏起来的）。</summary>
    public const string Stop = "macro.stop";

    /// <summary>试放一个宏（键鼠注入）；回放跑完由 <see cref="EventPlayed"/> 回来。</summary>
    public const string Play = "macro.play";

    /// <summary>改名：元数据 + 引用它的动作名一起改。</summary>
    public const string Rename = "macro.rename";

    /// <summary>删宏：事件文件 + 元数据 + 引用它的动作一起删。</summary>
    public const string Delete = "macro.delete";

    /// <summary>录制开始了（不管是谁发起的：按钮 / 热键 / 面板菜单都算）。</summary>
    public const string EventStarted = "macro.started";

    /// <summary>录制结束（成功与否都在这里，宿主据此把藏起来的窗口显示回来）。</summary>
    public const string EventStopped = "macro.stopped";

    /// <summary>宏已保存（<see cref="MacroSavedEvent.MacroId"/>）：宿主刷新横幅 / 列表。</summary>
    public const string EventSaved = "macro.saved";

    /// <summary>回放跑完了（<see cref="MacroPlayEvent.Error"/> 非空 = 失败原因）。</summary>
    public const string EventPlayed = "macro.played";
}

/// <summary>开始录制的请求。</summary>
internal sealed record MacroRecordRequest(string? Id = null, string? Name = null);

/// <summary>录制是否真的开始了；没开始（宏功能关了 / 正在回放 / 有动作在跑）给原因。</summary>
internal sealed record MacroRecordReply(bool Started, string? Error);

/// <summary>宏已保存：带宏 Id（编辑器据此只刷新自己那段，列表无条件刷新）。</summary>
internal sealed record MacroSavedEvent(string MacroId);

/// <summary>试放一个宏。</summary>
internal sealed record MacroPlayRequest(string Id);

/// <summary>试放是否真的开始了；忙（正在录制 / 另一个在回放）或宏文件不在时给原因。</summary>
internal sealed record MacroPlayReply(bool Started, string? Error);

/// <summary>回放跑完；<see cref="Error"/> 为空 = 正常放完。</summary>
internal sealed record MacroPlayEvent(string? Error);

/// <summary>改名（新名字已去掉首尾空白）。</summary>
internal sealed record MacroRenameRequest(string Id, string Name);

/// <summary>删宏。</summary>
internal sealed record MacroDeleteRequest(string Id);

/// <summary>删宏的结果：<see cref="RemovedActions"/> = 一起删掉的动作数（一宏一动作）。</summary>
internal sealed record MacroDeleteReply(int RemovedActions, string? Error);
