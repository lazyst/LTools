using System;
using System.Threading.Tasks;
using LTools.Core.Actions;

namespace LTools.Core.Link;

/// <summary>
/// 全局子动作库写入的**宿主侧**：<c>subactions/&lt;id&gt;.json</c> 的唯一物理写者。
/// </summary>
/// <remarks>
/// 只做一件事：把收到的定义照写进文件（先 <c>.tmp</c> 再替换，与本地写同一条实现）。
/// 宿主里没有子动作库页面（那页跟着设置走），所以写完不用刷界面；
/// 执行器那边由 <c>SubActionStore</c> 自己 <c>Invalidate()</c>。
/// </remarks>
internal static class SubActionHost
{
    /// <summary>处理一条写入请求；不是这两个方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method == SubActionMethods.Save)
        {
            SubActionSaveRequest? req = request.Read<SubActionSaveRequest>();
            if (req?.Def == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

            try
            {
                SubActionStore.Save(req.Def, req.CreateNew);
                AppLog.Info("SubAction", $"已写入子动作（{req.Def.Id}，替主程序落盘）");
                return Task.FromResult(LinkMessage.Response(request, new SubActionReply(true, null)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("SubAction", $"替主程序写子动作失败：{ex.Message}");
                return Task.FromResult(LinkMessage.Response(request, new SubActionReply(false, ex.Message)));
            }
        }

        if (request.Method == SubActionMethods.Delete)
        {
            SubActionDeleteRequest? req = request.Read<SubActionDeleteRequest>();
            if (req == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

            try
            {
                SubActionStore.Delete(req.Id);
                AppLog.Info("SubAction", $"已删除子动作（{req.Id}，替主程序落盘）");
                return Task.FromResult(LinkMessage.Response(request, new SubActionReply(true, null)));
            }
            catch (Exception ex)
            {
                AppLog.Warn("SubAction", $"替主程序删子动作失败：{ex.Message}");
                return Task.FromResult(LinkMessage.Response(request, new SubActionReply(false, ex.Message)));
            }
        }

        return null;
    }
}
