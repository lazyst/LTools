using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 「宿主写过配置」的**执行器侧**：从磁盘重读各域，把运行期状态跟上（不写盘、不回推宿主）。
/// </summary>
/// <remarks>
/// 排到 UI 线程做（<see cref="Ui.Enqueue"/>）：重读会碰快捷键匹配表 / 触发器 / 宏设置，
/// 都是 UI 线程平时改的那些静态状态；读盘本身几十毫秒，UI 卡一下不会影响输入（钩子在
/// <c>HookDispatcher</c> 自己的线程上）。
/// </remarks>
internal static class ConfigChangedHost
{
    /// <summary>处理 <c>config.changed</c>；不是这条事件时什么都不做。</summary>
    public static void OnEvent(LinkMessage message)
    {
        if (message.Method != LinkMethods.EventConfigChanged) return;

        ConfigChangedEvent? changed = message.Read<ConfigChangedEvent>();
        AppLog.Info("Config", $"宿主写过配置（{changed?.Reason ?? "?"}），执行器重读运行期状态");
        Ui.Enqueue(ConfigStore.ReloadRuntime);
    }
}
