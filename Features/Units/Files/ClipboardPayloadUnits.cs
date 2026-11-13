using System.IO;
using System.Windows.Media.Imaging;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Files;

/// <summary>剪贴板里的文件列表：读取（CF_HDROP）/ 写入（可剪切）。</summary>
public sealed class ClipboardFilesUnit : IStepUnit
{
    private static readonly string[] Operations = { "read", "write" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "读取文件列表",
        ["write"] = "写入文件列表",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "clipboard.files",
        Name = "剪贴板文件",
        Category = "剪贴板",
        Icon = "paste",
        Description = "读取剪贴板里复制的文件 / 文件夹路径，或把路径放进剪贴板（可剪切）",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("read"), Options: Operations,
                Labels: OperationLabels,
                OptionParams: OptionMap.Of(("write", ["list", "cut"]))),
            new InputSpec("list", "文件列表", ValueKind.list,
                Required: true,
                Description: "要放进剪贴板的文件 / 文件夹路径；用列表变量或每行一个，路径必须存在"),
            new InputSpec("cut", "剪切", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "开启后资源管理器里粘贴是「移动」；关掉是「复制」"),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "文件列表", ValueKind.list, "files",
                Description: "完整路径列表，可直接给「遍历列表」"),
            new OutputSpec("count", "数量", ValueKind.number, Description: "路径个数"),
            new OutputSpec("first", "第一个", ValueKind.text, Description: "首个路径（常用单个文件时省一步）"),
            new OutputSpec("names", "文件名列表", ValueKind.list, Description: "只有文件名（不含目录）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "每行一个路径，方便拼进文本 / 命令"),
            new OutputSpec("cut", "是剪切", ValueKind.@bool, Description: "剪贴板里的是「剪切」而不是「复制」"),
            new OutputSpec("found", "有文件", ValueKind.@bool, Description: "剪贴板里是否有文件列表"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();

        if (operation == "write")
        {
            var paths = (args.Has("list") ? args.List("list") : Array.Empty<Value>())
                .Select(item => item.AsText().Trim())
                .Where(path => path.Length > 0)
                .ToList();

            if (paths.Count == 0)
                throw new ActionException("要放进剪贴板的文件列表是空的");

            foreach (string path in paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new ActionException($"要放进剪贴板的路径不存在：{path}");
            }

            bool cut = args.Bool("cut");
            if (!NativeClipboard.SetFileList(paths, cut))
                throw new ActionException("写入剪贴板失败（可能被其他程序占用）");

            WriteOutputs(ctx, paths, cut);
            return;
        }

        if (operation != "read")
            throw new ActionException($"剪贴板文件操作只能是 read / write（当前 {operation}）");

        bool ok = NativeClipboard.TryGetFileList(out List<string> files, out bool isCut);
        WriteOutputs(ctx, ok ? files : new List<string>(), isCut);
    }

    private static void WriteOutputs(StepContext ctx, IReadOnlyList<string> paths, bool cut)
    {
        PathListOutput.Write(ctx, paths);
        ctx.SetOutput("cut", Value.FromBool(cut));
    }
}

/// <summary>剪贴板里的图片：读取（CF_DIB / PNG → png 文件）/ 写入（png 文件 → 剪贴板）。</summary>
public sealed class ClipboardImageUnit : IStepUnit
{
    private static readonly string[] Operations = { "read", "write" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "读取图片",
        ["write"] = "写入图片",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "clipboard.image",
        Name = "剪贴板图片",
        Category = "剪贴板",
        Icon = "image",
        Description = "把剪贴板里的图片存成 png（或把图片文件放进剪贴板）",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("read"), Options: Operations,
                Labels: OperationLabels,
                OptionParams: OptionMap.Of(("write", ["path"]))),
            new InputSpec("path", "图片文件", Editor: InputEditor.image, Required: true,
                Description: "要写进剪贴板的图片；读取时不用填"),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "图片文件", ValueKind.text, "clipImage",
                Description: "png 路径，可交给「图片信息」「用默认程序打开文件」「写入文件」"),
            new OutputSpec("width", "宽", ValueKind.number, Description: "图片宽度（像素）"),
            new OutputSpec("height", "高", ValueKind.number, Description: "图片高度（像素）"),
            new OutputSpec("found", "有图片", ValueKind.@bool, Description: "剪贴板里是否有图片 / 是否写入成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();

        if (operation == "write")
        {
            string path = ImageLibrary.Resolve(args.Text("path"), ctx.ResourceRoot);
            if (path.Length == 0) throw new ActionException("没有指定图片文件");
            if (!File.Exists(path)) throw new ActionException($"图片文件不存在：{path}");

            BitmapSource frame;
            try
            {
                using var stream = File.OpenRead(path);
                frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
            }
            catch (Exception ex)
            {
                throw new ActionException($"读不出图片（{Path.GetFileName(path)}）：{ex.Message}", ex);
            }

            if (!NativeClipboard.SetImage(frame))
                throw new ActionException("写入剪贴板失败（可能被其他程序占用）");

            WriteOutputs(ctx, path, frame.PixelWidth, frame.PixelHeight, true);
            return;
        }

        if (operation != "read")
            throw new ActionException($"剪贴板图片操作只能是 read / write（当前 {operation}）");

        if (!NativeClipboard.TryGetImage(out BitmapSource? image) || image == null)
        {
            WriteOutputs(ctx, "", 0, 0, false);
            return;
        }

        string file = ImageLibrary.SaveGenerated(image);
        WriteOutputs(ctx, file, image.PixelWidth, image.PixelHeight, true);
    }

    private static void WriteOutputs(StepContext ctx, string path, int width, int height, bool found)
    {
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("width", Value.FromNumber(width));
        ctx.SetOutput("height", Value.FromNumber(height));
        ctx.SetOutput("found", Value.FromBool(found));
    }
}
