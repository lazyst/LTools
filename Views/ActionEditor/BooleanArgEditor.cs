using System;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 开关参数（<see cref="InputEditor.boolean"/>）：一个勾选框，标签列已经写了参数名，
/// 不再挂「启用」之类的副标题。
/// </summary>
internal sealed class BooleanArgEditor : IArgEditor
{
    public BooleanArgEditor(ArgEditorContext context)
    {
        var check = new CheckBox
        {
            // 没存过的开关按参数默认值勾（以前 `current == null` 就勾上，会把
            // 没有 Default 的开关（如「递归」）白白存成 true）。
            IsChecked = context.Current?.Literal.AsBool() ?? false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.Checked += (_, _) => context.Edited();
        check.Unchecked += (_, _) => context.Edited();

        FrozenEditor frozen = ArgFreeze.Decorate(context.Owner, context.Current, check,
            () => StepArg.FromBool(check.IsChecked == true), context.Edited);
        Element = frozen.Element;
        _read = frozen.Read;
    }

    private readonly Func<StepArg?> _read;

    public FrameworkElement Element { get; }

    public StepArg? Read() => _read();

    public ValueField? Field => null;
}
