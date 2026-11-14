namespace LTools.Core.Actions;

/// <summary>
/// 变量类型的中文名（文本 / 数字 / 布尔 / 列表 / 对象）：参数表单的类型徽标、变量下拉的说明、
/// 参数框的标题、差异框与校验消息全用这一份。
/// </summary>
/// <remarks>
/// 以前参数表单、来源弹层、参数框、校验器、差异框各写一个 <c>switch</c>，同一个布尔在几处分别叫
/// 「布尔」和「开关」、默认值分别落成「文本」和「任意」，用户会以为那是两种东西，加一种类型还得改五处。
/// <b>别再就地写 <c>switch</c></b>：要给类型换个叫法只改这里。
/// </remarks>
internal static class ValueKindText
{
    /// <summary>类型名；<c>null</c> = 变量没声明类型，写「任意」。</summary>
    public static string Of(ValueKind? kind) => kind == null ? "任意" : Of(kind.Value);

    public static string Of(ValueKind kind) => kind switch
    {
        ValueKind.text => "文本",
        ValueKind.number => "数字",
        ValueKind.@bool => "布尔",
        ValueKind.list => "列表",
        ValueKind.@object => "对象",
        _ => "任意",
    };
}
