namespace LTools.Core.Actions;

/// <summary>一步运行得太久的信号（<see cref="ActionRunOptions.OnSlowStep"/>）。</summary>
/// <param name="Path">步骤路径（<c>/steps/2/then/0</c>），同一行循环多轮也只报一次。</param>
/// <param name="Label">给人看的位置（<c>第 3 行 › 否则 › 第 1 行</c>）。</param>
/// <param name="UnitKey">动作单元键。</param>
/// <param name="UnitName">动作单元名（提示文案里用）。</param>
/// <param name="Elapsed">从这一步开始到回调时的时长。</param>
public sealed record SlowStepInfo(string Path, string Label, string UnitKey, string UnitName, TimeSpan Elapsed);
