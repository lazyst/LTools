namespace LTools.Core.Actions;

/// <summary>
/// 输出的默认变量名：新建步骤勾上某项输出时，框里预填的名字。
/// </summary>
/// <remarks>
/// <para>
/// 规则只有一条：<see cref="OutputSpec.DefaultVariable"/> 写了就用它（重要的、容易说清楚的
/// 输出都写了，如 <c>windowFound</c> / <c>matchX</c>）；没写时，**只有跨动作单元重名的通用词**
/// 才加动作单元前缀（<c>screen.findImage</c> 的 <c>value</c> → <c>screenValue</c>），
/// 其余照用键名（<c>isFile</c> / <c>fileCount</c> 本来就不会撞）。
/// </para>
/// <para>
/// 以前没写 <see cref="OutputSpec.DefaultVariable"/> 的输出一律用键名，于是
/// <c>found</c> / <c>count</c> / <c>path</c> / <c>value</c> 在同一个动作里被两三个步骤
/// 反复覆盖，变量表还只显示第一个来源。前缀由动作单元键的第一段给（<c>screen.*</c> → <c>screen</c>），
/// 所以新增动作单元不用改这里。
/// </para>
/// </remarks>
internal static class OutputNaming
{
    /// <summary>这些键名太通用，几乎每个动作单元都有；不加前缀就会互相覆盖。</summary>
    private static readonly System.Collections.Generic.HashSet<string> Generic = new(
        System.StringComparer.OrdinalIgnoreCase)
    {
        "value", "text", "found", "count", "path", "ok", "list", "first",
        "names", "items", "texts", "ids", "all", "number", "score", "title", "time", "folder",
    };

    /// <summary>这个输出建议的变量名。</summary>
    public static string Suggest(string unitKey, OutputSpec output)
    {
        if (!string.IsNullOrWhiteSpace(output.DefaultVariable))
            return output.DefaultVariable!.Trim();

        if (!Generic.Contains(output.Key) || output.Key.Length == 0)
            return output.Key;

        return Prefix(unitKey) + char.ToUpperInvariant(output.Key[0]) + output.Key[1..];
    }

    /// <summary>动作单元键的第一段（<c>screen.findImage</c> → <c>screen</c>）。</summary>
    private static string Prefix(string unitKey)
    {
        int dot = unitKey.IndexOf('.');
        return dot > 0 ? unitKey[..dot] : "v";
    }
}
