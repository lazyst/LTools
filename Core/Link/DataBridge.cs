using System;
using LTools.Config;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 配置写入的**执行器侧**：把 <c>configs/*.json</c> 的落盘转给普通权限宿主。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="ConfigBundle.RemoteWriter"/> 调用（在 <c>ConfigIO</c> 的锁里、配置写路径上）。
/// 链路没连上 / 宿主出错时返回 <c>false</c>，调用方照旧本地写——降级只影响「谁写」，不丢改动。
/// </para>
/// <para>
/// 同步等待是有意的：配置写入本来就要落定，调用方（设置页保存、托盘开关、面板结构保存）
/// 都是「改完就要生效」的路径；本地管道往返在毫秒级，只有宿主失联时才会等满超时。
/// </para>
/// </remarks>
internal static class DataBridge
{
    /// <summary>配置写超时：本地管道往返很快，宿主卡住时别把界面吊太久。</summary>
    private const int WriteTimeoutMs = 2000;

    /// <summary>把一份域配置的 JSON 交给宿主落盘；<c>false</c> = 没写成，调用方本地写。</summary>
    public static bool TryWriteConfig(string name, string json)
    {
        // 宿主自己就是写者本人，永远不该走这条链路（防御：万一钩子被误挂）。
        if (ConfigStore.ReadOnly) return false;
        if (!LinkWorker.IsConnected) return false;

        try
        {
            LinkMessage reply = LinkWorker
                .RequestAsync(DataMethods.Write, new DataWriteRequest(name, json), WriteTimeoutMs)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("Data", $"宿主没能写入 {name}：{reply.Error}，改成本进程写");
                return false;
            }

            return reply.Read<DataWriteReply>()?.Written == true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Data", $"写入 {name} 的请求失败：{ex.Message}，改成本进程写");
            return false;
        }
    }

    /// <summary>挂到 <see cref="ConfigBundle.RemoteWriter"/> 上（<c>App.OnStartup</c> 调一次）。</summary>
    public static void Attach() => ConfigBundle.RemoteWriter = TryWriteConfig;
}
