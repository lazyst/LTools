using System;

namespace LTools.Core.Actions;

/// <summary>
/// 参数输入框右侧「▾ 来源」里能选的取值方式。
/// </summary>
/// <remarks>
/// 只声明「这个参数可以怎么填」，具体界面由 <c>Views/ActionEditor/ValueField</c> 实现：
/// 变量、选择文件 / 图片 / 窗口 / 进程、屏幕取点、录制按键…都从同一个入口进。
/// 没写 <see cref="InputSpec.Pickers"/> 时按 <see cref="InputEditor"/> 推一个合理默认
/// （见 <see cref="PickerDefaults.For"/>），单元作者只在默认不够时补一条。
/// </remarks>
[Flags]
public enum PickerKind
{
    none = 0,

    /// <summary>插入当前动作的变量（文本里插 <c>${名}</c>，变量模式直接填名）。</summary>
    variable = 1 << 0,

    file = 1 << 1,
    folder = 1 << 2,
    image = 1 << 3,

    /// <summary>屏幕区域 <c>x,y,w,h</c>（框选）。</summary>
    region = 1 << 4,

    /// <summary>从当前打开的窗口里挑一个，填标题（按包含匹配）。</summary>
    window = 1 << 5,

    /// <summary>从当前打开的窗口里挑一个，填类名（精确匹配）。</summary>
    windowClass = 1 << 6,

    /// <summary>从正在运行的进程里挑一个，填进程名。</summary>
    process = 1 << 7,

    /// <summary>在屏幕上单击取点；一次填好同组的 <c>x</c> / <c>y</c>。</summary>
    point = 1 << 8,

    /// <summary>在屏幕上单击取色，填 <c>#RRGGBB</c>。</summary>
    color = 1 << 9,

    /// <summary>录一段组合键，追加一行 <c>ctrl+s</c>（「模拟按键」的写法）。</summary>
    keys = 1 << 10,

    /// <summary>速记分类：列出现有分类目录。</summary>
    noteCategory = 1 << 11,

    /// <summary>时间格式：常用格式一键填。</summary>
    timeFormat = 1 << 12,

    /// <summary>文件选择框的筛选器：常用组合一键填。</summary>
    fileFilter = 1 << 13,

    /// <summary>列表分隔符：逗号 / 分号 / 换行…</summary>
    separator = 1 << 14,

    /// <summary>剪贴板历史里的一条记录（填它的 Id）。</summary>
    clipboardRecord = 1 << 15,

    /// <summary>内联取值单元（读剪贴板 / 截图 / 找图…），运行到这里时现场取一个值。</summary>
    inline = 1 << 16,

    /// <summary>清空当前值。</summary>
    clear = 1 << 17,
}

/// <summary>没声明 <see cref="InputSpec.Pickers"/> 时，按编辑器形态推出的可用来源。</summary>
internal static class PickerDefaults
{
    public static PickerKind For(InputSpec input)
    {
        // 代码原样传入（不解析 ${变量}），没有「来源」这回事。
        if (input.Raw || input.Editor == InputEditor.code) return PickerKind.none;

        PickerKind kind = input.Editor switch
        {
            InputEditor.enumList or InputEditor.boolean or InputEditor.macro => PickerKind.none,
            InputEditor.key or InputEditor.flags => PickerKind.none,

            // 「变量名」输入框：名字本身是数据，不能再嵌变量。
            InputEditor.variable when input.Type == ValueKind.text => PickerKind.none,
            InputEditor.variable => PickerKind.variable,

            InputEditor.file => PickerKind.variable | PickerKind.file | PickerKind.inline,
            InputEditor.folder => PickerKind.variable | PickerKind.folder | PickerKind.inline,
            InputEditor.image => PickerKind.variable | PickerKind.image | PickerKind.inline,
            InputEditor.region => PickerKind.variable | PickerKind.region | PickerKind.inline,

            _ => PickerKind.variable | PickerKind.inline,
        };

        return kind == PickerKind.none ? kind : kind | PickerKind.clear;
    }
}
