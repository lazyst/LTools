using System;
using System.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 发送文本的共用实现（自动 / 键入 / 粘贴三种模式）。
/// </summary>
/// <remarks>
/// 语义：auto 单行走 Unicode 注入（不碰剪贴板）、多行走剪贴板粘贴；
/// type 走真实按键（受输入法影响）；paste 走剪贴板 + 150ms 后还原。
/// </remarks>
internal static class TextSender
{
    /// <summary>按模式发送文本；不含焦点延时、不含追加回车。</summary>
    public static void Dispatch(string text, SendTextMode mode, string label = "")
    {
        // 自动模式下**含换行就改走粘贴**：Unicode 注入的 \n 是真正的 Enter 键，
        // 在「按 Enter 就发送 / 确认」的对话框里会被拆成好几段分别发出去（用户报过）。
        // 粘贴是一次 Ctrl+V，目标窗口自己决定换行怎么落地。
        if (mode == SendTextMode.auto && (text.Contains('\n') || text.Contains('\r')))
            mode = SendTextMode.paste;

        if (mode == SendTextMode.paste)
        {
            PasteText(text, label);
            return;
        }

        if (mode == SendTextMode.type)
        {
            TypeText(text, label);
            return;
        }

        InputHelper.SendUnicodeText(text);
    }

    /// <summary>
    /// 剪贴板粘贴：写入 → Ctrl+V → 等目标窗口读走 → 把剪贴板**整块还原**。
    /// </summary>
    /// <remarks>
    /// 粘贴必然覆盖剪贴板，所以先按格式整块快照（文本 / 文件列表 / 图片 / HTML / RTF…），
    /// 粘完再原样写回——只还原文本的话，用户原来复制的图片 / 文件会凭空消失。
    /// 还原前确认剪贴板里还是我们写进去的那段文本：用户中途自己复制了别的东西就别覆盖。
    /// </remarks>
    private static void PasteText(string text, string label)
    {
        var snapshot = NativeClipboard.Capture();
        if (snapshot == null)
            AppLog.Warn("Text", "粘贴前读剪贴板失败，粘贴后不会还原原内容");

        if (!NativeClipboard.SetText(text))
            throw new InvalidOperationException($"写剪贴板失败: {label}");

        InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
        // 等目标窗口读走剪贴板再恢复，否则粘到原内容；150ms 对前台应用足够。
        Thread.Sleep(150);

        if (snapshot == null) return;
        if (!NativeClipboard.TryGetText(out var now) || now != text) return;

        if (!NativeClipboard.Restore(snapshot))
            AppLog.Warn("Text", "粘贴后还原剪贴板失败（可能被别的程序占用）");
    }

    /// <summary>
    /// 键入式发送：预检全部字符可映射后逐字符发送；遇 <c>\n</c> / <c>\r\n</c> 转 Enter、<c>\t</c> 转 Tab。
    /// 强制 type 模式遇不可映射字符（中文等）**报错而非静默改走剪贴板**。
    /// </summary>
    public static void TypeText(string text, string label = "")
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n' || ch == '\r' || ch == '\t') continue;
            if (!InputHelper.CanType(ch))
                throw new InvalidOperationException(
                    $"键入方式无法映射字符 U+{(int)ch:X4}（非当前键盘布局字符），请改用「自动」或「粘贴」模式: {label}");
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;  // \r\n 算一次回车
                InputHelper.Tap((ushort)Win32.VkReturn);
            }
            else if (ch == '\n') InputHelper.Tap((ushort)Win32.VkReturn);
            else if (ch == '\t') InputHelper.Tap((ushort)Win32.VkTab);
            else InputHelper.SendChar(ch);
        }
    }
}
