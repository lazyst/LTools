using System.Collections.Generic;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作库写入的方法名（**执行器 → 宿主**）：<c>actions/</c> 的写者是普通权限宿主
/// （<c>docs/PROCESS_SPLIT.md</c> §15.3：目录族跟着它的界面走，落盘那一步先转过去）。
/// </summary>
/// <remarks>
/// 只翻「谁落盘」：执行器照旧在内存里增删改（`ConfigStore.AddAction` / `UpdateAction` /
/// `RemoveAction` / `DuplicateAction`），最后 <c>ActionLibrary.Save</c> 把**整份清单**交给宿主，
/// 由它按一个动作一个目录写到磁盘（含历史快照、搬 trash）。不造「动作级 RPC」——
/// 那会让每个调用点都要记得转一次。
/// </remarks>
internal static class ActionMethods
{
    /// <summary>整份动作清单交给宿主落盘。</summary>
    public const string SaveAll = "action.saveAll";
}

/// <summary>整份动作清单（与 <c>ActionLibrary.Save</c> 的 snapshot 同形）。</summary>
internal sealed record ActionSaveRequest(List<ActionDto> Actions);

/// <summary><see cref="Ok"/> = 宿主写完；<see cref="Error"/> 是失败原因（执行器据此退回本地写）。</summary>
internal sealed record ActionSaveReply(bool Ok, string? Error);
