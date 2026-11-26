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
    // ============ 子动作写入（AI） ============

    /// <summary>
    /// 只校验不落盘：AI 写完先跑一遍，拿到和编辑器「校验」同一套问题清单。
    /// </summary>
    public static Dictionary<string, object?> SubActionsCheck(JsonElement program)
    {
        var def = ParseSubAction(program);
        var result = ActionValidator.ValidateSubAction(def, SubActionStore.Revision(def.Id));
        return CheckPayload(result, result.Ok, parseError: null);
    }

    /// <summary>
    /// 整份写入一个子动作（新建或覆盖）。两道闸与 <c>staging_promote</c> 一致：
    /// <see cref="RequireWrites"/> + <see cref="RequireConfirm"/>；写前跑静态校验，有 error 就拒绝。
    /// </summary>
    public static Dictionary<string, object?> SubActionsWrite(JsonElement program, string? expectedRevision)
    {
        RequireWrites();

        var def = ParseSubAction(program);
        def.Id = (def.Id ?? "").Trim();
        def.Name = (def.Name ?? "").Trim();
        if (def.Id.Length == 0)
            throw new AiToolException("invalid_program", "子动作缺少 id（subactions/<id>.json 的文件名就是 id）");
        if (def.Name.Length == 0)
            throw new AiToolException("invalid_program", "子动作缺少 name");

        string? current = SubActionStore.Revision(def.Id);
        bool exists = current != null;

        var validation = ActionValidator.ValidateSubAction(def);
        if (!validation.Ok)
        {
            var errors = validation.Issues.Where(i => i.Severity == IssueSeverity.error).Take(5).ToList();
            throw new AiToolException("check_failed",
                $"静态校验有 {validation.Errors} 个 error，先修好再写入：{string.Join("；", errors.Select(i => i.Message))}");
        }

        // CAS：给了期望 revision 就必须对得上（文件内容 sha256，谁写的都能重算）。
        if (!string.IsNullOrWhiteSpace(expectedRevision) &&
            !string.Equals(expectedRevision.Trim(), current ?? "", StringComparison.OrdinalIgnoreCase))
            throw new DraftConflictException(exists
                ? "子动作已被改动（期望的 revision 与当前不一致），先重新读取"
                : "子动作不存在（或已被删除），先重新读取");

        RequireConfirm(exists ? "修改子动作" : "新建子动作",
            exists
                ? $"AI 要覆盖子动作「{def.Name}」（{def.Id}），调用它的动作会一起变。"
                : $"AI 要新建子动作「{def.Name}」（{def.Id}），共 {def.Steps.Count} 步。",
            okText: exists ? "覆盖" : "新建");

            SubActionStore.Save(def, createNew: !exists);

        return new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["created"] = !exists,
            ["steps"] = def.Steps.Count,
            ["inputs"] = def.Variables.Count(v => v.Direction == VariableDirection.input),
            ["outputs"] = def.Variables.Count(v => v.Direction == VariableDirection.output),
            ["revision"] = SubActionStore.Revision(def.Id),
            ["warnings"] = validation.Warnings,
        };
    }

    public static Dictionary<string, object?> SubActionsDelete(string id)
    {
        RequireWrites();

        id = (id ?? "").Trim();
        var def = SubActionStore.Load(id) ?? throw new AiToolException("not_found", $"子动作不存在：{id}");
        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;

        var refs = SubActionStore.FindReferences(id);
        string detail = refs.Count == 0
            ? $"确定删除子动作「{name}」（{id}）？目前没有动作在调用它。此操作不可恢复。"
            : $"确定删除子动作「{name}」（{id}）？下面 {refs.Count} 处调用之后会报「找不到子动作」：\n"
              + string.Join("\n", refs.Take(8).Select(r => $"· {r.ActionName} · {r.Where}"));

        RequireConfirm("删除子动作", detail, danger: true);
        SubActionStore.Delete(id);

        return new Dictionary<string, object?>
        {
            ["deleted"] = true,
            ["id"] = id,
            ["name"] = name,
            ["references"] = refs.Count,
        };
    }

    /// <summary>
    /// 子动作的签名（id / 名称 / 输入 / 输出）。给 <c>sub.run</c> 的动作单元规格一起返回，
    /// agent 才知道该传哪些参数名——这些名字不在动作单元声明里，是每个子动作自己的变量。
    /// </summary>
    private static Dictionary<string, object?> Signature(SubActionDef def) => new()
    {
        ["id"] = def.Id,
        ["name"] = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name,
        ["inputs"] = def.Variables
            .Where(v => v.Direction == VariableDirection.input)
            .Select(v => new Dictionary<string, object?>
            {
                ["key"] = v.Key,
                ["type"] = v.Type.ToString(),
                ["required"] = IsEmptyValue(v.Seed()),
                ["default"] = v.Seed(),
                ["description"] = v.Label,
            })
            .ToList(),
        ["outputs"] = def.Variables
            .Where(v => v.Direction == VariableDirection.output)
            .Select(v => new Dictionary<string, object?>
            {
                ["key"] = v.Key,
                ["type"] = v.Type.ToString(),
                ["description"] = v.Label,
            })
            .ToList(),
    };

    private static bool IsEmptyValue(Value value) => value.Kind switch
    {
        ValueKind.text => value.AsText().Length == 0,
        ValueKind.list => value.Items.Count == 0,
        ValueKind.@object => value.Members.Count == 0,
        _ => false,
    };

    public static Dictionary<string, object?> StagingList()
    {
        var drafts = DraftStore.List().Select(meta => new Dictionary<string, object?>
        {
            ["id"] = meta.Id,
            ["title"] = meta.Title,
            ["status"] = meta.Status.ToString(),
            ["origin"] = meta.Origin,
            ["createdAt"] = meta.CreatedAt,
            ["updatedAt"] = meta.UpdatedAt,
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
            ["baseActionId"] = meta.BaseActionId,
            ["promotedActionId"] = meta.PromotedActionId,
            ["summary"] = meta.Summary,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = drafts.Count,
            ["drafts"] = drafts,
        };
    }

}
