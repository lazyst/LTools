namespace LTools.Core.SelfTest;

/// <summary>
/// 自测断言（<c>LTools.exe --selftest</c> 用）。只记「通过 / 失败」与失败详情，
/// 不引第三方测试框架：见 <c>docs/ACTION_SYSTEM_OPTIMIZATION.md</c> P0 的决策表。
/// </summary>
internal static class Check
{
    public static int Passed { get; private set; }

    public static int Failed { get; private set; }

    /// <summary>当前用例（失败信息里带它，方便一眼看出是哪个用例）。</summary>
    public static string Current { get; set; } = "";

    public static List<string> Failures { get; } = new();

    /// <summary>提示（不算失败）：例如「哪些枚举值还没有中文名」。</summary>
    public static List<string> Notes { get; } = new();

    public static void Reset()
    {
        Passed = 0;
        Failed = 0;
        Failures.Clear();
        Notes.Clear();
        Current = "";
    }

    public static void True(bool condition, string what)
    {
        if (condition) { Passed++; return; }
        Fail(what, "期望为真");
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) { Passed++; return; }
        Fail(what, $"期望「{expected}」实际「{actual}」");
    }

    public static void NotEqual<T>(T unexpected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(unexpected, actual)) { Passed++; return; }
        Fail(what, $"期望不等于「{unexpected}」");
    }

    public static void Contains(string? text, string needle, string? what = null)
    {
        if (text != null && text.Contains(needle, StringComparison.Ordinal)) { Passed++; return; }
        Fail(what ?? $"应包含「{needle}」", $"「{Trim(text)}」不含「{needle}」");
    }

    public static void NotContains(string? text, string needle, string? what = null)
    {
        if (text == null || !text.Contains(needle, StringComparison.Ordinal)) { Passed++; return; }
        Fail(what ?? $"不该包含「{needle}」", $"「{Trim(text)}」不该含「{needle}」");
    }

    /// <summary>断言抛指定类型异常，返回它（便于继续断言消息）。</summary>
    public static T Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            Passed++;
            return ex;
        }
        catch (Exception ex)
        {
            Fail(what, $"期望抛 {typeof(T).Name}，实际抛 {ex.GetType().Name}：{ex.Message}");
            return null!;
        }

        Fail(what, $"期望抛 {typeof(T).Name}，实际没有抛异常");
        return null!;
    }

    /// <summary>记一条提示（不改变通过 / 失败计数）。</summary>
    public static void Note(string text) => Notes.Add(text);

    public static void Fail(string what, string detail)
    {
        Failed++;
        Failures.Add($"{(Current.Length == 0 ? "" : Current + " · ")}{what} — {detail}");
    }

    private static string Trim(string? text)
    {
        if (text == null) return "<null>";
        string flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
