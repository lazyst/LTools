using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Views;

/// <summary>
/// 「同一个目标同时只开一个编辑器」的登记处（动作 / 子动作各按 Id 认）。
/// </summary>
/// <remarks>
/// <para>
/// 同一个动作开两个编辑器会互相覆盖：两边各拿一份快照，谁后保存谁把对方刚改的步骤盖掉，
/// 而且第二个窗口关掉时还会按它的旧内容再写一遍。所以第二个入口不新开窗口，**把已有的拉到前面**。
/// </para>
/// <para>
/// 键由调用方给（动作 Id / 草稿的资源根 / 子动作 Id）；<c>null</c> = 不参与去重（例如「新建动作」
/// 没有 Id，用户想同时开几个草稿就开几个）。只登记 UI 线程上 <c>ShowDialog</c> 的那一个，
/// 关窗一定要 <see cref="Release"/>，否则下一次打开会去激活一个已经关掉的窗口。
/// </para>
/// </remarks>
internal static class SingleEditor
{
    private static readonly Dictionary<string, Window> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同一个目标已经开着编辑器：拉到前面并返回 true（调用方就此返回，不用再开）。</summary>
    public static bool Reveal(string? key)
    {
        if (string.IsNullOrEmpty(key) || !Open.TryGetValue(key, out Window? window)) return false;

        // 登记里残留了已经关掉的窗口：清掉，让调用方正常新建
        if (PresentationSource.FromVisual(window) == null)
        {
            Open.Remove(key);
            return false;
        }

        // 用户可能把它最小化了；统一走 WindowFront（最小化还原 + 真·提到最前，
        // 前台锁挡得住 Activate 时它会借 Topmost 抬一次再翻回，编辑器仍然不置顶）
        WindowFront.Show(window);
        return true;
    }

    /// <summary>登记「这个目标已经在编辑」；<paramref name="key"/> 为 null 时什么也不做。</summary>
    public static void Track(string? key, Window window)
    {
        if (!string.IsNullOrEmpty(key)) Open[key] = window;
    }

    /// <summary>关窗时注销（只注销自己，别把后来者挤掉）。</summary>
    public static void Release(string? key, Window window)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (Open.TryGetValue(key, out Window? current) && ReferenceEquals(current, window)) Open.Remove(key);
    }
}
