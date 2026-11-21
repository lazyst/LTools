using System;
using LTools.Features.Builtin;

namespace LTools.Features.Panel;

/// <summary>面板剪贴板里那一份的「意图」。</summary>
internal enum ActionClipKind
{
    /// <summary>只是把同一个动作 / 内置功能再放到另一个位置：动作本体只有一份，改内容处处生效。</summary>
    Reference,

    /// <summary>粘贴时现复制一份新动作：新 Id、名字加「副本」、<c>files/</c> 一并带走，之后两者各走各的。</summary>
    Copy,
}

/// <summary>剪贴板里的一项：类型 + 被复制的引用（动作 Id 或内置功能引用）+ 当时的显示名。</summary>
/// <param name="Reference">动作 Id，或内置功能的整串引用（<c>common.capture#quality=high</c>）。</param>
/// <param name="Name">复制那一刻的名字，只用于菜单 / toast 文案；粘贴时一律重新取最新的。</param>
internal sealed record ActionClipEntry(ActionClipKind Kind, string Reference, string Name)
{
    /// <summary>这条引用是不是内置功能（内置功能没有「副本」，只能复制引用）。</summary>
    public bool IsFeature => BindingRef.IsFeature(Reference);

    /// <summary>菜单上的标题：<c>粘贴「X」</c> / <c>粘贴「X」的副本</c>。</summary>
    public string PasteLabel => Kind == ActionClipKind.Copy
        ? $"粘贴「{Name}」的副本"
        : $"粘贴「{Name}」";

    /// <summary>toast / 日志里的一句话：<c>引用「X」</c> / <c>「X」的副本</c>。</summary>
    public string Describe => Kind == ActionClipKind.Copy ? $"「{Name}」的副本" : $"引用「{Name}」";
}

/// <summary>
/// 面板的复制剪贴板：右键「复制引用」/「复制为副本」放进来，到目标分组「粘贴」。
/// </summary>
/// <remarks>
/// <para>
/// **只在进程内**：关掉 LTools 就没了。用户要的是「复制完切到别的分组 / 场景去粘」，
/// 这种窗口期很短，落盘反而要处理「重启后粘的是什么版本」这类问题。
/// </para>
/// <para>
/// 存的是「引用 + 当时的名字」：名字只用来写菜单与提示，粘贴时一律回
/// <see cref="ActionRegistry"/> / <see cref="FeatureCatalog"/> 取最新的（动作改名后提示也跟着变）。
/// 内置功能只能「复制引用」——它没有本体可复制。
/// </para>
/// </remarks>
internal static class ActionClipboard
{
    private static ActionClipEntry? _entry;

    /// <summary>剪贴板里有没有东西（菜单据此决定显不显示「粘贴」）。</summary>
    public static bool Has => _entry != null;

    /// <summary>当前那一份；空剪贴板返回 null。</summary>
    public static ActionClipEntry? Entry => _entry;

    /// <summary>复制引用：动作 Id 或内置功能引用（带 <c>#参数</c> 的整串照原样保留）。</summary>
    public static ActionClipEntry CopyReference(string reference)
    {
        var entry = new ActionClipEntry(ActionClipKind.Reference, reference, BindingRef.Name(reference));
        _entry = entry;
        return entry;
    }

    /// <summary>复制为副本：只对真动作；内置功能请走 <see cref="CopyReference"/>。</summary>
    public static ActionClipEntry CopyCopy(string actionId)
    {
        var entry = new ActionClipEntry(ActionClipKind.Copy, actionId, BindingRef.Name(actionId));
        _entry = entry;
        return entry;
    }

    /// <summary>清空（粘贴发现源已被删时、或用户点「清空剪贴板」）。</summary>
    public static void Clear() => _entry = null;
}
