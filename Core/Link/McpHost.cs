using System;
using System.Threading.Tasks;
using LTools.Core.Ai;

namespace LTools.Core.Link;

/// <summary>
/// MCP 服务的**执行器侧**：配置属主、服务本体、知识包都在这里。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="McpBridge"/> 配对。所有方法都是「读 / 写一个小文件 + 排一次重启」，
/// 不需要 UI 线程，链路处理器直接做（`McpServer.Apply` 本身也是后台线程跑的）。
/// </para>
/// <para>
/// 配置读写只走 <see cref="AiSettings"/>：**别在别处直接拼 <c>ai/mcp.json</c>**。
/// 「重启服务」永远排后台（<c>Task.Run(McpServer.Apply)</c>）：它会等旧实例释放端口（最多 8 秒），
/// 在当前线程等就等于让这条命令卡死。
/// </para>
/// </remarks>
internal static class McpHost
{
    /// <summary>这四条方法名归这里处理（同步入口与异步链路共用一张名单）。</summary>
    public static bool IsCommand(string method) => method is McpMethods.Status or McpMethods.Apply
        or McpMethods.ResetToken or McpMethods.Knowledge;

    /// <summary>处理一条 MCP 命令；不是这些方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) => request.Method switch
    {
        McpMethods.Status => Task.FromResult(LinkMessage.Response(request, Status())),
        McpMethods.Apply => Task.FromResult(Apply(request)),
        McpMethods.ResetToken => Task.FromResult(LinkMessage.Response(request, ResetToken())),
        McpMethods.Knowledge => Task.FromResult(Knowledge(request)),
        _ => null,
    };

    /// <summary>同步执行一条 MCP 命令（自测 / 冒烟直接调它，不必起管道）。</summary>
    public static LinkMessage Handle(LinkMessage request) => request.Method switch
    {
        McpMethods.Status => LinkMessage.Response(request, Status()),
        McpMethods.Apply => Apply(request),
        McpMethods.ResetToken => LinkMessage.Response(request, ResetToken()),
        McpMethods.Knowledge => Knowledge(request),
        _ => LinkMessage.Response(request, error: "未知的 AI 命令：" + request.Method),
    };

    /// <summary>当前配置 + 服务状态（设置页 1 秒轮询）。</summary>
    public static McpState Status()
    {
        AiSettings settings = AiSettings.Load();
        return new McpState(
            settings.Enabled,
            settings.Port,
            settings.AllowWrites,
            settings.ApprovalMode,
            settings.Token,
            McpServer.IsRunning,
            McpServer.RunningPort,
            McpServer.LastError ?? settings.LastError);
    }

    /// <summary>保存四项然后重启（不等服务起来，状态靠轮询跟上）。</summary>
    public static McpState Save(McpApplyRequest request)
    {
        AiSettings settings = AiSettings.Load();
        settings.Enabled = request.Enabled;
        settings.Port = AiSettings.NormalizePort(request.Port);
        settings.AllowWrites = request.AllowWrites;
        settings.ApprovalMode = request.ApprovalMode;
        settings.LastError = null;
        settings.Save();

        Restart();
        return Status();
    }

    /// <summary>换一个新令牌（已连上的客户端下一次请求起就 401）。</summary>
    public static McpState ResetToken()
    {
        AiSettings settings = AiSettings.Load();
        settings.ResetToken();
        Restart();
        return Status();
    }

    /// <summary>重建知识包：写进 <paramref name="units"/> 个动作单元的说明。</summary>
    public static int RegenerateKnowledge() => KnowledgePack.Regenerate();

    /// <summary>重启服务：**永远排后台**，绝不在调用线程上等旧实例释放端口。</summary>
    private static void Restart() => _ = Task.Run(McpServer.Apply);

    private static LinkMessage Apply(LinkMessage request)
    {
        McpApplyRequest? req = request.Read<McpApplyRequest>();
        return req == null
            ? LinkMessage.Response(request, error: "缺少参数")
            : LinkMessage.Response(request, Save(req));
    }

    private static LinkMessage Knowledge(LinkMessage request) =>
        LinkMessage.Response(request, new McpKnowledgeReply(RegenerateKnowledge()));
}
