using LTools.Core.Actions;
using LTools.Features.Units.Apps;
using LTools.Features.Units.Text;
using LTools.Features.Units.Ui;

namespace LTools.Features.Units;

/// <summary>
/// 随程序分发的功能单元注册入口。
/// </summary>
/// <remarks>
/// 新增单元只需在这里加一行 <c>registry.Register(new XxxUnit())</c>。
/// 流程单元（if / 循环 / 赋值…）由 <see cref="LTools.Core.Actions.FlowUnits"/> 注册。
/// </remarks>
public static class BuiltInUnits
{
    public static void RegisterAll(StepRegistry registry)
    {
        registry.Register(new ToastUnit());
        registry.Register(new ClipboardUnit());
        registry.Register(new LaunchAppUnit());
    }
}
