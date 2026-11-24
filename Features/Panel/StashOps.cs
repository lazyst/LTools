using System;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Views;

namespace LTools.Features.Panel;

/// <summary>
/// 暂存区的四个写操作（打开草稿 / 试运行 / 采纳 / 删除，外加清空）。
/// </summary>
/// <remarks>
/// <para>
/// 面板搬家的过渡期（<c>docs/PROCESS_SPLIT.md</c> §14）里，暂存区页在普通权限宿主里也能看，
/// 但草稿目录的写入、编辑器、自动化试运行都只在提权进程做。为了不写两份逻辑，
/// 卡片背后的操作抽到这里：宿主经 <c>panel.op stash.*</c> 转给提权进程，提权侧再调同一份。
/// </para>
/// <para>
/// **本类不关面板**（谁调用谁负责）：面板是 Topmost，模态框要等它先关掉，否则会闪黑。
/// </para>
/// </remarks>
internal static class StashOps
{
    /// <summary>卡片上的名字：草稿内容里的名称优先，其次元数据标题。</summary>
    public static string DisplayName(DraftMeta meta, ActionProgram? program)
    {
        if (!string.IsNullOrWhiteSpace(program?.Name)) return program!.Name.Trim();
        if (!string.IsNullOrWhiteSpace(meta.Title)) return meta.Title!.Trim();
        return "未命名草稿";
    }

    /// <summary>在动作编辑器里改草稿；保存后写回 <c>staging/&lt;id&gt;/program.json</c>。</summary>
    public static void Open(string id)
    {
        ActionProgram program;
        try
        {
            var snapshot = DraftStore.Read(id);
            if (snapshot.Program == null)
            {
                ConfirmDialog.Info(null, "打开草稿",
                    snapshot.Error ?? "草稿内容不完整，先让 AI 重新写一遍。");
                return;
            }
            program = snapshot.Program;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(null, "打开草稿", ex.Message);
            return;
        }

        var draft = new ActionDto
        {
            Name = string.IsNullOrWhiteSpace(program.Name) ? "AI 草稿" : program.Name,
            Icon = string.IsNullOrWhiteSpace(program.Icon) ? IconCatalog.Default : program.Icon,
            Program = program.Clone(),
        };

        var result = ActionEditor.Show(null, "编辑草稿 · " + draft.Name, draft, DraftStore.FilesDir(id));
        if (result?.Program == null) return;

        try
        {
            DraftStore.WriteProgram(id, result.Program);
            DraftStore.Save(id, null, "在编辑器中修改");
            ToastService.Show("草稿已更新（AI 下次读取会拿到这一版）", result.Name, 2500, ToastKind.success);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(null, "保存草稿失败", ex.Message);
        }
    }

    /// <summary>试运行草稿（走 <see cref="AiRuns"/>，与 MCP 的试运行同一条路）。</summary>
    public static void Run(string id)
    {
        ActionProgram program;
        string revision;
        string filesDir;
        try
        {
            var snapshot = DraftStore.Read(id);
            if (snapshot.Program == null)
            {
                ConfirmDialog.Info(null, "试运行", snapshot.Error ?? "草稿内容不完整，无法试运行。");
                return;
            }
            program = snapshot.Program;
            revision = snapshot.Revision ?? "";
            filesDir = DraftStore.FilesDir(id);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(null, "试运行", ex.Message);
            return;
        }

        var validation = ActionValidator.Validate(program, revision.Length > 0 ? revision : null, filesDir,
            draftCheck: true);
        if (!validation.Ok && !ShowIssues(validation, "还不能试运行")) return;

        string name = string.IsNullOrWhiteSpace(program.Name) ? "AI 草稿" : program.Name;
        if (AiRuns.Start(id, name, program, null, filesDir, notify: true) == null)
        {
            ToastService.Show("已有自动化在运行，等它结束再试", "试运行", 3000, ToastKind.warning);
            return;
        }
        ToastService.Show("试运行中（停止热键可中止）", name, 3000, ToastKind.info);
    }

    /// <summary>采纳草稿：<paramref name="mode"/> = <c>new</c>（新动作）/ <c>replace</c>（覆盖来源动作）。</summary>
    public static void Promote(string id, string mode)
    {
        var meta = DraftStore.Find(id);
        if (meta == null) return;

        bool replace = string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase);
        string title = DisplayName(meta, null);

