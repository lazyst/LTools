using LTools.Core.Actions;

namespace LTools.Core.Link;

/// <summary>
/// 全局子动作库写入的方法名（**执行器 → 宿主**）：<c>subactions/</c> 的写者是普通权限宿主
/// （<c>docs/PROCESS_SPLIT.md</c> §15.3：目录族跟着它的界面走，落盘那一步先转过去）。
/// </summary>
/// <remarks>
/// 与 <see cref="ActionMethods"/> 同一套思路，但子动作是**一个文件一个定义**（没有清单），
/// 所以按「一份定义 / 一个 id」转，而不是整库快照——整库快照会让两边的内存缓存互相覆盖。
/// 读仍然各读各的：<c>sub.run</c> 在执行器里跑，必须在那边读得到。
/// </remarks>
internal static class SubActionMethods
{
    /// <summary>写入一份子动作（<c>CreateNew</c> = 新建，目标已存在要拒）。</summary>
    public const string Save = "subaction.save";

    /// <summary>删除一个子动作（不存在时什么也不做）。</summary>
    public const string Delete = "subaction.delete";
}

/// <summary>
/// 写入一份子动作。<see cref="CreateNew"/> = 目标文件已存在时拒绝，
/// 避免新生成的 Id 撞车时静默覆盖别人的子动作。
/// </summary>
internal sealed record SubActionSaveRequest(SubActionDef Def, bool CreateNew);

/// <summary>删掉一个子动作（id 就是文件名）。</summary>
internal sealed record SubActionDeleteRequest(string Id);

/// <summary><see cref="Ok"/> = 宿主写完 / 删完；<see cref="Error"/> 是失败原因（执行器据此退回本地写）。</summary>
internal sealed record SubActionReply(bool Ok, string? Error);
