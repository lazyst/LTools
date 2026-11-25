using System;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 「AI 接入」页的**唯一 UI 入口**：看状态 / 存配置 / 重置令牌 / 重建知识包。
/// </summary>
/// <remarks>
/// <para>
/// MCP 服务、<c>ai/mcp.json</c> 与 <c>ai/knowledge/</c> 都只有执行器一个写者（它要跑服务、
/// 按动作单元生成知识包）。宿主里的页面经 <see cref="McpBridge"/> 转过去，执行器自己的设置页
/// 直接调 <see cref="McpHost"/>。两条分支只在这里分，别在页面里各判一次身份。
/// </para>
/// <para>
/// 状态查询有两个入口：<see cref="Status"/> 给「打开页面 / 改完设置」用（同步，往返是亚毫秒级），
/// <see cref="StatusAsync"/> 给 1 秒轮询用（**别在 UI 线程上同步等轮询**，对端忙一下面板就跟着卡）。
/// 拿不到一律 null——页面按「看不到服务状态」显示。
/// </para>
/// </remarks>
internal static class McpControl
{
    /// <summary>当前配置 + 服务状态（同步；打开页面、改完设置时用）。</summary>
    public static McpState? Status() =>
        AppRole.IsUiHost ? McpBridge.StatusAsync().GetAwaiter().GetResult() : McpHost.Status();

    /// <summary>当前配置 + 服务状态（异步；1 秒轮询用，绝不阻塞 UI 线程）。</summary>
    public static Task<McpState?> StatusAsync() =>
        AppRole.IsUiHost ? McpBridge.StatusAsync() : Task.FromResult<McpState?>(McpHost.Status());

    /// <summary>保存开关 / 端口 / 写入 / 审批模式并重启服务；失败返回 null 并给原因。</summary>
    public static McpState? Apply(McpApplyRequest request, out string? error)
    {
        error = null;
        if (AppRole.IsUiHost) return McpBridge.Apply(request, out error);

        try { return McpHost.Save(request); }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>重置令牌；返回的状态里带新令牌。</summary>
    public static McpState? ResetToken(out string? error)
    {
        error = null;
        if (AppRole.IsUiHost) return McpBridge.ResetToken(out error);

        try { return McpHost.ResetToken(); }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>重建知识包；<paramref name="units"/> = 写进去的动作单元数。</summary>
    public static bool RegenerateKnowledge(out int units, out string? error)
    {
        error = null;
        units = 0;

        if (AppRole.IsUiHost) return McpBridge.RegenerateKnowledge(out units, out error);

        try
        {
            units = McpHost.RegenerateKnowledge();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
