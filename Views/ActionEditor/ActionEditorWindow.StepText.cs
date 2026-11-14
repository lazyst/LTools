using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 步骤的文本形式（DSL）出入口：右键「复制为文本 / 粘贴步骤（文本）」、<c>Ctrl+Shift+C / V</c>。
/// </summary>
/// <remarks>
/// 格式与读写全在 <see cref="StepText"/>；这里只负责三件小事：取哪几步（选中的最外层步骤 /
/// 整份动作）、落到哪儿（与 Ctrl+V 同一套落点）、Undo 与选中。**别再另写一套格式**——
/// 想改语法就改 <see cref="StepText"/>，冒烟的往返断言守在那里。
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>要写成文本的步骤：选中的最外层步骤；没选中就是整份动作（方便整段贴给 AI）。</summary>
    internal List<Step> StepsForText()
    {
        List<StepRow> rows = SelectedRows();
        if (rows.Count == 0) return _program.Steps.ToList();
        return OutermostSelectedRows().Select(r => r.Step!).ToList();
    }

    private void CopyStepsAsText()
    {
        List<Step> steps = StepsForText();
        if (steps.Count == 0)
        {
            SetStatus("没有可复制的步骤", hint: true);
            return;
        }

        try
        {
            Clipboard.SetText(StepText.Write(steps));
            SetStatus($"已复制 {steps.Count} 步的文本形式", hint: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "复制为文本", "复制失败：" + ex.Message);
        }
    }

    /// <summary>剪贴板里有没有可粘贴的文本（右键菜单的启用状态用它，拿不到就当没有）。</summary>
    private static bool ClipboardHasText()
    {
        try { return Clipboard.ContainsText(); }
        catch { return false; }
    }

    private void PasteStepsFromText()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "粘贴步骤（文本）", "读剪贴板失败：" + ex.Message);
            return;
        }

        if (!StepText.TryParse(text, out List<Step> steps, out string? error))
        {
            ConfirmDialog.Info(this, "粘贴步骤（文本）",
                (error ?? "这段文本不是步骤") +
                "\n\n格式：一行一步，形如 text.send text=\"你好\"；缩进表示子步骤，else 写在自己那一行。");
            return;
        }

        var (list, index) = StepInsertTarget();
        InsertSteps(steps, list, index, "粘贴步骤（文本）");
    }

    /// <summary>粘贴文本步骤的落点：与 Ctrl+V 完全一致（选中行之后 / 空分支处 / 末尾）。</summary>
    private (List<Step> List, int Index) StepInsertTarget()
    {
        StepRow? row = SelectedRow();
        if (row?.Owner == null) return (_program.Steps, _program.Steps.Count);

        int index = row.Step != null ? row.Index + 1 : row.Index;
        return (row.Owner, Math.Clamp(index, 0, row.Owner.Count));
    }
}
