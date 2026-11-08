using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

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
        Inputs = new[]
        {
            new InputSpec("kind", "选择", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("openFile"), Options: Kinds),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("filter", "文件类型", ValueKind.text,
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

        var (ok, path) = OnUiThread(() =>
        {
            Window? owner = Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

            if (string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase))
            {
                var dialog = new OpenFolderDialog();
                if (title.Length > 0) dialog.Title = title;
                if (initial.Length > 0) dialog.InitialDirectory = initial;
                bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
                return (picked, picked ? dialog.FolderName : "");
            }

            if (string.Equals(kind, "saveFile", StringComparison.OrdinalIgnoreCase))
            {
                var dialog = new SaveFileDialog
                {
                    Title = title.Length > 0 ? title : "保存文件",
                    Filter = filter.Length > 0 ? filter : "所有文件|*.*",
                };
                if (initial.Length > 0) dialog.InitialDirectory = initial;
                bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
                return (picked, picked ? dialog.FileName : "");
            }

            var open = new OpenFileDialog
            {
                Title = title.Length > 0 ? title : "选择文件",
                Filter = filter.Length > 0 ? filter : "所有文件|*.*",
            };
            if (initial.Length > 0) open.InitialDirectory = initial;
            bool okOpen = owner == null ? open.ShowDialog() == true : open.ShowDialog(owner) == true;
            return (okOpen, okOpen ? open.FileName : "");
        });

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
