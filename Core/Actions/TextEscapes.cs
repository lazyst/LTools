namespace LTools.Core.Actions;

/// <summary>
/// 参数里「看不见的字符」的写法：<c>\n</c> / <c>\r</c> / <c>\t</c> / <c>\\</c>。
/// </summary>
/// <remarks>
/// <para>
/// 拆分的分隔符要在单行输入框里填，换行 / 制表符根本敲不出来（<c>Enter</c> 在单行框里是「确定」）。
/// 所以这些参数按约定认反斜杠转义：填 <c>\n</c> 就是换行、<c>\r\n</c> 是 Windows 换行、
/// <c>\t</c> 是制表符，<c>\\</c> 表示一个真正的反斜杠（想按字面「反斜杠 + n」拆就写 <c>\\n</c>）。
/// </para>
/// <para>
/// 只给这类「分隔符 / 分隔文本」参数用（目前是「文本处理」与「列表操作」的拆分 / 拼接），<b>不要</b>
/// 套到普通文本参数上——那里用户写 <c>C:\new</c> 这种 Windows 路径很常见，转义会把 <c>\n</c>
/// 变成换行、把路径拆坏。
/// </para>
/// </remarks>
internal static class TextEscapes
{
    /// <summary>还原常见的反斜杠转义；认不出的写法原样保留（<c>\d</c> 仍是 <c>\d</c>）。</summary>
    public static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;

        var buffer = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                buffer.Append(c);
                continue;
            }

            char next = text[++i];
            switch (next)
            {
                case 'n': buffer.Append('\n'); break;
                case 'r': buffer.Append('\r'); break;
                case 't': buffer.Append('\t'); break;
                case '\\': buffer.Append('\\'); break;
                default:
                    buffer.Append('\\').Append(next);
                    break;
            }
        }

        return buffer.ToString();
    }
}
