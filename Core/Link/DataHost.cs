using System;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 配置文件写入的**宿主侧**：<c>configs/*.json</c> 的唯一物理写者。
/// </summary>
/// <remarks>
/// <para>
/// 收到执行器转来的域配置就落盘（<see cref="ConfigBundle.TryWriteDomainFile"/>：
/// 临时文件 + 整体替换，名字不在七个域文件白名单里直接拒绝）。
/// </para>
/// <para>
/// **不在这里重读自己的内存配置**：宿主那份 <c>ConfigStore</c> 是只读副本，
/// 而这些域的属主（设置页 / 快捷键 / 触发规则 / 面板的编辑入口）此刻还在执行器里，
/// 宿主不需要跟着变。等这些页面搬进宿主、宿主成为属主时，改成「谁改谁重读」。
/// </para>
/// </remarks>
internal static class DataHost
{
    /// <summary>处理一条配置写入请求；不是该方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method != DataMethods.Write) return null;

        DataWriteRequest? req = request.Read<DataWriteRequest>();
        if (req == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

        bool written;
        try
        {
            written = ConfigBundle.TryWriteDomainFile(ConfigLocator.FindPath(), req.Name, req.Json);
            if (written) AppLog.Info("Data", $"已写入 {req.Name}（替主程序落盘）");
            else AppLog.Warn("Data", $"拒绝写入 configs/{req.Name}（不在域文件白名单里）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Data", $"写入 {req.Name} 失败：{ex.Message}");
            return Task.FromResult(LinkMessage.Response(request, new DataWriteReply(false)));
        }

        return Task.FromResult(LinkMessage.Response(request, new DataWriteReply(written)));
    }
}
