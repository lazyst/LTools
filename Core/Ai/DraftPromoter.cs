using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Migration;

namespace LTools.Core.Ai;

/// <summary>采纳结果。</summary>
internal sealed class PromoteResult
{
    public required string ActionId { get; init; }

    public required bool Created { get; init; }

    public IReadOnlyList<string> ImportedImages { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 草稿 → 正式动作（promote）。
/// </summary>
/// <remarks>
/// <para>
/// 这里复刻 Quicker 的三层结构：正式动作<b>只能</b>由用户确认后的采纳产生，MCP 没有直接写正式库的工具。
/// 写入统一走 <see cref="ConfigStore"/>（内部 Load → 改 → Save），避免和设置页并发保存互相覆盖。
/// </para>
/// <para>
/// 图片：草稿 <c>staging/&lt;id&gt;/files/</c> 整体复制进 <c>actions/&lt;actionId&gt;/files/</c>，
/// 步骤里的 <c>files/x.png</c> 原样保留——不再改写路径，也不再搬进共享的 <c>resources/images</c>。
/// 删除动作时整个目录进 <c>trash/</c>，图片跟着走，不需要按引用扫描孤儿。
/// </para>
/// </remarks>
internal static class DraftPromoter
{
    /// <summary>
    /// 采纳草稿。<paramref name="mode"/> 是 <c>new</c>（新建动作）或 <c>replace</c>（覆盖来源动作）。
    /// 静态校验有 error 时拒绝采纳。
    /// </summary>
    public static PromoteResult Promote(string draftId, string mode, string? expectedRevision)
    {
        draftId = draftId.Trim();
        var meta = DraftStore.Find(draftId) ?? throw new ActionException($"草稿不存在：{draftId}");
        var snapshot = DraftStore.Read(draftId);
        if (snapshot.Program == null)
            throw new ActionException(snapshot.Error ?? "草稿内容不完整，无法采纳");

        if (!string.IsNullOrWhiteSpace(expectedRevision) && snapshot.Revision != null &&
            !string.Equals(expectedRevision.Trim(), snapshot.Revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new DraftConflictException(
                $"草稿内容已被改动（当前 revision {snapshot.Revision[..12]}）。先重新读取，再采纳");
        }

        var validation = ActionValidator.Validate(snapshot.Program, snapshot.Revision, DraftStore.FilesDir(draftId),
            draftCheck: true);
        if (!validation.Ok)
        {
            string detail = string.Join("；", validation.Issues
                .Where(i => i.Severity == IssueSeverity.error)
                .Take(5)
                .Select(i => $"{i.Label}{i.Message}"));
            throw new ActionException($"静态校验没通过，先修好再采纳：{detail}");
        }

        var program = snapshot.Program.Clone();

        string title = string.IsNullOrWhiteSpace(program.Name) ? meta.Title ?? "AI 动作" : program.Name.Trim();
        bool replace = mode.Equals("replace", StringComparison.OrdinalIgnoreCase);
        string actionId;
        var imported = new List<string>();
        string draftFiles = DraftStore.FilesDir(draftId);

        if (replace)
        {
            string? baseId = meta.BaseActionId;
            if (string.IsNullOrWhiteSpace(baseId))
                throw new ActionException("这个草稿不是从正式动作复制的，只能「另存为新动作」");

            var existing = AiUi.Run(() => ActionRegistry.FindById(baseId))
                ?? throw new ActionException($"原动作已删除（{baseId}），请另存为新动作");

            if (string.IsNullOrWhiteSpace(meta.BaseSourceHash))
                throw new ActionException("草稿没有记录来源动作的内容哈希，请另存为新动作");

            string currentHash = DraftStore.HashProgram(ActionPrograms.For(existing));
            if (!string.Equals(currentHash, meta.BaseSourceHash, StringComparison.OrdinalIgnoreCase))
                throw new ActionException("原动作在你复制之后已被修改，覆盖会丢掉那些改动；请另存为新动作");

            AiUi.Run(() =>
            {
                var update = existing.Clone();
                update.Name = title;
                if (!string.IsNullOrWhiteSpace(program.Icon)) update.Icon = program.Icon;
                program.Id = update.Id;
                update.Program = program;
                update.ClearLegacy();
                // 先把草稿 files/ 搬进动作目录再落盘：复制失败会放弃采纳，不留「引用了不存在的图」的动作。
                imported.AddRange(ActionStore.CopyFilesFrom(ConfigStore.ConfigPath, update.Id, draftFiles));
                ConfigStore.UpdateAction(update);
                ConfigStore.Save();
            });

            actionId = existing.Id;
            AppLog.Info("Ai", $"采纳草稿 {draftId} → 覆盖动作 {actionId}「{title}」");
        }
        else
        {
            var created = AiUi.Run(() =>
            {
                var action = new ActionDto
                {
                    Name = title,
                    Icon = string.IsNullOrWhiteSpace(program.Icon) ? IconCatalog.Default : program.Icon,
                    Type = ActionType.composite,
                    Program = program,
                };
                if (!ConfigStore.AddAction(action)) throw new ActionException("新建动作失败（名称不能为空）");
                // 复制来源的草稿会把旧动作 Id 带在 program 里；生成新 Id 后对齐，避免动作体指向别的动作。
                program.Id = action.Id;
                action.Program = program;
                imported.AddRange(ActionStore.CopyFilesFrom(ConfigStore.ConfigPath, action.Id, draftFiles));
                ConfigStore.Save();
                return action;
            });

            actionId = created.Id;
            AppLog.Info("Ai", $"采纳草稿 {draftId} → 新动作 {actionId}「{title}」");
        }

        // 动作已经引用自己的 files/，这里回收不会误删它；顺手清掉不再被引用的共享模板。
        ImageLibrary.CollectGarbage();
        DraftStore.MarkPromoted(draftId, actionId);

        return new PromoteResult { ActionId = actionId, Created = !replace, ImportedImages = imported };
    }
}
