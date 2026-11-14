using System;
using System.Windows;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 值字段（文本 / 多行 / 数字 / 文件 / 文件夹 / 图片 / 区域 / 按键 / 宏 / 子动作 / 变量名…）：
/// 唯一能表达三态的控件——输入框永远在，来源收进右侧「▾」。
/// </summary>
internal sealed class ValueArgEditor : IArgEditor
{
    public ValueArgEditor(ArgEditorContext context)
    {
        var field = new ValueField
        {
            Spec = context.Spec,
            Type = context.Spec.Type,
            VariableNameMode = context.Spec.Editor == InputEditor.variable,
            // 列表参数按「每行一项」写，单行框看不清也容易误删；
            // 绑定参数时仍由引擎按行 / 逗号拆成列表。
            Multiline = context.Spec.Editor == InputEditor.multiline
                        || (context.Spec.Editor == InputEditor.text && context.Spec.Type == ValueKind.list),
            Program = context.Program,
            VariableNameSource = context.VariableNames,
            VariableInfoSource = context.VariableInfos,
            ResourceRoot = context.ResourceRoot,
        };
        field.Value = context.Current ?? StepArg.FromText("");
        field.Changed += context.Edited;
        if (context.Captured != null) field.Captured += context.Captured;
        if (context.PointPicked != null) field.PointPicked += context.PointPicked;

        // 候选型参数（按键 / 宏 / 子动作）：候选随用户数据变化，收进 ▾ 的「从列表选…」，
        // 行内不再摆下拉。
        if (context.Choices != null) field.ChoiceSource = context.Choices;

        context.RegisterField(text => field.LiteralText = text);
        if (context.Spec.Editor == InputEditor.region)
            context.RegisterRect(text => field.LiteralText = text);

        _field = field;
    }

    private readonly ValueField _field;

    public FrameworkElement Element => _field;

    public StepArg? Read() => _field.Value;

    public ValueField? Field => _field;

    public void MarkMissing(bool on) => _field.MarkMissing(on);
}
