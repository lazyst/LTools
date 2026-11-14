using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>多选参数（<see cref="InputEditor.flags"/>）：一排勾选框，存成 <c>a+b</c>。</summary>
internal sealed class FlagsArgEditor : IArgEditor
{
    public FlagsArgEditor(ArgEditorContext context)
    {
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var boxes = new List<(string Key, CheckBox Box)>();
        var selected = new HashSet<string>(
            (context.Current?.Literal.AsText() ?? "")
                .Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (string option in context.Spec.Options ?? Array.Empty<string>())
        {
            var check = new CheckBox
            {
                Content = OptionLabels.For(context.Spec, option),
                IsChecked = selected.Contains(option),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked += (_, _) => context.Edited();
            check.Unchecked += (_, _) => context.Edited();
            boxes.Add((option, check));
            wrap.Children.Add(check);
        }

        FrozenEditor frozen = ArgFreeze.Decorate(context.Owner, context.Current, wrap,
            () => StepArg.FromText(string.Join("+",
                boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Key))),
            context.Edited);
        Element = frozen.Element;
        _read = frozen.Read;
    }

    private readonly Func<StepArg?> _read;

    public FrameworkElement Element { get; }

    public StepArg? Read() => _read();

    public ValueField? Field => null;
}
