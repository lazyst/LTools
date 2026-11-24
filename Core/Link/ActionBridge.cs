using System;
using System.Collections.Generic;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作库写入的**执行器侧**：把整份动作清单交给普通权限宿主落盘。
/// </summary>
/// <remarks>
/// 由 <c>ActionLibrary.Save</c> 调用。返回 <c>false</c> = 没转成（宿主没起 / 出错），
/// 调用方照旧本地写——降级只影响「谁写」，不丢改动。
/// </remarks>
internal static class ActionBridge
{
    /// <summary>
    /// 整份清单比单个配置域大（动作多时上百 KB），给宽一点；但宿主卡住时仍然很快就退回本地写，
    /// 别把界面吊在一条链路上。
    /// </summary>
    private const int SaveTimeoutMs = 5_000;

    public static bool TrySaveAll(IReadOnlyList<ActionDto> actions)
    {
        if (ConfigStore.ReadOnly) return false;      // 宿主自己就是写者
        if (!LinkWorker.IsConnected) return false;

        try
        {
            var request = new ActionSaveRequest(new List<ActionDto>(actions));
            LinkMessage reply = LinkWorker.RequestAsync(ActionMethods.SaveAll, request, SaveTimeoutMs)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("Action", $"宿主没能写入动作库：{reply.Error}，改成本进程写");
                return false;
            }

            ActionSaveReply? result = reply.Read<ActionSaveReply>();
            if (result is { Ok: false })
            {
                AppLog.Warn("Action", $"宿主写动作库失败：{result.Error}，改成本进程写");
                return false;
            }

            return result?.Ok == true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Action", $"写动作库的请求失败：{ex.Message}，改成本进程写");
            return false;
        }
    }
}
