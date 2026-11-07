using System.Threading;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 发送文本的共用实现（自动 / 键入 / 粘贴三种模式）。
/// </summary>
/// <remarks>
/// 「发送文本」单元与旧 <see cref="ActionExecutor"/> 的 sendText / sendKeys 共用，
/// 语义见旧实现的注释：auto 走 Unicode 注入（不碰剪贴板）、type 走真实按键（受输入法影响）、
/// paste 走剪贴板 + 150ms 后恢复。
/// </remarks>
internal static class TextSender
{
    /// <summary>按模式发送文本；不含焦点延时、不含追加回车。</summary>
    public static void Dispatch(string text, SendTextMode mode, string label = "")
    {
        if (mode == SendTextMode.paste)
        {
            // 先备份原剪贴板文本（非文本内容如图片无法经文本接口恢复——粘贴模式必然覆盖）。
            bool hadOld = NativeClipboard.TryGetText(out var oldText);
            if (!NativeClipboard.SetText(text))
                throw new InvalidOperationException($"写剪贴板失败: {label}");

            InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
            // 等目标窗口读走剪贴板再恢复，否则粘到原内容；150ms 对前台应用足够。
            Thread.Sleep(150);
            // 仅当剪贴板仍是我们写入的文本（用户未在此期间复制新内容）才恢复，避免覆盖新复制。
            if (hadOld && NativeClipboard.TryGetText(out var now) && now == text)
                NativeClipboard.SetText(oldText);
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
