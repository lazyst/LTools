namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 一步迁移：只负责一个版本跨度（<see cref="From"/> → <see cref="To"/>）。
/// 必须可重入：中途失败时下一轮从同一个 <see cref="From"/> 重跑，靠文件现状判断。
/// </summary>
internal interface IMigrationStep
{
    /// <summary>适用时的磁盘版本。</summary>
    int From { get; }

    /// <summary>跑完后的目标版本（实际可能一步补齐更多，以 <see cref="ConfigSchema.Detect"/> 为准）。</summary>
    int To { get; }

    /// <summary>日志里显示的名字。</summary>
    string Name { get; }

    void Run(string anchorPath);
}
