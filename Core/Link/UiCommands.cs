using System.Collections.Generic;

namespace LTools.Core.Link;

/// <summary>
/// 交互类动作单元的方法名（**提权进程 → 宿主**）：动作在执行器里跑，但界面归普通权限宿主。
/// </summary>
/// <remarks>
/// 与面板那批（宿主 → 执行器）方向相反：这里是执行器**发请求**、宿主弹窗并回值。
/// 见 <c>docs/PROCESS_SPLIT.md</c> §15.2。
/// </remarks>
internal static class UiMethods
{
    /// <summary>要一段文本（对应动作单元 <c>ui.input</c>）。</summary>
    public const string Input = "ui.input";

    /// <summary>确认框 / 选择器（对应 <c>ui.confirm</c> / <c>ui.select</c>）。</summary>
    public const string Choice = "ui.choice";

    /// <summary>关掉一个还开着的交互窗口（停止热键 / 超时）。</summary>
    public const string Cancel = "ui.cancel";
}

/// <summary>要文本：<see cref="DialogId"/> 用来在停止 / 超时时把窗口关掉。</summary>
internal sealed record UiInputRequest(string DialogId, string Title, string Prompt, string Default, bool Multiline);

/// <summary>要文本的结果；<see cref="Ok"/> = 用户点了确定（取消 / 关窗为 false）。</summary>
internal sealed record UiInputReply(bool Ok, string Text);

/// <summary>
/// 确认框 / 选择器。<see cref="SelectMode"/> = true 时 <see cref="Options"/> 是候选；
/// <see cref="TimeoutMs"/> = 0 表示一直等。
/// </summary>
internal sealed record UiChoiceRequest(
    string DialogId,
    string Title,
    string Message,
    List<string> Options,
    List<string> Buttons,
    bool SelectMode,
    int TimeoutMs);

/// <summary>选择结果：<see cref="Accepted"/> = 作答了；<see cref="Index"/> = -1 表示没作答。</summary>
internal sealed record UiChoiceReply(bool Accepted, int Index, string Value);

/// <summary>关掉一个交互窗口（按 <see cref="DialogId"/> 找）。</summary>
internal sealed record UiCancelRequest(string DialogId);
