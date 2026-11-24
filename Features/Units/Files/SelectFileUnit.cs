using System.Windows;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Features;

namespace LTools.Features.Units.Files;

/// <summary>弹出系统的文件 / 文件夹选择框。</summary>
public sealed class SelectFileUnit : IStepUnit
{
    private static readonly string[] Kinds = { "openFile", "saveFile", "folder" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.select",
        Name = "选择文件",
        Category = "文件",
        Icon = "folder",
        Description = "弹出文件 / 文件夹选择框；取消时 found = false",
        Kind = UnitKind.pure,
        // 会弹系统对话框：干跑时跳过。
        DryRun = false,
        Inputs = new[]
        {
            new InputSpec("kind", "选择", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("openFile"), Options: Kinds),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("filter", "文件类型", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.fileFilter | PickerKind.inline | PickerKind.clear,
                Default: Value.FromText("所有文件|*.*")),
            new InputSpec("initialDir", "起始目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, "path", Description: "选中的文件路径；取消时为空"),
            new OutputSpec("found", "已选择", ValueKind.@bool, Description: "是否选了文件"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string kind = args.Text("kind").Trim();
        string title = args.Text("title").Trim();
        string filter = args.Text("filter").Trim();
        string initial = args.Text("initialDir").Trim();

        // 界面归宿主：转过去弹；没连上再本地弹（FilePicker 是唯一实现，两边同一份行为）。
        UiSelectFileReply? remote = UiBridge.TrySelectFile(kind, title, filter, initial, ctx.Token);

        bool ok;
        string path;
        if (remote != null)
        {
            ok = remote.Ok;
            path = remote.Path ?? "";
        }
        else
        {
            (ok, path) = OnUiThread(() => FilePicker.TryPick(kind, title, filter, initial));
        }

        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("found", Value.FromBool(ok));
    }

    private static T OnUiThread<T>(Func<T> work)
    {
        var app = Application.Current;
        if (app == null) return work();
        return app.Dispatcher.Invoke(work);
    }
}

/// <summary>读取文本文件。</summary>
