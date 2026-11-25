using LTools.Core.Ai;

namespace LTools.Core.Link;

/// <summary>
/// AI 接入（MCP 服务）的方法名（**宿主 → 执行器**）：服务本身、令牌、知识包都在提权进程那边。
/// </summary>
/// <remarks>
/// <para>
/// 设置 →「AI 接入」页开在宿主里（M3-5c-3c）：它只是这一页的**控制面板**，不再是配置的属主。
/// <c>ai/mcp.json</c> 与 <c>ai/knowledge/</c> 只有一个写者（执行器，它要跑服务、要按动作单元
/// 生成知识包），宿主连读都不读——状态与令牌随 <see cref="McpState"/> 回来。
/// </para>
/// <para>
/// **`mcp.apply` 不等服务起来**：`McpServer.Apply` 要等旧实例真正释放端口（最多 8 秒），把它放进
/// 答复里会让设置页点一下卡好几秒。这里保存完就排后台重启，答复带的是「此刻」的状态，
/// 页面靠已有的 1 秒轮询跟上（与搬窗口之前的行为一致）。
/// </para>
/// </remarks>
internal static class McpMethods
{
    /// <summary>当前配置 + 服务是否在跑（1 秒轮询用）。</summary>
    public const string Status = "mcp.status";

    /// <summary>保存开关 / 端口 / 写入 / 审批模式，然后重启服务（不等它起来）。</summary>
    public const string Apply = "mcp.apply";

    /// <summary>重置令牌（已有客户端会立刻断开）。</summary>
    public const string ResetToken = "mcp.resetToken";

    /// <summary>重建知识包（<c>ai/knowledge/*.md</c> 三份）。</summary>
    public const string Knowledge = "mcp.knowledge";
}

/// <summary>要保存的四项（令牌不在里面：它只由 <see cref="McpMethods.ResetToken"/> 换）。</summary>
internal sealed record McpApplyRequest(bool Enabled, int Port, bool AllowWrites, AiApprovalMode ApprovalMode);

/// <summary>
/// MCP 服务的完整状态：配置（含令牌，宿主拿它拼提示词 / 客户端 JSON）+ 是否在跑。
/// </summary>
/// <remarks>
/// 令牌走本机命名管道（与 <c>ai/mcp.json</c> 同一条信任边界：同一个用户），不回日志、不进审计。
/// </remarks>
internal sealed record McpState(
    bool Enabled,
    int Port,
    bool AllowWrites,
    AiApprovalMode ApprovalMode,
    string Token,
    bool Running,
    int RunningPort,
    string? LastError);

/// <summary>知识包重建结果：写进去的动作单元数。</summary>
internal sealed record McpKnowledgeReply(int Units);
