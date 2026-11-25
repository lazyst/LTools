using System;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// 「宿主写过配置」的通知（**宿主 → 执行器**）：宿主是 <c>configs/</c> 的物理写者，
/// 但运行期状态（钩子 / 触发器 / 宏设置 / 截图默认值）在执行器里，写完要让它跟着重读。
/// </summary>
/// <remarks>
/// 只有宿主用；执行器自己写的那几条（转给宿主落盘）不用发——改动就是它起的，内存已经是对的。
/// 发的是**不带答复的事件**（<c>NotifyAsync</c>），别做成请求：写配置的调用点在 UI 线程上，
/// 等一次往返会平白多一层「保存时卡一下」。
/// </remarks>
internal static class ConfigChangedBridge
{
    private static LinkServer? _link;

    /// <summary>宿主启动链路服务端后挂上（与其它 Bridge 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>告诉执行器「磁盘变了，重读一遍」；<paramref name="reason"/> 只进日志。</summary>
    public static void Notify(string reason)
    {
        LinkServer? link = _link;
        if (link == null || !link.IsConnected) return;

        _ = Task.Run(async () =>
        {
            try { await link.NotifyAsync(LinkMethods.EventConfigChanged, new ConfigChangedEvent(reason)); }
            catch (Exception ex) { AppLog.Warn("Link", $"通知『配置已改』失败：{ex.Message}"); }
        });
    }
}
