using LTools.Core.Actions;
using LTools.Features.Units.Apps;
using LTools.Features.Units.Data;
using LTools.Features.Units.Files;
using LTools.Features.Units.Input;
using LTools.Features.Units.Internal;
using LTools.Features.Units.Screen;
using LTools.Features.Units.Text;
using LTools.Features.Units.Ui;
using LTools.Features.Units.Windows;

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
        // —— 界面 / 文本 ——
        registry.Register(new ToastUnit());
        registry.Register(new UserInputUnit());
        registry.Register(new ClipboardUnit());
        registry.Register(new SelectionUnit());
        registry.Register(new SendTextUnit());
        registry.Register(new ProcessTextUnit());

        // —— 文件 ——
        registry.Register(new OpenPathUnit());
        registry.Register(new SelectFileUnit());
        registry.Register(new ReadFileUnit());
        registry.Register(new WriteFileUnit());
        registry.Register(new FileOperationUnit());
        registry.Register(new PathInfoUnit());
        registry.Register(new ExplorerUnit());

        // —— 程序 ——
        registry.Register(new LaunchAppUnit());
        registry.Register(new RunCommandUnit());
        registry.Register(new ProcessUnit());

        // —— 输入 ——
        registry.Register(new SendKeysUnit());
        registry.Register(new MouseUnit());
        registry.Register(new CursorUnit());
        registry.Register(new ClickTextUnit());
        registry.Register(new ClickImageUnit());
        registry.Register(new MacroUnit());

        // —— 窗口 ——
        registry.Register(new FindWindowUnit());
        registry.Register(new ForegroundWindowUnit());
        registry.Register(new OperateWindowUnit());

        // —— 屏幕 ——
        registry.Register(new CaptureScreenUnit());
        registry.Register(new FindImageUnit());
        registry.Register(new OcrUnit());
        registry.Register(new FindTextUnit());
        registry.Register(new PixelUnit());

        // —— 数据 ——
        registry.Register(new NoteWriteUnit());
        registry.Register(new NoteReadUnit());
        registry.Register(new ListUnit());
        registry.Register(new CalcUnit());
        registry.Register(new TimeUnit());

        // —— 内部 ——
        registry.Register(new InvokeInternalUnit());
        registry.Register(new LastErrorUnit());
    }
}
