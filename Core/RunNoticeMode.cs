namespace LTools.Core;

/// <summary>
/// 动作运行结束后的提示策略（设置 → 通用）。
/// </summary>
/// <remarks>
/// 默认只在失败时提示：动作是高频操作，成功也弹一条会很快变成噪音；
/// 但失败必须留得住——见 <see cref="ToastService"/> 的常驻提示。
/// </remarks>
public enum RunNoticeMode
{
    /// <summary>什么也不提示（只看日志）。</summary>
    off,

    /// <summary>只在失败 / 被中止时提示。</summary>
    onFailure,

    /// <summary>成功也提示一条（2.5 秒后自动消失）。</summary>
    always,
}
