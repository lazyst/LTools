using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 步骤树的撤销 / 重做。
/// </summary>
/// <remarks>
/// <para>
/// 快照 = 步骤树的深拷贝（<see cref="Step.Clone"/>）；变量表不在快照里——它有自己的编辑入口，
/// 撤销一次步骤不该把用户刚改的变量默认值一起吞掉。每次结构性改动（增 / 删 / 移 / 粘贴 /
/// 复制一份 / 编辑 / 跳过）之前记一笔，最多留 50 步。
/// </para>
/// <para>
/// 撤销 / 重做只重建列表，不动 <c>_program</c> 的引用（菜单、试运行、保存都指着它）。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private readonly List<(List<Step> Steps, string Label)> _undo = new();
    private readonly List<(List<Step> Steps, string Label)> _redo = new();
    private const int UndoLimit = 50;

    /// <summary>改动前记一笔；<paramref name="label"/> 会出现在「已撤销：…」提示里。</summary>
    private void PushUndo(string label)
    {
        _undo.Add((CloneSteps(_program.Steps), label));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            SetStatus("没有可撤销的操作", hint: true);
            return;
        }

        var (steps, label) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((CloneSteps(_program.Steps), label));
        ApplySteps(steps);
        SetStatus($"已撤销：{label}", hint: true);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            SetStatus("没有可重做的操作", hint: true);
            return;
        }

        var (steps, label) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((CloneSteps(_program.Steps), label));
        ApplySteps(steps);
        SetStatus($"已重做：{label}", hint: true);
    }

    private void Undo_Click(object sender, System.Windows.RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, System.Windows.RoutedEventArgs e) => Redo();

    /// <summary>窗口级 Ctrl+Z / Ctrl+Y；焦点在文本框里时让给文本框自己的撤销。</summary>
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
    }

    private static List<Step> CloneSteps(List<Step> steps)
    {
        var copy = new List<Step>(steps.Count);
        foreach (Step step in steps) copy.Add(step.Clone());
        return copy;
    }

    private void ApplySteps(List<Step> steps)
    {
        _program.Steps.Clear();
        foreach (Step step in steps) _program.Steps.Add(step.Clone());
        ReloadRows();
    }
}
