using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Migration;

/// <summary>
/// 动作 → v2 动作体（<see cref="ActionDto.Program"/>）的统一入口。
/// 旧格式按需迁移一次，结果缓存在动作对象上；迁移告警写运行日志，不中断加载。
/// </summary>
public static class ActionPrograms
{
    /// <summary>取动作体；旧格式当场迁移并缓存。</summary>
    public static ActionProgram For(ActionDto action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        if (action.Program != null) return action.Program;

        MigrationResult result = ActionMigrator.Migrate(action);
        foreach (string problem in result.Problems)
            AppLog.Warn("Migration", $"「{action.Name}」：{problem}");

        if (string.IsNullOrWhiteSpace(result.Program.Id))
            result.Program.Id = action.Id ?? "";

        if (string.IsNullOrWhiteSpace(result.Program.Name))
            result.Program.Name = action.Name ?? "";

        if (string.IsNullOrWhiteSpace(result.Program.Icon))
            result.Program.Icon = action.Icon ?? "";

        action.Program = result.Program;

        // 迁移成功就丢掉 v1 字段：动作从此只写 program（失败时保留，便于排查与重试）。
        if (result.Ok) action.ClearLegacy();

        return result.Program;
    }

    /// <summary>补全整份动作清单（配置加载后调用；已迁移的直接跳过）。</summary>
    public static void EnsureAll(IEnumerable<ActionDto> actions)
    {
        foreach (var action in actions)
        {
            if (action == null) continue;
            For(action);
        }
    }

    /// <summary>是否有可执行的步骤（空动作只存在于编辑器里）。</summary>
    public static bool HasSteps(ActionDto action) => For(action).Steps.Count > 0;
}
