namespace LTools.Core.Link;

/// <summary>
/// 速记相关的方法名（**执行器 → 宿主**）：<c>速记/</c> 的写者与速记窗口都在普通权限宿主里
/// （<c>docs/PROCESS_SPLIT.md</c> §7 / §15.4 第 1 步）。
/// </summary>
internal static class NoteMethods
{
    /// <summary>事件：开关速记窗口（执行器的托盘 / 快捷键只表达意图）。</summary>
    public const string Toggle = "note.toggle";

    /// <summary>请求：写一条速记（<c>data.noteWrite</c> 动作单元在提权进程里跑）。</summary>
    public const string Write = "note.write";
}

/// <summary>
/// 写速记的结果。<see cref="Ok"/> = 写成了；<see cref="Error"/> 非空时是宿主那边的失败原因
/// （动作单元据此报错，**不要**再退回本地写一遍：可能已经写了一半）。
/// </summary>
internal sealed record NoteWriteReply(bool Ok, string? Title, string? Time, string? File, string? Error);
