namespace LTools.Config;

/// <summary>
/// 采集历史的保留策略（<c>configs/settings.json</c> 的 <c>CaptureHistory</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>capture_history/*</c>：开关 / 永久保留 / 保留天数 / 条数上限 / 磁盘上限。
/// 上游还有 <c>compression_level</c>（low / medium / high），本项目历史资产一律存无损 png、
/// 不做二次有损编码，所以不落成开关（见 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </remarks>
public sealed class CaptureHistoryFile
{
    /// <summary>记录新的采集（照上游 <c>capture_history/enabled</c>，默认开）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>永久保留：不再按天数 / 条数 / 磁盘上限裁剪（照上游 <c>keep_permanently</c>）。</summary>
    public bool KeepPermanently { get; set; }

    /// <summary>保留天数（1–365，默认 7）。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>最多保留多少条（1–1000，默认 100）。</summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>磁盘上限 MiB（128–10240，默认 1024）。</summary>
    public int MaxDiskMiB { get; set; } = 1024;
}
