using LTools.Config;

namespace LTools.Features;

/// <summary>
/// 自动触发的事件源（二期：窗口 / 进程切换、剪贴板变化、文件系统变化）。
/// </summary>
/// <remarks>
/// <para>
/// 一期只保留接口与「统一投递」的约定，不注册任何实现；定时 / 启动后由
/// <see cref="TriggerScheduler"/> 直接排程。二期新增事件源 = 实现本接口 +
/// 在 <see cref="TriggerService"/> 的注册点加一行，投递一律回调到
/// <see cref="TriggerService.Fire"/>，不在事件源里自己调动作执行链。
/// </para>
/// <para>
/// <see cref="Start"/> / <see cref="Stop"/> 都在后台线程调用，不碰 WPF / 不阻塞；
/// 事件源要把系统回调里的重活（查进程、读剪贴板）放到后台，别堵消息泵。
/// </para>
/// </remarks>
internal interface ITriggerSource
{
    /// <summary>开始订阅；命中时调用 <paramref name="fire"/> 把命中的规则交给统一投递。</summary>
    void Start(Action<TriggerRule> fire);

    /// <summary>停止订阅（退出时调用，不在 UI 线程上等待后台关停）。</summary>
    void Stop();
}
