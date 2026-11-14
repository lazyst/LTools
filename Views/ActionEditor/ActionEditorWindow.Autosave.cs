using System;
using System.IO;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 编辑器的自动存盘（P3）：每 10 秒把没保存的内容写进 <c>cache/editor/</c>，
/// 打开动作时若它比正式文件新，问一句「恢复未保存的改动？」。
/// </summary>
/// <remarks>
/// <para>
/// 只有**已经有 Id** 的动作 / 子动作才自动存盘：新建的动作在保存前还没有 Id，
/// 拿不到稳定的文件名，与其猜一个不如不写（新建一条动作的内容本来也不多）。
/// </para>
/// <para>
/// 恢复是「临时内容」不是保存：恢复之后仍然是脏的，按「确定」才真正落盘；恢复本身记一笔撤销，
/// 所以后悔可以 Ctrl+Z 回到打开时看到的版本。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private const int AutosaveIntervalSeconds = 10;

    private DispatcherTimer? _autosaveTimer;

    /// <summary>自动存盘的文件名 Id；空 = 这个编辑器不自动存盘（新动作还没有 Id）。</summary>
    private string? _autosaveId;

    /// <summary>正式文件（<c>action.json</c> / <c>subactions/&lt;id&gt;.json</c>），用来判断自动存盘是不是过期了。</summary>
    private string _autosaveSource = "";

    /// <summary>构造末尾调：认 Id、记正式文件路径、起 10 秒计时器。</summary>
    private void SetupAutosave()
    {
        _autosaveId = _isSubAction ? _subDef?.Id : _draft?.Id;
        if (string.IsNullOrWhiteSpace(_autosaveId))
        {
            _autosaveId = null;
            return;
        }

        _autosaveSource = _isSubAction
            ? Path.Combine(SubActionStore.DirectoryPath, _autosaveId + ".json")
            : string.IsNullOrWhiteSpace(ActionStore.Anchor)
                ? ""
                : ActionStore.ActionPath(ActionStore.Anchor, _autosaveId);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutosaveIntervalSeconds) };
        _autosaveTimer.Tick += (_, _) => WriteAutosave();
        _autosaveTimer.Start();
    }

    /// <summary>计时器一跳：有改动才写（没改就什么都不做，别每次刷新文件时间）。</summary>
    private void WriteAutosave()
    {
        if (_autosaveId == null) return;
        if (_saved || !IsDirty()) return;

        EditorAutosave.Write(_autosaveId, CaptureProgram());
    }

    /// <summary>
    /// 编辑器当前内容的一份「能存就存」的快照：与 <see cref="Commit"/> 同源，
    /// 但**不做校验、不弹框**——自动存盘不该因为一个填错的默认值就整份丢掉。
    /// </summary>
    private ActionProgram CaptureProgram()
    {
        var program = new ActionProgram
        {
            Id = _program.Id,
            Name = (NameBox.Text ?? "").Trim(),
            Icon = IconPicker.SelectedIcon ?? "",
            Description = (DescriptionBox.Text ?? "").Trim(),
        };

        // tolerant = true 时不会返回 null（填错的默认值按原文存），这里只是让编译器也放心。
        foreach (var variable in ReadVariables(tolerant: true) ?? new List<VariableDef>())
            program.Variables.Add(variable);
        foreach (var step in _program.Steps) program.Steps.Add(step.Clone());

        return program;
    }

    /// <summary>载入完成后调（Loaded 之后、后台优先级）：过期就清掉，新就问一句要不要恢复。</summary>
    private void TryRestoreAutosave()
    {
        if (_autosaveId == null) return;

        string id = _autosaveId;
        if (!EditorAutosave.IsNewerThan(id, _autosaveSource))
        {
            // 正式文件比它新（在别处改过 / 已经保存过）：这份副本没有意义了。
            EditorAutosave.Delete(id);
            return;
        }

        ActionProgram? saved = EditorAutosave.TryRead(id);
        if (saved == null) return;

        string when = EditorAutosave.TimeText(id);
        bool restore = ConfirmDialog.Confirm(this, "恢复未保存的改动？",
            (when.Length == 0 ? "上次关闭编辑器时" : $"上次关闭编辑器时（{when}）")
            + "还有没保存的改动。要恢复到编辑器里吗？\n恢复后不会自动保存，按「确定」才落盘。",
            okText: "恢复");

        if (!restore)
        {
            EditorAutosave.Delete(id);
            return;
        }

        PushUndo("恢复自动存盘");
        ApplyProgram(saved);
        SetStatus("已恢复上次没保存的改动（按「确定」才保存）", hint: true);
    }

    /// <summary>把一份 <see cref="ActionProgram"/> 灌回编辑器（名称 / 图标 / 说明 / 变量 / 步骤）。</summary>
    private void ApplyProgram(ActionProgram program)
    {
        NameBox.Text = program.Name ?? "";
        IconPicker.SelectedIcon = string.IsNullOrWhiteSpace(program.Icon) ? IconCatalog.Default : program.Icon;
        DescriptionBox.Text = program.Description ?? "";

        _program.Variables.Clear();
        foreach (var variable in program.Variables) _program.Variables.Add(variable.Clone());
        BuildVariableRows();

        _program.Steps.Clear();
        foreach (var step in program.Steps) _program.Steps.Add(step.Clone());
        ReloadRows();
    }

    /// <summary>保存 / 放弃 / 关窗时收尾：停计时器、删掉这份自动存盘。保存成功时它已经没必要存在。</summary>
    private void ForgetAutosave()
    {
        _autosaveTimer?.Stop();
        if (_autosaveId != null) EditorAutosave.Delete(_autosaveId);
    }
}
