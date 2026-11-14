using System;
using System.Windows;
using LTools.Core;

namespace LTools.Views.Controls;

/// <summary>
/// 把一段文本放进剪贴板（右键菜单里的「复制名称 / 复制 Id」共用）。
/// </summary>
/// <remarks>
/// 剪贴板是全局独占资源，别的程序正占着时 <see cref="Clipboard.SetText(string)"/> 会抛
/// <c>COMException</c>——菜单项失败不该把面板带崩，所以这里收成一条崩溃日志（返回值给调用方
/// 判断要不要提示，目前用不上）。空文本直接返回 false，别把剪贴板清成空。
/// </remarks>
internal static class ClipboardCopy
{
    /// <summary>复制文本；空文本或剪贴板被占用时返回 false。</summary>
    public static bool Text(string? text, string context)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(context, ex);
            return false;
        }
    }
}
