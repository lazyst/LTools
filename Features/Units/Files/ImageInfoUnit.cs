using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

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
