using System.Windows;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Views;

namespace LTools.Features.Units.Ui;

/// <summary>弹输入框向用户要一段文本（取消时 found = false）。</summary>
public sealed class UserInputUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.input",
        Name = "获取用户输入",
        Category = "界面",
        Icon = "input",
        Description = "向用户要一段文本（一次一个值；要问几项就多写几步）；取消时 found = false",
        Kind = UnitKind.pure,
        // 会弹模态框：干跑时跳过（问用户不是「解析参数」能算出来的）。
        DryRun = false,
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
        string prompt = args.Text("prompt");
        string title = args.Text("title");
        string defaultValue = args.Text("defaultValue");
        bool multiline = args.Bool("multiline");

        title = title.Length == 0 ? "自动化" : title;
        prompt = prompt.Length == 0 ? "请输入" : prompt;

        // 界面归宿主（普通权限）：转过去弹；没连上就本地弹（体验降级，不卡流程）。
        UiInputReply? remote = UiBridge.TryInput(title, prompt, defaultValue, multiline, ctx.Token);
        bool ok;
        string value;
        if (remote != null)
        {
            ok = remote.Ok;
            value = remote.Text ?? "";
        }
        else
        {
            var app = Application.Current
                ?? throw new ActionException("界面未初始化，无法弹出输入框");

            // 全部在 UI 线程上做：Application.Windows 与对话框都归属 UI 线程（流程跑在后台线程）。
            (ok, value) = app.Dispatcher.Invoke(() =>
            {
                Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
                return InputDialog.Show(owner, title, prompt, defaultValue, variables: null, multiline: multiline);
            });
        }

        ctx.SetOutput("text", Value.FromText(ok ? value : ""));
        ctx.SetOutput("found", Value.FromBool(ok));
    }
}
