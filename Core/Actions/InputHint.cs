namespace LTools.Core.Actions;

/// <summary>
/// 参数输入框下方的「该填什么」说明。
/// </summary>
/// <remarks>
/// <para>
/// 动作单元声明里写了 <see cref="InputSpec.Description"/> 就用它；没写时按 <see cref="InputSpec.Type"/> /
/// <see cref="InputSpec.Editor"/> 给一句默认说明——这样每个输入框旁边都有类型提示，而动作单元作者
/// 只需要为「默认说法不够」的参数补一句（单位、格式、可填哪些值）。
/// </para>
/// <para>
/// 下拉（<see cref="InputEditor.enumList"/>）与开关（<see cref="InputEditor.boolean"/>）本身已经
/// 说明了能选什么，默认不加说明，避免每行都挂一句废话；要补特殊说明时照常写 Description。
/// </para>
/// </remarks>
internal static class InputHint
{
    /// <summary>给参数生成一行说明；没有可说的返回空串。</summary>
    public static string Of(InputSpec input)
    {
        if (!string.IsNullOrWhiteSpace(input.Description)) return input.Description!.Trim();

        return input.Editor switch
        {
            InputEditor.enumList or InputEditor.boolean => "",
            InputEditor.key or InputEditor.flags => "",
            InputEditor.multiline => "多行文本；可用 ${变量} 引用变量",
            InputEditor.code => "代码原样传入，不解析 ${变量}",
            InputEditor.number => "数字（整数或小数）；可用 ${变量}",
            InputEditor.file => "文件路径；点右侧 ▾ 选文件，或写 ${变量}",
            InputEditor.folder => "文件夹路径；点右侧 ▾ 选文件夹，或写 ${变量}",
            InputEditor.image => "图片文件路径（png / jpg）；点右侧 ▾ 框选 / 换图，或写 ${变量}",
            InputEditor.macro => "选择一个已录制的宏（在「设置 → 键鼠宏」里录制）",
            InputEditor.region => "屏幕区域 x,y,w,h；点右侧 ▾ 框选一块",
            InputEditor.variable => "变量名（只填名字，不用 ${}）",
            InputEditor.condition => "写文本、用 ${变量}，或点右侧 ▾ 换成变量 / 取值动作单元",
            _ => input.Type switch
            {
                ValueKind.number => "数字；可用 ${变量}",
                ValueKind.@bool => "真 / 假；可用 ${变量}",
                ValueKind.list => "列表；每行一项或逗号分隔，也可写 JSON 数组",
                ValueKind.@object => "对象（JSON）；可用 ${变量} 取其中一个成员",
                _ => "文本；可用 ${变量} 引用变量",
            },
        };
    }
}
