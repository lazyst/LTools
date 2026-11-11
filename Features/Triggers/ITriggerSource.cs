using LTools.Config;

namespace LTools.Features;

/// <summary>
/// 自动触发的事件源（窗口切换、剪贴板变化、文件系统变化，见 <c>docs/AUTO_TRIGGER.md</c> §13）。
/// </summary>
/// <remarks>
/// <para>
/// 事件源只负责订阅系统事件，命中时构造一条 <see cref="TriggerEvent"/> 交给统一投递
/// <see cref="TriggerService.FireEvent"/>；**不自己筛选规则、不自己调动作执行链、不自己判安全策略**。
/// 条件匹配、规则顺序、<c>SkipFurther</c>、防抖节流延迟、并发单飞都在 <see cref="TriggerService"/> 一侧。
/// </para>
/// <para>
/// <see cref="Start"/> / <see cref="Reload"/> / <see cref="Stop"/> 都在后台线程调用，不碰 WPF / 不阻塞；
/// 系统回调里的重活（查进程、读剪贴板）要放到后台，别堵消息泵（见钩子关键约束）。
/// </para>
/// </remarks>
internal interface ITriggerSource
{
    /// <summary>开始订阅；命中时把 <see cref="TriggerEvent"/> 交给统一投递。</summary>
    void Start(Action<TriggerEvent> fire);

    /// <summary>
    /// 配置变了：按当前 <see cref="TriggerRegistry.Active"/> 重建订阅
    /// （设置页保存 / 导入备份 / 删动作后由 <see cref="TriggerService.Reload"/> 调用）。
    /// </summary>
    void Reload();

    /// <summary>停止订阅（退出时调用，不在 UI 线程上等待后台关停）。</summary>
    void Stop();
}
