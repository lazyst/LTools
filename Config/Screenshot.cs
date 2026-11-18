namespace LTools.Config;

/// <summary>
/// 截图 Pro 的全局设置（<c>configs/settings.json</c> 的 <c>Screenshot</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>screenshot/*</c>：目前只有「延时截图」的等待秒数
/// （<c>screenshot/delay_seconds</c>，clamp 1–10，默认 3）。
/// 采集入口自己的参数（截哪块、要不要自动保存）挂在功能参数上，不在这里。
/// </remarks>
public sealed class ScreenshotFile
{
    /// <summary>「延时截图」等待多少秒（1–10，默认 3）。</summary>
    public int? DelaySeconds { get; set; } = 3;
}
