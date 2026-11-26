using System.Text.Json;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Native;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具的实际实现。所有工具共用一个回执形状与审计入口，避免每个工具各写一份 try/catch。
/// </summary>
internal static partial class AiToolHandlers
{
    // ============ 草稿 ============

    public static Dictionary<string, object?> StagingCreate(string title, string? description, string? copyFromActionId)
    {
        RequireWrites();
        var meta = AiUi.Run(() => DraftStore.Create(title, description, copyFromActionId, origin: "mcp"));
        return new Dictionary<string, object?>
        {
            ["draftId"] = meta.Id,
            ["title"] = meta.Title,
            ["revision"] = DraftStore.ComputeRevision(meta.Id),
            ["directory"] = Path.GetFullPath(DraftStore.DirectoryOf(meta.Id)),
            ["editFile"] = DraftStore.ProgramPath(meta.Id),
            ["filesDir"] = Path.GetFullPath(DraftStore.FilesDir(meta.Id)),
            ["baseActionId"] = meta.BaseActionId,
        };
    }

    public static Dictionary<string, object?> StagingOpen(string draftId)
    {
        var snapshot = DraftStore.Read(draftId);
        return new Dictionary<string, object?>
        {
            ["draftId"] = snapshot.Meta.Id,
            ["title"] = snapshot.Meta.Title,
            ["status"] = snapshot.Meta.Status.ToString(),
            ["revision"] = snapshot.Revision,
            ["savedRevision"] = snapshot.Meta.SavedRevision,
            ["editVersion"] = snapshot.Meta.EditVersion,
            ["directory"] = snapshot.Directory,
            ["programPath"] = DraftStore.ProgramPath(snapshot.Meta.Id),
            ["filesDir"] = Path.GetFullPath(DraftStore.FilesDir(snapshot.Meta.Id)),
            ["baseActionId"] = snapshot.Meta.BaseActionId,
            ["promotedActionId"] = snapshot.Meta.PromotedActionId,
            ["summary"] = snapshot.Meta.Summary,
            ["files"] = snapshot.Files.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["bytes"] = f.Bytes,
            }).ToList(),
            ["program"] = snapshot.Program,
            ["error"] = snapshot.Error,
        };
    }

    public static Dictionary<string, object?> StagingWrite(string draftId, JsonElement program, string? expectedRevision)
    {
        RequireWrites();
        ActionProgram parsed = ParseProgram(program);
        string revision = DraftStore.WriteProgram(draftId, parsed, expectedRevision);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["revision"] = revision,
            ["name"] = parsed.Name,
            ["steps"] = parsed.Steps.Count,
        };
    }

    public static Dictionary<string, object?> StagingReadFile(string draftId, string path)
    {
        byte[] bytes = DraftStore.ReadFile(draftId, path);
        if (bytes.Length > 16 * 1024 * 1024)
            throw new AiToolException("too_large", $"文件太大（{bytes.Length} 字节），不要通过 MCP 读它");

        return new Dictionary<string, object?>
        {
            ["name"] = path,
            ["bytes"] = bytes.Length,
            ["base64"] = Convert.ToBase64String(bytes),
        };
    }

    public static Dictionary<string, object?> StagingWriteFile(string draftId, string path, string contentBase64)
    {
        RequireWrites();
        byte[] bytes;
        try { bytes = Convert.FromBase64String((contentBase64 ?? "").Trim()); }
        catch (FormatException) { throw new AiToolException("invalid_base64", "contentBase64 不是合法的 base64"); }

        if (bytes.Length > 8 * 1024 * 1024)
            throw new AiToolException("too_large", $"文件太大（{bytes.Length} 字节），单文件上限 8 MB");

        string full = DraftStore.WriteFile(draftId, path, bytes);
        return new Dictionary<string, object?>
        {
            ["name"] = path,
            ["bytes"] = bytes.Length,
            ["path"] = full,
            ["revision"] = DraftStore.ComputeRevision(draftId),
        };
    }

    public static Dictionary<string, object?> StagingDeleteFile(string draftId, string path)
    {
        RequireWrites();
        bool deleted = DraftStore.DeleteFile(draftId, path);
        return new Dictionary<string, object?>
        {
            ["deleted"] = deleted,
            ["revision"] = DraftStore.ComputeRevision(draftId),
        };
    }

    public static Dictionary<string, object?> StagingCheck(string draftId)
    {
        var snapshot = DraftStore.Read(draftId);
        var result = ActionValidator.Validate(snapshot.Program, snapshot.Revision, DraftStore.FilesDir(draftId),
        draftCheck: true);

        return CheckPayload(result, result.Ok && snapshot.Program != null, snapshot.Error);
    }

    public static Dictionary<string, object?> StagingSave(string draftId, string expectedRevision, string? summary)
    {
        RequireWrites();
        var meta = DraftStore.Save(draftId, expectedRevision, summary);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
            ["status"] = meta.Status.ToString(),
        };
    }

    public static Dictionary<string, object?> StagingReset(string draftId)
    {
        RequireWrites();
        var meta = DraftStore.Reset(draftId);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["revision"] = DraftStore.ComputeRevision(draftId),
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
        };
    }

    public static Dictionary<string, object?> StagingDelete(string draftId)
    {
        RequireWrites();
        var meta = DraftStore.Find(draftId) ?? throw new AiToolException("not_found", $"草稿不存在：{draftId}");
        RequireConfirm("删除草稿", $"确定删除草稿「{meta.Title ?? meta.Id}」（{meta.Id}）？此操作不可恢复。", danger: true);
        DraftStore.Delete(draftId);
        return new Dictionary<string, object?> { ["deleted"] = true, ["draftId"] = draftId };
    }

    public static Dictionary<string, object?> StagingRun(string draftId, string? startPath,
        string? expectedRevision, bool dryRun = false)
    {
        var snapshot = DraftStore.Read(draftId);
        if (snapshot.Program == null)
            throw new AiToolException("invalid_program", snapshot.Error ?? "草稿内容不完整，无法试运行");

        if (!string.IsNullOrWhiteSpace(expectedRevision) && snapshot.Revision != null &&
            !string.Equals(expectedRevision.Trim(), snapshot.Revision, StringComparison.OrdinalIgnoreCase))
            throw new DraftConflictException("草稿内容已被改动（期望的 revision 与当前不一致），先重新读取");

        var validation = ActionValidator.Validate(snapshot.Program, snapshot.Revision, DraftStore.FilesDir(draftId),
        draftCheck: true);
        if (!validation.Ok)
        {
            var errors = validation.Issues.Where(i => i.Severity == IssueSeverity.error).Take(5).ToList();
            string detail = string.Join("；", errors.Select(i => $"{i.Label}{i.Message}"));
            throw new AiToolException("check_failed", $"静态校验有 {validation.Errors} 个 error，先修好再试运行：{detail}");
        }

        string name = string.IsNullOrWhiteSpace(snapshot.Program.Name)
            ? snapshot.Meta.Title ?? "AI 草稿"
            : snapshot.Program.Name;

        // 草稿里的图写在 files/x.png：把草稿目录当资源根，试运行就不用写绝对路径。
        var run = AiRuns.Start(draftId, name, snapshot.Program.Clone(), startPath,
            DraftStore.FilesDir(draftId), notify: false, dryRun: dryRun);
        if (run == null)
            throw new AiToolException("busy", "已有自动化正在运行，等它结束再试（不排队，避免两套键鼠注入打架）");

        return new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["draftId"] = draftId,
            ["state"] = run.State,
            ["revision"] = snapshot.Revision,
            ["dryRun"] = run.DryRun,
        };
    }

    public static Dictionary<string, object?> RunOutput(string runId, int cursor, int limit)
    {
        var (run, items, next, hasMore) = AiRuns.Read(runId, cursor, limit);
        if (run == null) throw new AiToolException("not_found", $"试运行记录不存在：{runId}");

        return new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["draftId"] = run.DraftId,
            ["name"] = run.Name,
            ["state"] = run.State,
            ["finished"] = run.Finished,
            ["dryRun"] = run.DryRun,
            ["steps"] = run.Steps,
            ["skipped"] = run.Skipped,
            ["elapsedMs"] = Math.Round(run.ElapsedMs),
            ["error"] = run.Error,
            ["traces"] = items.Select(item => new Dictionary<string, object?>
            {
                ["seq"] = item.Seq,
                ["path"] = item.Trace.Path,
                ["label"] = item.Trace.Label,
                ["depth"] = item.Trace.Depth,
                ["unit"] = item.Trace.UnitKey,
                ["unitName"] = item.Trace.UnitName,
                ["status"] = item.Trace.Status.ToString(),
                ["elapsedMs"] = item.Trace.ElapsedMs,
                ["error"] = item.Trace.Error,
                ["inputs"] = item.Trace.Inputs,
                ["outputs"] = item.Trace.Outputs,
            }).ToList(),
            ["next"] = next,
            ["hasMore"] = hasMore,
        };
    }

    public static Dictionary<string, object?> RunStop(string runId)
    {
        bool stopped = AiRuns.Stop(runId);
        var run = AiRuns.Find(runId) ?? throw new AiToolException("not_found", $"试运行记录不存在：{runId}");
        return new Dictionary<string, object?>
        {
            ["stopped"] = stopped,
            ["state"] = run.State,
        };
    }

    public static Dictionary<string, object?> StagingPromote(string draftId, string mode, string? expectedRevision)
    {
        RequireWrites();
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("new" or "replace"))
            throw new AiToolException("invalid_mode", "mode 只能是 new 或 replace");

        var meta = DraftStore.Find(draftId) ?? throw new AiToolException("not_found", $"草稿不存在：{draftId}");
        RequireConfirm(
            mode == "replace" ? "覆盖正式动作" : "采纳为新动作",
            $"把草稿「{meta.Title ?? meta.Id}」（{meta.Id}）{(mode == "replace" ? "覆盖来源正式动作" : "采纳为一个新的正式动作")}？",
            okText: mode == "replace" ? "覆盖动作" : "采纳");

        var result = DraftPromoter.Promote(draftId, mode, expectedRevision);
        return new Dictionary<string, object?>
        {
            ["actionId"] = result.ActionId,
            ["created"] = result.Created,
            ["importedImages"] = result.ImportedImages,
            ["draftId"] = draftId,
        };
    }

}
