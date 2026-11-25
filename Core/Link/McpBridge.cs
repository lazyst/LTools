using System;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// MCP 服务的**宿主侧**：把「看状态 / 存配置 / 重置令牌 / 重建知识包」交给执行器。
/// </summary>
/// <remarks>
/// <para>
/// 只有宿主里的「AI 接入」页会调；执行器自己的设置页走 <see cref="McpHost"/> 的本地分支
/// （两条分支只在 <see cref="Features.McpControl"/> 里分一次）。
/// </para>
/// <para>
/// 状态查询每 1 秒跑一次，所以 <see cref="StatusAsync"/> 是异步的、超时也短：
/// 在 UI 线程上同步等一次往返，等于让面板 / 右键菜单跟着卡一下。失败（没连上 / 超时）
/// 一律给 null——调用方按「看不到服务状态」显示，别拿假数据糊弄。
/// </para>
/// </remarks>
internal static class McpBridge
{
    /// <summary>保存 / 重置令牌要写文件、还要排一次重启：给宽一点，超时算失败。</summary>
    private const int WriteTimeoutMs = 5_000;

    /// <summary>1 秒轮询用：本地管道往返是亚毫秒级，超过这个数就说明对端忙，直接按「没拿到」算。</summary>
    private const int StatusTimeoutMs = 1_500;

    private static LinkServer? _link;

    /// <summary>宿主启动链路服务端后挂上（与其它 Bridge 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>当前状态；没连上 / 超时 / 被拒都给 null。</summary>
    public static async Task<McpState?> StatusAsync(int timeoutMs = StatusTimeoutMs)
    {
        LinkServer? link = _link;
        if (link == null || !link.IsConnected) return null;

        try
        {
            LinkMessage reply = await link.RequestAsync(McpMethods.Status, timeoutMs: timeoutMs)
                .ConfigureAwait(false);
            return reply.Ok ? reply.Read<McpState>() : null;
        }
        catch (Exception)
        {
            return null;   // 轮询失败不刷日志：下一秒还会再问
        }
    }

    /// <summary>保存四项并重启；失败给可显示的原因。</summary>
    public static McpState? Apply(McpApplyRequest request, out string? error) =>
        Request(McpMethods.Apply, request, out error)?.Read<McpState>();

    /// <summary>重置令牌；返回的状态里带新令牌。</summary>
    public static McpState? ResetToken(out string? error) =>
        Request(McpMethods.ResetToken, null, out error)?.Read<McpState>();

    /// <summary>重建知识包；<paramref name="units"/> = 写进去的动作单元数。</summary>
    public static bool RegenerateKnowledge(out int units, out string? error)
    {
        units = 0;
        McpKnowledgeReply? knowledge = Request(McpMethods.Knowledge, null, out error)
            ?.Read<McpKnowledgeReply>();
        if (knowledge == null) return false;

        units = knowledge.Units;
        return true;
    }

    /// <summary>发一条要答复的请求；失败（没连上 / 被拒 / 超时）返回 null 并给原因。</summary>
    private static LinkMessage? Request(string method, object? payload, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上";
            return null;
        }

        try
        {
            LinkMessage reply = link.RequestAsync(method, payload, timeoutMs: WriteTimeoutMs)
                .GetAwaiter().GetResult();
            if (!reply.Ok)
            {
                error = reply.Error ?? "主程序拒绝了这次改动";
                return null;
            }
            return reply;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
