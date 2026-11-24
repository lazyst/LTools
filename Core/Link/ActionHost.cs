using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作库写入的**宿主侧**：<c>actions/</c> 的唯一物理写者。
/// </summary>
/// <remarks>
/// <para>
/// 收到整份清单就按一个动作一个目录写（<see cref="ActionStore.SaveAll"/>：内容没变的目录不重写、
/// 清单里没有的目录搬进 <c>trash/</c>、变动的动作留一份历史快照）。**不绕 `ConfigIO.Modify`**：
/// 那条路会先把整个 <c>actions/</c> 扫一遍解析，纯属白扫。
/// </para>
/// <para>
/// 写完顺手把宿主这份**只读**动作清单换成发来的这一份（面板显示动作名、暂存区预览都靠它），
/// 但**不整份 Reload**——那会连面板库一起重读，把正在拖磁贴的用户打断。
/// </para>
/// </remarks>
internal static class ActionHost
{
    /// <summary>处理一条写入请求；不是该方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method != ActionMethods.SaveAll) return null;

        ActionSaveRequest? req = request.Read<ActionSaveRequest>();
        if (req?.Actions == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

        try
        {
            var snapshot = new List<ActionDto>(req.Actions);
            // 直接走动作目录的落盘实现：`ConfigIO.Modify(actions)` 会先 AppConfig.LoadStrict 把整个
            // `actions/` 扫一遍解析（每次保存都白扫一次，还会在「刚建目录、还没写 action.json」时刷一条
            // 「忽略没有 action.json 的目录」）。这里只做一件事：把清单照写进目录。
            ActionStore.SaveAll(ConfigLocator.FindPath(), snapshot);
            // 内存副本换到 UI 线程上做：面板快照 / 显示名随时在读它，链路线程直接重置会和它们撞车。
            Ui.Post(() => ActionLibrary.ReplaceAll(snapshot));
            AppLog.Info("Action", $"已写入动作库（{snapshot.Count} 个动作，替主程序落盘）");
            return Task.FromResult(LinkMessage.Response(request, new ActionSaveReply(true, null)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Action", $"替主程序写动作库失败：{ex.Message}");
            return Task.FromResult(LinkMessage.Response(request, new ActionSaveReply(false, ex.Message)));
        }
    }
}
