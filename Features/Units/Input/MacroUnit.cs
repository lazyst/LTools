using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>回放录制的键鼠宏。</summary>
/// <remarks>
/// 一个宏就是一个「1 步 · 回放宏」的动作（见 <see cref="MacroAction"/>），
/// 所以宏动作在编辑器里打开时看到的也是这一步。
/// </remarks>
public sealed class MacroUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = MacroAction.UnitKey,
        Name = "回放宏",
        Category = "键鼠与输入",
        Icon = "macro",
        Description = "同步回放一个已录制的键鼠宏",
        Inputs = new[]
        {
            new InputSpec("macroId", "宏", ValueKind.text, Required: true,
                Editor: InputEditor.macro,
                Description: "要回放的宏；宏在「设置 → 键鼠宏」里录制、改名、调回放参数"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string id = args.Text("macroId").Trim();
        if (id.Length == 0) throw new ActionException("回放宏没有指定宏");

        var macro = MacroStore.Load(id)
            ?? throw new ActionException($"宏不存在：{id}");

        MacroPlayer.PlaySync(macro);
    }
}
