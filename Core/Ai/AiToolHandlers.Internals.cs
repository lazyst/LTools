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
    // ============ 内部 ============

    private static ActionProgram ParseProgram(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string text = element.GetString() ?? "";
                return JsonSerializer.Deserialize<ActionProgram>(text, DraftStore.Json)
                       ?? throw new AiToolException("invalid_program", "program 是空的");
            }

            if (element.ValueKind != JsonValueKind.Object)
                throw new AiToolException("invalid_program", "program 必须是 JSON 对象");

            return element.Deserialize<ActionProgram>(DraftStore.Json)
                   ?? throw new AiToolException("invalid_program", "program 解析结果为空");
        }
        catch (JsonException ex)
        {
            throw new AiToolException("invalid_program", "program 解析失败：" + ex.Message);
        }
    }

    private static SubActionDef ParseSubAction(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
                return SubActionStore.Deserialize(element.GetString() ?? "");

            if (element.ValueKind != JsonValueKind.Object)
                throw new AiToolException("invalid_program", "program 必须是 JSON 对象（子动作定义）");

            return element.Deserialize<SubActionDef>(SubActionStore.JsonOptions)
                   ?? throw new AiToolException("invalid_program", "program 解析结果为空");
        }
        catch (JsonException ex)
        {
            throw new AiToolException("invalid_program", "program 解析失败：" + ex.Message);
        }
    }

    /// <summary>校验回执（草稿 check 与子动作 check 共用同一形状）。</summary>
    private static Dictionary<string, object?> CheckPayload(ActionValidationResult result, bool ok,
        string? parseError) => new()
        {
            ["ok"] = ok,
            ["revision"] = result.Revision,
            ["errors"] = result.Errors,
            ["warnings"] = result.Warnings,
            ["issues"] = result.Issues.Select(issue => new Dictionary<string, object?>
            {
                ["path"] = issue.Path,
                ["label"] = issue.Label,
                ["unit"] = issue.Unit,
                ["key"] = issue.Key,
                ["message"] = issue.Message,
                ["severity"] = issue.Severity.ToString(),
            }).ToList(),
            ["parseError"] = parseError,
        };

    private static void RequireWrites()
    {
        var settings = AiSettings.Load();
        if (!settings.WritesAllowed)
        {
            throw new AiToolException("writes_disabled",
                "写入没开启：让用户在 LTools 设置 → AI 里打开「允许写入」，并把审批模式从「只读」改掉");
        }
    }

    private static void RequireConfirm(string action, string detail, bool danger = false, string? okText = null)
    {
        var settings = AiSettings.Load();
        if (settings.ApprovalMode == AiApprovalMode.full) return;
        if (!AiUi.Confirm(action, detail, danger, okText))
            throw new AiToolException("denied", "用户拒绝了这次操作");
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOpt);

    private static string Error(string code, string message) =>
        Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = code,
            ["message"] = message,
        });
}