        if (replace)
        {
            // 采纳前先看清楚：草稿与来源动作的步骤级差异（同 StashPane 原来的确认框）。
            var baseAction = ActionRegistry.FindById(meta.BaseActionId ?? "");
            if (baseAction == null)
            {
                ConfirmDialog.Info(null, "覆盖原动作",
                    $"草稿的来源动作「{meta.BaseActionId}」已经不存在了，只能「采纳为新动作」。");
                return;
            }

            var draft = DraftStore.Read(id).Program;
            if (draft == null)
            {
                ConfirmDialog.Info(null, "覆盖原动作", "这份草稿的内容读不出来（program.json 缺失或损坏），先刷新暂存区再试。");
                return;
            }

            var diff = new ProgramDiffDialog(null,
                "覆盖原动作",
                $"用草稿「{title}」覆盖动作「{baseAction.Name}」：",
                baseAction.Program, draft, "覆盖");
            if (diff.ShowDialog() != true) return;
        }

        try
        {
            string? revision = DraftStore.Read(id).Revision;
            var result = DraftPromoter.Promote(id, replace ? "replace" : "new", revision);
            string toast = result.Created
                ? $"已采纳为新动作「{title}」"
                : $"已覆盖动作「{title}」";
            string hint = result.Created
                ? "已进动作库；要放上面板，在「动作管理」里拖进来。"
                : "";
            ToastService.Show(hint.Length > 0 ? toast + "。" + hint : toast, "暂存区", 3500, ToastKind.success);
        }
        catch (Exception ex)
        {
            // 采纳失败一律用常驻提示：新建时面板还开着，模态框会被 Topmost 面板挡住；按草稿 id 去重。
            ToastService.Show(ex.Message, "采纳失败", ToastService.LongMs, ToastKind.error, key: "stash:" + id);
        }
    }

    /// <summary>删除一份草稿（先确认）。</summary>
    public static void Delete(string id)
    {
        var meta = DraftStore.Find(id);
        if (meta == null) return;

        if (!AiUi.Confirm("删除草稿",
                $"删除草稿「{DisplayName(meta, null)}」？\n\n草稿目录（program.json / files / history）会一起删掉，" +
                "不可恢复；已经采纳出去的正式动作不受影响。",
                danger: true))
            return;

        try
        {
            DraftStore.Delete(id);
            ToastService.Show("草稿已删除", "暂存区", 2000, ToastKind.success);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(null, "删除草稿失败", ex.Message);
        }
    }

    /// <summary>清空暂存区（先确认）。</summary>
    public static void ClearAll()
    {
        var metas = DraftStore.List();
        if (metas.Count == 0)
        {
            ToastService.Show("暂存区已经是空的", "清空暂存区", 2000, ToastKind.info);
            return;
        }

        if (!AiUi.Confirm("清空暂存区",
                $"删除全部 {metas.Count} 份草稿？\n\n草稿目录会一起删掉，不可恢复；已经采纳出去的正式动作不受影响。",
                danger: true))
            return;

        int deleted = 0;
        foreach (var meta in metas)
        {
            try { DraftStore.Delete(meta.Id); deleted++; }
            catch (Exception ex) { AppLog.Warn("Stash", $"删除草稿 {meta.Id} 失败：{ex.Message}"); }
        }
        ToastService.Show($"已删除 {deleted} 份草稿", "清空暂存区", 2500, ToastKind.success);
    }

    /// <summary>校验没通过时列出前几条。返回 true = 用户选择「仍然继续」。</summary>
    private static bool ShowIssues(ActionValidationResult validation, string title, bool allowContinue = true)
    {
        string detail = string.Join("\n", validation.Issues
            .Where(i => i.Severity == IssueSeverity.error)
            .Take(6)
            .Select(i => $"· {i.Label}{i.Message}"));

        if (!allowContinue)
        {
            ConfirmDialog.Info(null, title, detail);
            return false;
        }

        return ConfirmDialog.Confirm(null, title, $"{detail}\n\n仍然继续？", okText: "仍然继续");
    }
}
