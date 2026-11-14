using System;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 按 <see cref="InputSpec.Editor"/> 分发控件形态：每种形态在
/// <see cref="ArgEditor"/>.cs 旁边各有一个 <see cref="IArgEditor"/> 实现，
/// 这里只负责装配上下文（声明 / 当前值 / 反填路径）并挑一个实现。
/// </summary>
public partial class StepFormView
{
    private IArgEditor BuildEditor(InputSpec input)
    {
        var context = new ArgEditorContext
        {
            Spec = input,
            Current = CurrentArg(input),
            Owner = this,
            Edited = OnEdited,
            RegisterEnum = setter => { _enumSetters[input.Key] = setter; },
            RegisterField = setter => { _fieldSetters[input.Key] = setter; },
            RegisterRect = setter => { _rectSetters[input.Key] = setter; },
            Choices = ChoiceSourceFor(input),
            Program = Program,
            VariableNames = VariableNameSource,
            VariableInfos = VariableInfoSource,
            ResourceRoot = ResourceRoot,
            Captured = ApplyPickedRegion,
            PointPicked = (x, y) => ApplyPickedPoint(input, x, y),
        };

        // 不认识的形态一律落到值字段——它是唯一能表达三态、也唯一带「该填什么」状态行的控件。
        return input.Editor switch
        {
            InputEditor.boolean => new BooleanArgEditor(context),
            InputEditor.enumList => new EnumArgEditor(context),
            InputEditor.flags => new FlagsArgEditor(context),
            InputEditor.code => new CodeArgEditor(context),
            _ => new ValueArgEditor(context),
        };
    }
}
