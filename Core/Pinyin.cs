namespace LTools.Core;

/// <summary>
/// 汉字拼音首字母（只服务搜索：输入 <c>jt</c> 能搜到「截图」）。
/// </summary>
/// <remarks>
/// <para>
/// 搜索框里一个汉字一个首字母，多个汉字连着打就是首字母串：`截图找图` → <c>jztz</c>，
/// 所以 `jz` / `jztz` 都能命中。英文 / 数字原样保留（小写），空格与标点丢掉——
/// 「HTTP 请求」的首字母串是 <c>httpqq</c>，`http` 两种写法都命中。
/// </para>
/// <para>
/// **只收项目里真实出现过的字**（动作单元名、分类名一共 160 个），故意不引入整套拼音表：
/// 那要带上几千字的数据、还会有人问「为什么多音字认错」。多音字按所在词取值
/// （`行` 取 xíng、`调` 取 diào 的「调用」、`重` 取 chóng 的「重复」）。
/// 新加动作单元时如果名字里有表里没有的字，冒烟 `[pinyin]` 那几条会直接报出来，
/// 照提示补一行就好——**别让它静默退化成「搜不到」**。
/// </para>
/// </remarks>
internal static class Pinyin
{
    /// <summary>
    /// 首字母 → 该字母下的汉字。一个字母一行，方便照着拼音表核对。
    /// </summary>
    private static readonly (char Initial, string Chars)[] Groups =
    {
        ('a', "按"),
        ('b', "别变板标步本表遍编"),
        ('c', "出拆存次查程错操窗重处"),
        ('d', "动定打待弹的等读点调"),
        ('f', "发复放返"),
        ('g', "光果管"),
        ('h', "哈宏回环"),
        ('j', "令件击剪夹径机据界脚解计记进键间截继"),
        ('k', "口开框空"),
        ('l', "临列历流浏理览路量络"),
        ('m', "命幕模码面默"),
        ('n', "内拟"),
        ('p', "屏片"),
        ('q', "前取启器请确清求"),
        ('r', "入如认日"),
        ('s', "上史数时算素色识设示送速输随鼠缩"),
        ('t', "停台图态提条统贴跳弹"),
        ('w', "位文网误"),
        ('x', "下信像希序循息行消系续选写"),
        ('y', "一与压用源运颜"),
        ('z', "中作在址志字子择正止状资置则找"),
    };

    private static readonly Dictionary<char, char> Table = Build();

    private static Dictionary<char, char> Build()
    {
        var table = new Dictionary<char, char>();
        foreach (var (initial, chars) in Groups)
            foreach (char ch in chars)
                table[ch] = initial;
        return table;
    }

    /// <summary>表里有几个字（冒烟用来确认没人把某一行的字删重了）。</summary>
    internal static int Count => Table.Count;

    /// <summary>这个汉字有没有拼音首字母（冒烟用；正文不需要问）。</summary>
    internal static bool Knows(char ch) => Table.ContainsKey(ch);

    /// <summary>
    /// 拼音首字母串：汉字 → 首字母，英文 / 数字 → 小写原样，其余（空格 / 标点 / 未知汉字）丢掉。
    /// </summary>
    internal static string Initials(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (Table.TryGetValue(ch, out char initial))
                sb.Append(initial);
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>搜索框里的一段输入：去掉空白、转小写（用户打「jz tz」也当「jztz」）。</summary>
    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";

        var sb = new System.Text.StringBuilder(query.Length);
        foreach (char ch in query)
            if (!char.IsWhiteSpace(ch))
                sb.Append(char.ToLowerInvariant(ch));

        return sb.ToString();
    }

    /// <summary>
    /// 搜索命中：原样子串（忽略大小写）**或**拼音首字母子串。
    /// 空查询一律算命中（调用方已经在外面判过空，这里只是别让「忘了判」变成搜不到）。
    /// </summary>
    internal static bool Matches(string? text, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return true;
        if (string.IsNullOrEmpty(text)) return false;

        if (text.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;

        return Initials(text).Contains(normalizedQuery, StringComparison.Ordinal);
    }
}
