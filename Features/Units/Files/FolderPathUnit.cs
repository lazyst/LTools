using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>系统常用文件夹路径（桌面 / 下载 / 文档 / 临时 / AppData…）。</summary>
public sealed class FolderPathUnit : IStepUnit
{
    private static readonly string[] Folders =
    {
        "desktop", "downloads", "documents", "pictures", "music", "videos",
        "temp", "appdata", "localAppData", "programFiles", "programFilesX86",
        "windows", "startup", "userProfile", "dataRoot",
    };

    /// <summary><c>windows</c> 在全局表里是「Windows 自带」（OCR 引擎），这里要改成目录。</summary>
    private static readonly Dictionary<string, string> FolderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows"] = "Windows 目录",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.folderPath",
        Name = "系统文件夹",
        Category = "文件",
        Icon = "folder",
        Description = "取桌面 / 下载 / 文档 / 临时目录 / AppData 等常用文件夹路径",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("folder", "文件夹", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("desktop"), Options: Folders,
                Labels: FolderLabels,
                Description: "要取哪个系统文件夹的路径；「LTools 数据目录」是配置与资源所在处"),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, "folderPath", Description: "文件夹完整路径"),
            new OutputSpec("found", "存在", ValueKind.@bool, Description: "这个文件夹是否存在"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string folder = args.Text("folder").Trim();
        string path = Resolve(folder);
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("found", Value.FromBool(path.Length > 0 && Directory.Exists(path)));
    }

    private static string Resolve(string folder) => folder.ToLowerInvariant() switch
    {
        "desktop" => Special(Environment.SpecialFolder.DesktopDirectory),
        "downloads" => Downloads(),
        "documents" => Special(Environment.SpecialFolder.MyDocuments),
        "pictures" => Special(Environment.SpecialFolder.MyPictures),
        "music" => Special(Environment.SpecialFolder.MyMusic),
        "videos" => Special(Environment.SpecialFolder.MyVideos),
        "temp" => Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
        "appdata" => Special(Environment.SpecialFolder.ApplicationData),
        "localappdata" => Special(Environment.SpecialFolder.LocalApplicationData),
        "programfiles" => Special(Environment.SpecialFolder.ProgramFiles),
        "programfilesx86" => Special(Environment.SpecialFolder.ProgramFilesX86),
        "windows" => Special(Environment.SpecialFolder.Windows),
        "startup" => Special(Environment.SpecialFolder.Startup),
        "userprofile" => Special(Environment.SpecialFolder.UserProfile),
        "dataroot" => AppPaths.DataRoot,
        _ => throw new ActionException(
            $"文件夹只能是 desktop / downloads / documents / pictures / music / videos / " +
            $"temp / appdata / localAppData / programFiles / programFilesX86 / windows / startup / " +
            $"userProfile / dataRoot（当前 {folder}）"),
    };

    private static string Special(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);

    private static string Downloads()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = profile.Length > 0 ? Path.Combine(profile, "Downloads") : "";
        return Directory.Exists(path) ? path : Special(Environment.SpecialFolder.UserProfile);
    }
}

/// <summary>读取图片的宽高、格式与 EXIF 方向 / 拍摄时间。</summary>
