using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LTools.Views;

/// <summary>
/// 视觉树 / 逻辑树的查找：事件处理里「这次点的是哪一行 / 哪块磁贴」都从这里走。
/// </summary>
/// <remarks>
/// <para>
/// **模板里生成的内容往往不是 Visual**（<c>DataTemplate</c> / <c>ControlTemplate</c> 里的
/// <c>Run</c>、<c>ContentElement</c>），一路往上的父链会在视觉树与逻辑树之间切换，
/// 所以两个都要试。
/// </para>
/// <para>
/// **别再在窗口里各写一份 <c>FindAncestor&lt;T&gt;</c>**：全项目原来有十几份，
/// 行为还不一致（只走视觉树的那几份点到模板里的文字就找不到）。
/// </para>
/// </remarks>
internal static class VisualTree
{
    /// <summary>从 <paramref name="source"/> 起（含自己）往上找第一个 <typeparamref name="T"/>。</summary>
    public static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = Parent(source);
        }
        return null;
    }

    /// <summary>最近的一个父级：视觉元素走视觉树，其余（内容元素）走逻辑树。</summary>
    private static DependencyObject? Parent(DependencyObject source) =>
        source is Visual or Visual3D
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);

    /// <summary>
    /// <paramref name="container"/> 是不是 <paramref name="node"/> 的祖先（含自己）：
    /// 「这一下点在不在那块界面里」（工具条 / 输入框 / 二维码卡片都靠它判）。
    /// </summary>
    public static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, container)) return true;
            node = Parent(node);
        }
        return false;
    }
}
