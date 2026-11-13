using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 空动作的起步模板：中间那块「从模板开始」，点一下插几步，之后照常改参数。
/// </summary>
/// <remarks>
/// <para>
/// 模板本体在 <see cref="ActionTemplates"/>（纯数据，能跑），这里只管把它们画成按钮、
/// 点完把步骤 + 变量声明塞进当前动作。**没有第二套模板机制**：插进来的就是普通步骤，
/// 会进撤销栈、能被拖动 / 删除 / 试运行，和手写的没有任何区别。
/// </para>
/// <para>
/// 只在「一步都没有」时显示（<see cref="ReloadRows"/> 里判），有步骤了就收起来，
/// 免得占着中间那块地方；想再插模板可以先把步骤删光，或者直接用左边的工具箱。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>按当前注册表里的模板重建按钮（模板是固定几条，重建很便宜）。</summary>
    private void BuildTemplatePanel()
    {
        TemplateList.Children.Clear();

        foreach (ActionTemplate template in ActionTemplates.All)
        {
            var content = new StackPanel { Orientation = Orientation.Vertical };
            content.Children.Add(new TextBlock
            {
                Text = template.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = (double)FindResource("FontSizeSecondary"),
                Foreground = (Brush)FindResource("TextBrush"),
            });
            content.Children.Add(new TextBlock
            {
                Text = template.Hint,
                FontSize = (double)FindResource("FontSizeHint"),
                Foreground = (Brush)FindResource("HintTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });

            var button = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = "插到动作末尾（可以撤销）",
            };
            button.Click += (_, _) => ApplyTemplate(template);
            TemplateList.Children.Add(button);
        }
    }

    /// <summary>
    /// 把一个模板追加到动作末尾：先补变量声明（已有的同名变量不重复加），再插步骤。
    /// 插完选中第一步，用户接着改参数就是。
    /// </summary>
    private void ApplyTemplate(ActionTemplate template)
    {
        PushUndo($"插入模板「{template.Name}」");

        var existing = new HashSet<string>(
            _variables.Select(v => v.Name.Text.Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (VariableDef def in template.Variables())
        {
            if (!existing.Add(def.Key)) continue;
            AddVariableRow(def.Key, def.Type, def.Default, def.Direction);
        }

        List<Step> steps = template.Build().ToList();
        if (steps.Count == 0) return;

        foreach (Step step in steps)
        {
            _program.Steps.Add(step);
            NoteUnitUsed(step.Unit);
        }

        ReloadRows(steps[0]);
    }
}
