using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Native;
using ModelContextProtocol.Protocol;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具的实际实现。所有工具共用一个回执形状与审计入口，避免每个工具各写一份 try/catch。
/// </summary>
internal static partial class AiToolHandlers
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(), new ValueJsonConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>统一的工具调用入口：执行、写审计、把异常翻译成 JSON 回执。</summary>
    public static string Call(string tool, string? draftId, Func<Dictionary<string, object?>> body)
    {
        long started = Environment.TickCount64;
        try
        {
            var payload = body();
            payload["ok"] = true;
            AiAudit.Write(tool, draftId, "ok", Environment.TickCount64 - started);
            return Serialize(payload);
        }
        catch (Exception ex)
        {
            var (code, message) = Describe(ex, tool);
            AiAudit.Write(tool, draftId, code, Environment.TickCount64 - started);
            return Error(code, message);
        }
    }

    /// <summary>异常 → 回执里的 <c>error</c> 码 + 人话。所有工具（含带图片的回执）共用这一份。</summary>
    private static (string Code, string Message) Describe(Exception ex, string tool) => ex switch
    {
        AiToolException e => (e.Code, e.Message),
        DraftConflictException e => ("version_conflict", e.Message),
        ActionException e => ("invalid_action", e.Message),
        FileNotFoundException e => ("not_found", e.Message),
        _ => Logged(ex, tool),
    };

    private static (string Code, string Message) Logged(Exception ex, string tool)
    {
        AppLog.Error("Ai", $"工具 {tool} 失败", ex);
        return ("internal_error", ex.Message);
    }
}
