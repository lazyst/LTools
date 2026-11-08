using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>生成一个还没被占用的临时文件路径（不创建文件）。</summary>
public sealed class TempPathUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.tempPath",
        Name = "临时文件路径",
        Category = "文件",
        Icon = "save",
        Description = "生成一个临时文件路径（只给路径，不创建文件）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("ext", "扩展名", ValueKind.text,
                Default: Value.FromText(".tmp"),
                Description: "例如 .txt / .png；不填就是 .tmp（只生成路径，不创建文件）"),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, "tempPath", Description: "临时目录下的完整路径"),
            new OutputSpec("folder", "临时目录", ValueKind.text, Description: "系统临时目录"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string folder = Path.GetTempPath();
        string ext = NormalizeExtension(args.Text("ext"));

        string path;
        do
        {
            path = Path.Combine(folder, $"ltools-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}" + ext);
        }
        while (File.Exists(path) || Directory.Exists(path));

        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("folder", Value.FromText(folder));
    }

    private static string NormalizeExtension(string raw)
    {
        string ext = (raw ?? "").Trim();
        if (ext.Length == 0) return ".tmp";
        if (ext.Contains('\\') || ext.Contains('/') || ext.Contains(':'))
            throw new ActionException($"扩展名里不能带路径分隔符（当前 {ext}）");
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}

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
public sealed class ImageInfoUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "image.info",
        Name = "图片信息",
        Category = "文件",
        Icon = "image",
        Description = "读取图片的宽高、格式与 EXIF 方向 / 拍摄时间",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "图片文件", Editor: InputEditor.image, Required: true,
                Description: "png / jpg / bmp / gif / tiff；可写 ${变量}"),
        },
        Outputs = new[]
        {
            new OutputSpec("width", "宽", ValueKind.number, Description: "像素宽度"),
            new OutputSpec("height", "高", ValueKind.number, Description: "像素高度"),
            new OutputSpec("format", "格式", ValueKind.text, Description: "png / jpeg / bmp / gif / tiff / ico"),
            new OutputSpec("orientation", "EXIF 方向", ValueKind.number,
                Description: "1 = 正常；3 = 旋转 180°；6 = 顺时针 90°；8 = 逆时针 90°"),
            new OutputSpec("exifDate", "拍摄时间", ValueKind.text, Description: "EXIF 里的原始时间，没有就是空"),
            new OutputSpec("found", "读到了", ValueKind.@bool, Description: "文件存在且能解码"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = ImageLibrary.Resolve(args.Text("path"), ctx.ResourceRoot);

        int width = 0, height = 0, orientation = 1;
        string format = "", exifDate = "";
        bool found = false;

        if (path.Length > 0 && File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                found = true;

                if (frame.Metadata is BitmapMetadata metadata)
                {
                    object? tag = Query(metadata, "/app1/ifd/{ushort=274}");
                    if (tag is ushort us) orientation = us;
                    else if (tag is int value) orientation = value;

                    object? taken = Query(metadata, "/app1/ifd/exif/{ushort=36867}")
                                    ?? Query(metadata, "/app1/ifd/{ushort=306}");
                    exifDate = taken as string ?? "";
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Image", $"读图片信息失败（{path}）：{ex.Message}");
                found = false;
            }
        }

        if (format.Length == 0)
            format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        ctx.SetOutput("width", Value.FromNumber(width));
        ctx.SetOutput("height", Value.FromNumber(height));
        ctx.SetOutput("format", Value.FromText(format));
        ctx.SetOutput("orientation", Value.FromNumber(orientation));
        ctx.SetOutput("exifDate", Value.FromText(exifDate));
        ctx.SetOutput("found", Value.FromBool(found));
    }

    private static object? Query(BitmapMetadata metadata, string query)
    {
        try { return metadata.GetQuery(query); }
        catch { return null; }
    }
}

/// <summary>算文件的哈希（MD5 / SHA-1 / SHA-256 / CRC32）。</summary>
public sealed class FileHashUnit : IStepUnit
{
    private static readonly string[] Algorithms = { "md5", "sha1", "sha256", "crc32" };

    private static readonly Dictionary<string, string> AlgorithmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md5"] = "MD5",
        ["sha1"] = "SHA-1",
        ["sha256"] = "SHA-256",
        ["crc32"] = "CRC32",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.hash",
        Name = "文件哈希",
        Category = "文件",
        Icon = "search",
        Description = "算文件的 MD5 / SHA-1 / SHA-256 / CRC32（流式读取，大文件也行）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "文件", ValueKind.text, InputEditor.file, Required: true,
                Description: "要算哈希的文件；可写 ${变量}"),
            new InputSpec("algorithm", "算法", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("sha256"), Options: Algorithms, Labels: AlgorithmLabels,
                Description: "MD5 / SHA-1 / SHA-256 / CRC32（CRC32 与 ZIP 一致）"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "「文件」填相对路径时的基准目录；填了绝对路径就不用管"),
        },
        Outputs = new[]
        {
            new OutputSpec("hash", "哈希", ValueKind.text, "hash", Description: "小写十六进制"),
            new OutputSpec("algorithm", "算法", ValueKind.text, Description: "实际使用的算法"),
            new OutputSpec("size", "字节数", ValueKind.number, Description: "文件大小"),
            new OutputSpec("found", "读到了", ValueKind.@bool, Description: "文件是否存在且读取成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        string algorithm = args.Text("algorithm").Trim().ToLowerInvariant();
        if (Array.IndexOf(Algorithms, algorithm) < 0)
            throw new ActionException($"算法只能是 md5 / sha1 / sha256 / crc32（当前 {algorithm}）");

        if (!File.Exists(path))
        {
            ctx.SetOutput("hash", Value.FromText(""));
            ctx.SetOutput("algorithm", Value.FromText(algorithm));
            ctx.SetOutput("size", Value.FromNumber(0));
            ctx.SetOutput("found", Value.FromBool(false));
            return;
        }

        long size;
        string hash;
        try
        {
            using var stream = File.OpenRead(path);
            size = stream.Length;
            hash = algorithm == "crc32" ? Crc32.Compute(stream) : Hashed(stream, algorithm);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActionException($"读不了文件（{Path.GetFileName(path)}）：{ex.Message}", ex);
        }

        ctx.SetOutput("hash", Value.FromText(hash));
        ctx.SetOutput("algorithm", Value.FromText(algorithm));
        ctx.SetOutput("size", Value.FromNumber(size));
        ctx.SetOutput("found", Value.FromBool(true));
    }

    private static string Hashed(Stream stream, string algorithm)
    {
        using HashAlgorithm hasher = algorithm switch
        {
            "md5" => MD5.Create(),
            "sha1" => SHA1.Create(),
            _ => SHA256.Create(),
        };
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }
}

/// <summary>CRC-32（IEEE 802.3，与 ZIP / Quicker 的 crc32 一致）。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static string Compute(Stream stream)
    {
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        }

        return (~crc).ToString("x8", CultureInfo.InvariantCulture);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
