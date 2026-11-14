using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 枚举参数（<see cref="InputEditor.enumList"/>）：下拉只收字面量；值来自变量 / 取值时
/// 留空并交给冻结处理说明来源，「改用固定值」才落到第一项。
/// </summary>
internal sealed class EnumArgEditor : IArgEditor
{
    public EnumArgEditor(ArgEditorContext context)
    {
        InputSpec input = context.Spec;
        var combo = new ComboBox { MinHeight = 28, MinWidth = 180 };
        IReadOnlyList<string> options = input.Options ?? Array.Empty<string>();

        // 显示中文名，Tag 里留英文键——存进步骤的仍是键，老配置与动作单元实现不受影响。
        foreach (string option in options)
            combo.Items.Add(new ComboBoxItem
            {
                Content = OptionLabels.For(input, option),
                Tag = option,
            });

        StepArg? current = context.Current;
        string want = current?.Literal.AsText() ?? input.Default?.AsText() ?? "";
        int index = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (!string.Equals(options[i], want, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }

        // 老配置里可能有已下线的选项（内置命令改名 / 删掉）：把它原样留在下拉里并选中，
        // 否则会静默落到第一项，点一次确定就把配置改掉了。
        if (index < 0 && want.Length > 0)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = want + "（未知选项）",
                Tag = want,
            });
            index = combo.Items.Count - 1;
        }

        // 值来自变量 / 取值：下拉里的选项都不是它（以前 `index >= 0 ? index : 0`
        // 会兜底选中第一项，打开一次再确定就把引用改成了固定值）。
        // 这里留空、由冻结处理说明来源；用户点「改用固定值」才落到第一项。
        bool dynamicValue = current != null && current.Kind != StepArgKind.literal;
        combo.SelectedIndex = dynamicValue ? -1 : index >= 0 ? index : 0;

        combo.SelectionChanged += (_, _) => context.Edited();

        // 让别的行能按英文键改这一行的选中项（框选区域后自动切到「固定区域」）。
        context.RegisterEnum(key =>
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (!string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                combo.SelectedItem = item;
                break;
            }
        });

        FrozenEditor frozen = ArgFreeze.Decorate(context.Owner, current, combo,
            () => StepArg.FromText(((combo.SelectedItem as ComboBoxItem)?.Tag as string) ?? ""),
            context.Edited,
            () => { if (combo.SelectedIndex < 0) combo.SelectedIndex = 0; });
        Element = frozen.Element;
        _read = frozen.Read;
    }

    private readonly Func<StepArg?> _read;

    public FrameworkElement Element { get; }

    public StepArg? Read() => _read();

    public ValueField? Field => null;
}
