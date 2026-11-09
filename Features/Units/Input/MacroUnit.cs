using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>回放录制的键鼠宏。</summary>
public sealed class MacroUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.macro",
        Name = "回放宏",
        Category = "键鼠与输入",
        Icon = "macro",
        Description = "同步回放一个已录制的键鼠宏",
        Inputs = new[]
        {
            new InputSpec("macroId", "宏", ValueKind.text, Required: true),
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
