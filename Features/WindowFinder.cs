using System;
using System.Text;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 按「标题包含 / 类名精确」查找可见的顶层窗口。
/// </summary>
/// <remarks>
/// 条件里的「窗口」取值器与「窗口操作」动作共用这一套匹配语义，避免两处行为不一致：
/// 标题按包含、不区分大小写；类名按完全相等、不区分大小写；两者都留空 = 任意可见窗口。
/// <see cref="Win32.EnumWindows"/> 按 z 序回调，因此命中的是**最上面**的那个窗口。
/// </remarks>
internal static class WindowFinder
{
    /// <summary>找第一个匹配的可见窗口；找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr Find(string? titleFragment, string? className) =>
        Find(titleFragment, className, out _);

    /// <summary>找第一个匹配的可见窗口，并带回它的实际标题（日志 / 提示用）。</summary>
    public static IntPtr Find(string? titleFragment, string? className, out string matchedTitle)
    {
        IntPtr found = IntPtr.Zero;
        string title = "";

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            if (!Matches(hwnd, titleFragment, className, out string text)) return true;
            found = hwnd;
            title = text;
            return false;   // 找到第一个就停
        }, IntPtr.Zero);

        matchedTitle = title;
        return found;
    }

    /// <summary>窗口是否满足匹配条件（<paramref name="title"/> 为该窗口的实际标题）。</summary>
    public static bool Matches(IntPtr hwnd, string? titleFragment, string? className, out string title)
    {
        title = GetText(hwnd);

        if (!string.IsNullOrWhiteSpace(titleFragment) &&
            !title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(className) &&
            !string.Equals(GetClass(hwnd), className, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>读窗口标题（无标题返回空串）。</summary>
    public static string GetText(IntPtr hwnd)
    {
        int len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>读窗口类名。</summary>
    public static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>把匹配条件说成一句人话（「标题包含「记事本」」）。</summary>
    public static string Describe(string? titleFragment, string? className)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(titleFragment);
        bool hasClass = !string.IsNullOrWhiteSpace(className);

        if (hasTitle && hasClass) return $"标题包含「{titleFragment}」且类名为 {className}";
        if (hasTitle) return $"标题包含「{titleFragment}」";
        if (hasClass) return $"类名为 {className}";
        return "任意窗口";
    }
}
