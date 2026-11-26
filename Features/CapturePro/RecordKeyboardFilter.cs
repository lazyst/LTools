namespace LTools.Features.CapturePro;

/// <summary>
/// 按键浮层收哪些键（一比一移植 Snow Shot 的
/// <c>snow-crates/crates/snow-recording-effects/src/keyboard_filter.rs</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 三个分类各一个开关（数字 / 字母与符号 / 控制键），再加一个「保留快捷键」：按住
/// Ctrl / Alt / Win（含左右键与 AltGr 之外的组合）时，本来被分类排除的键也照样显示；
/// 关掉「控制键」时，浮层里**不再带修饰键前缀**（只留最终那个键）。
/// </para>
/// <para>
/// 纯函数、只认 Windows 虚拟键码，<c>--selftest</c> 直接断言上游那几条用例。
/// </para>
/// </remarks>
internal static class RecordKeyboardFilter
{
    internal enum Category
    {
        Number,
        Text,
        Control,
    }

    /// <summary>虚拟键码 → 分类（0x30–0x39 数字、0x60–0x69 小键盘数字算数字）。</summary>
    internal static Category Of(int vk)
    {
        if (vk is >= 0x30 and <= 0x39) return Category.Number;
        if (vk is >= 0x60 and <= 0x69) return Category.Number;

        if (vk == 0x20) return Category.Text;
        if (vk is >= 0x41 and <= 0x5A) return Category.Text;
        if (vk is >= 0x6A and <= 0x6F) return Category.Text;
        if (vk is >= 0xBA and <= 0xC0) return Category.Text;
        if (vk is >= 0xDB and <= 0xDF) return Category.Text;
        if (vk == 0xE2) return Category.Text;

        return Category.Control;
    }

    /// <summary>按开关判断这个键要不要出现在浮层里。</summary>
    internal static bool Includes(RecordOptions options, int vk) => Of(vk) switch
    {
        Category.Number => options.RecordNumberKeys,
        Category.Text => options.RecordLetterKeys,
        _ => options.RecordControlKeys,
    };

    /// <summary>修饰键本身（浮层里从不单独显示，只挂在别的键前面）。</summary>
    internal static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>
    /// 按下这个键要不要收：分类开关命中就收；否则在「保留快捷键」开着、
    /// 并且此刻按着某个快捷键修饰键时也收。
    /// </summary>
    internal static bool Accept(RecordOptions options, int vk, bool shortcutHeld) =>
        Includes(options, vk)
        || (options.RecordControlKeys && options.PreserveShortcuts && shortcutHeld);
}
