using System;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 全局子动作库写入的**执行器侧**：把一份定义 / 一个 id 交给普通权限宿主落盘。
/// </summary>
/// <remarks>
/// 由 <c>SubActionStore.Save</c> / <c>Delete</c> 调用。返回 <c>false</c> = 没转成（宿主没起 / 出错），
/// 调用方照旧本地写——降级只影响「谁写」，不丢改动。
/// </remarks>
internal static class SubActionBridge
{
    /// <summary>一份定义很小（几 KB），但宿主可能正在写盘；给 3 秒就够，超时退回本地写。</summary>
    private const int TimeoutMs = 3_000;

    public static bool TrySave(SubActionDef def, bool createNew)
    {
        if (ConfigStore.ReadOnly || !LinkWorker.IsConnected) return false;
        return Request(SubActionMethods.Save, new SubActionSaveRequest(def, createNew));
    }

    public static bool TryDelete(string id)
    {
        if (ConfigStore.ReadOnly || !LinkWorker.IsConnected) return false;
        return Request(SubActionMethods.Delete, new SubActionDeleteRequest(id));
    }

    private static bool Request(string method, object payload)
    {
        try
        {
            LinkMessage reply = LinkWorker.RequestAsync(method, payload, TimeoutMs).GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("SubAction", $"宿主没能写入子动作：{reply.Error}，改成本进程写");
                return false;
            }

            SubActionReply? result = reply.Read<SubActionReply>();
            if (result is { Ok: false })
            {
                AppLog.Warn("SubAction", $"宿主写子动作失败：{result.Error}，改成本进程写");
                return false;
            }

            return result?.Ok == true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("SubAction", $"写子动作的请求失败：{ex.Message}，改成本进程写");
            return false;
        }
    }
}
