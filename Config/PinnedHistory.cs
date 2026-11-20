namespace LTools.Config;

/// <summary>
/// 贴图历史的保留策略（<c>configs/settings.json</c> 的 <c>PinnedHistory</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>pinned_history/*</c>。默认值与上游不同（LTools 沿用既有的 30 天 / 200 条，
/// 避免升级时把用户已有的贴图历史删掉；上游默认 7 天 / 100 条），可在设置页改。
/// 上游的 <c>compression_level</c> 不落开关：贴图资产一律无损 png（见 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </remarks>
public sealed class PinnedHistoryFile
{
    /// <summary>记录贴图历史（关掉 = 贴图关掉之后不留记录，管理页也看不到）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>永久保留：不按天数 / 条数 / 磁盘上限裁剪。</summary>
    public bool KeepPermanently { get; set; }

    /// <summary>保留天数（1–365，默认 30）。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>最多保留多少条（1–1000，默认 200）。</summary>
    public int MaxEntries { get; set; } = 200;

    /// <summary>磁盘上限 MiB（128–10240，默认 1024）。</summary>
    public int MaxDiskMiB { get; set; } = 1024;
}
