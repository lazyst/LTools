using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 配置整份重读之后的**通知**（执行器 → 宿主）：宿主那份只读副本已经过期，跟着重读。
/// </summary>
/// <remarks>
/// 只有 <see cref="ConfigStore.Reload"/> 一个调用点：导入备份、设置页「重新加载」都会走它，
/// 别在各个按钮里各发一次（漏一个就是「宿主面板还是旧的」）。
/// 只发不排队：广播的是状态，宿主没连上时它本来也没有过期副本。
/// </remarks>
internal static class ConfigReloadBridge
{
    public static void Notify()
    {
        if (ConfigStore.ReadOnly || !LinkWorker.IsConnected) return;

        try { _ = LinkWorker.NotifyAsync(LinkMethods.EventConfigReload); }
        catch (System.Exception ex) { AppLog.Warn("Link", $"通知配置重读失败：{ex.Message}"); }
    }
}
