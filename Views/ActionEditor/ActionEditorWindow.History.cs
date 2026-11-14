using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 步骤树 + 变量表的撤销 / 重做。
/// </summary>
/// <remarks>
/// <para>
/// 快照 = 步骤树的深拷贝（<see cref="Step.Clone"/>）**加上变量表每行的原文**
/// （<see cref="EditorUndoState"/>）。只快照步骤时，「删除步骤」「插入模板」「拖动排序」
/// 撤销回来以后，变量表还停在改完之后的样子（多出来的声明 / 少掉的声明都得手工改）。
/// </para>
/// <para>
/// 每次结构性改动（增 / 删 / 移 / 粘贴 / 复制一份 / 编辑 / 跳过 / 新建与删除变量）之前记一笔，
/// 最多留 50 步。撤销 / 重做只重建列表，不动 <c>_program</c> 的引用（菜单、试运行、保存都指着它）。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private readonly List<EditorUndoState> _undo = new();
    private readonly List<EditorUndoState> _redo = new();
    private const int UndoLimit = 50;

    /// <summary>改动前记一笔；<paramref name="label"/> 会出现在「已撤销：…」提示里。</summary>
    private void PushUndo(string label)
    {
        _undo.Add(CaptureState(label));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>当前内容的一份撤销快照。</summary>
    private EditorUndoState CaptureState(string label) =>
        new(CloneSteps(_program.Steps), CaptureVariables(), label);

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            SetStatus("没有可撤销的操作", hint: true);
            return;
        }

        EditorUndoState state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(CaptureState(state.Label));
        ApplyState(state);
        SetStatus($"已撤销：{state.Label}", hint: true);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            SetStatus("没有可重做的操作", hint: true);
            return;
        }

        EditorUndoState state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(CaptureState(state.Label));
        ApplyState(state);
        SetStatus($"已重做：{state.Label}", hint: true);
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

    /// <summary>恢复一份快照：步骤树与变量表一起回到当时的样子。</summary>
    private void ApplyState(EditorUndoState state)
    {
        _program.Steps.Clear();
        foreach (Step step in state.Steps) _program.Steps.Add(step.Clone());
        RestoreVariables(state.Variables);
        ReloadRows();
    }
}
