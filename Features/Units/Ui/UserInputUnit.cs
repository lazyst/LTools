using System.Windows;
using LTools.Core.Actions;
using LTools.Views;

namespace LTools.Features.Units.Ui;

/// <summary>弹输入框向用户要一段文本（取消时 found = false）。</summary>
public sealed class UserInputUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.input",
        Name = "输入框",
        Category = "界面",
        Icon = "input",
        Description = "弹出输入框向用户要一段文本；取消时 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("prompt", "提示", ValueKind.text, Default: Value.FromText("请输入")),
            new InputSpec("title", "标题", ValueKind.text, Default: Value.FromText("自动化")),
            new InputSpec("defaultValue", "默认值", ValueKind.text),
            new InputSpec("multiline", "多行", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "输入内容", ValueKind.text, "input", Description: "用户输入的文字；取消时为空"),
            new OutputSpec("found", "已确定", ValueKind.@bool, Description: "是否点了确定"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var app = Application.Current
            ?? throw new ActionException("界面未初始化，无法弹出输入框");

        string prompt = args.Text("prompt");
        string title = args.Text("title");
        string defaultValue = args.Text("defaultValue");
        bool multiline = args.Bool("multiline");

        // 全部在 UI 线程上做：Application.Windows 与对话框都归属 UI 线程（流程跑在后台线程）。
        var (ok, value) = app.Dispatcher.Invoke(() =>
        {
            Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            return InputDialog.Show(owner,
                title.Length == 0 ? "自动化" : title,
                prompt.Length == 0 ? "请输入" : prompt,
                defaultValue, variables: null, multiline: multiline);
        });

        ctx.SetOutput("text", Value.FromText(ok ? value : ""));
        ctx.SetOutput("found", Value.FromBool(ok));
    }
}
