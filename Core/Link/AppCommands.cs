using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 只有提权进程做得了的「应用级」操作（**宿主 → 执行器**）。
/// </summary>
/// <remarks>
/// <para>
/// 开机自启：要建一个 <c>RunLevel=HighestAvailable</c> 的任务计划，**必须提权**
/// （普通权限的宿主里 <c>schtasks /create</c> 会 Access denied）；而且任务里存的是
/// <c>Environment.ProcessPath</c>——宿主里那是 <c>UiHost.exe</c>，注册出去就是一条无效自启。
/// </para>
/// <para>
/// 清空历史记录：`state/run-history.jsonl` 与 `state/trigger-history.jsonl` 只有执行器在写
/// （它才是运行 / 触发的产生者，进程内还有一份缓存）。宿主里的设置页只负责显示与发命令，
/// 自己删文件的话下一轮追加会把刚清掉的条目又写回来。
/// </para>
/// </remarks>
internal static class AppMethods
{
    /// <summary>查询 / 启用 / 禁用开机自启（<see cref="AppAutoStartRequest.Action"/>）。</summary>
    public const string AutoStart = "app.autostart";

    /// <summary>清空一份执行器写的记录（<see cref="AppClearHistoryRequest.Kind"/>）。</summary>
    public const string ClearHistory = "app.clearHistory";
}

/// <summary><see cref="AppAutoStartRequest.Action"/> 的取值。</summary>
internal static class AutoStartActions
{
    public const string Query = "query";
    public const string Enable = "enable";
    public const string Disable = "disable";
}

/// <summary><see cref="AppClearHistoryRequest.Kind"/> 的取值。</summary>
internal static class HistoryRecords
{
    /// <summary>运行历史（<c>state/run-history.jsonl</c>）。</summary>
    public const string Run = "run";

    /// <summary>触发记录（<c>state/trigger-history.jsonl</c>）。</summary>
    public const string Trigger = "trigger";
}

/// <summary><see cref="Action"/> = <c>query</c> / <c>enable</c> / <c>disable</c>。</summary>
internal sealed record AppAutoStartRequest(string Action);

/// <summary>自启操作结果 + 做完之后的任务现状（失败时 <see cref="Info"/> 无意义）。</summary>
internal sealed record AppAutoStartReply(bool Ok, AutoStartInfo Info, string? Error);

/// <summary>要清掉哪一份记录。</summary>
internal sealed record AppClearHistoryRequest(string Kind);

/// <summary>「做了 / 没做」+ 可显示的原因（没有附带数据的应用级命令用）。</summary>
internal sealed record AppAck(bool Ok, string? Error);
