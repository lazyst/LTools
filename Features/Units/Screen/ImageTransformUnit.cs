using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Units.Files;
using OpenCvSharp;

namespace LTools.Features.Units.Screen;

/// <summary>
/// 改图：裁剪 / 缩放 / 旋转 / 灰度 / 翻转 / 转格式（<c>image.transform</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 「截图」只能取图、「找图」只能比对——中间缺的「把图改一改」在这里：截一块当模板、
/// 放大再 OCR、把 png 转成 jpg 发出去，都靠它接上。
/// </para>
/// <para>
/// 不填目标路径时输出到 <c>cache/screenshots/</c>（运行期产物，下次运行开始清空）；
/// 填了就是用户要留下的文件，按填的路径写。走 OpenCV（包里已有），不额外引依赖。
/// </para>
/// </remarks>
public sealed class ImageTransformUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "crop", "resize", "rotate", "grayscale", "flip", "convert" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crop"] = "裁剪",
        ["resize"] = "缩放",
        ["rotate"] = "旋转",
        ["grayscale"] = "转灰度",
        ["flip"] = "翻转",
        ["convert"] = "转格式",
    };

    private static readonly string[] Formats = { "png", "jpg", "bmp", "webp" };
    private static readonly string[] Directions = { "horizontal", "vertical" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "image.transform",
        Name = "图片处理",
        Category = "屏幕",
        Icon = "image",
        Description = "裁剪 / 缩放 / 旋转 / 灰度 / 翻转 / 转格式；留空目标路径时输出到运行期缓存",
        Inputs = new[]
        {
            new InputSpec("image", "图片", ValueKind.text, InputEditor.image, Required: true,
                Description: "要处理的图片（截图步骤的输出、图片文件都行）"),
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("crop"),
                Options: Operations, Labels: OperationLabels),
            new InputSpec("region", "裁剪区域", ValueKind.text, InputEditor.region,
                VisibleWhen: "operation == 'crop'",
                Description: "x,y,w,h；超出图片的部分会被裁掉，留空 = 整张"),
            new InputSpec("width", "宽", ValueKind.number, InputEditor.number,
                VisibleWhen: "operation == 'resize'",
                Description: "目标宽度；宽高只填一个时按比例缩放，都填 0 会报错"),
            new InputSpec("height", "高", ValueKind.number, InputEditor.number,
                VisibleWhen: "operation == 'resize'",
                Description: "目标高度"),
            new InputSpec("angle", "角度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(90), VisibleWhen: "operation == 'rotate'",
                Description: "90 / 180 / 270 走快路径；其它角度按中心旋转，画布大小不变"),
            new InputSpec("direction", "方向", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("horizontal"), Options: Directions,
                Labels: new Dictionary<string, string>
                {
                    ["horizontal"] = "左右镜像",
                    ["vertical"] = "上下翻转",
                },
                VisibleWhen: "operation == 'flip'"),
            new InputSpec("format", "格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("png"), Options: Formats,
                VisibleWhen: "operation == 'convert'",
                Description: "jpg 不支持透明，透明区域会变黑"),
            new InputSpec("quality", "质量", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(90), VisibleWhen: "operation == 'convert'", Advanced: true,
                Description: "只对 jpg / webp 有意义（1～100）"),
            new InputSpec("target", "输出文件", ValueKind.text, InputEditor.file,
                Description: "写到哪里；留空 = cache/screenshots（下次运行开始清空）"),
        },
        Outputs = new[]
        {
            new OutputSpec("file", "图片路径", ValueKind.text, "imageFile",
                Description: "处理后的图片文件（绝对路径），可以直接给 OCR / 找图 / 剪贴板用"),
            new OutputSpec("width", "宽", ValueKind.number, Description: "处理后图片的像素宽"),
            new OutputSpec("height", "高", ValueKind.number, Description: "处理后图片的像素高"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = ImageLibrary.Resolve(ScreenArgs.ImagePath(args), ctx.ResourceRoot);
        if (!File.Exists(path)) throw new ActionException($"图片不存在：{path}");

        string op = args.Text("operation").Trim().ToLowerInvariant();

        using Mat source = Load(path);
        using Mat result = Transform(source, op, args);
        if (result.Empty()) throw new ActionException($"处理后是空图（{op}）");

        string target = args.Text("target").Trim();
        string output = target.Length > 0
            ? FileText.Resolve(target, "")
            : NextCapturePath(op == "convert" ? args.Text("format") : "png");

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        Save(result, output, (int)Math.Clamp(args.Number("quality"), 1, 100));

        ctx.SetOutput("file", Value.FromText(output));
        ctx.SetOutput("width", Value.FromNumber(result.Width));
        ctx.SetOutput("height", Value.FromNumber(result.Height));
    }

    private static Mat Transform(Mat source, string op, StepArgs args)
    {
        switch (op)
        {
            case "crop":
            {
                int[]? rect = UnitValues.ParseRect(Value.FromText(args.Text("region")));
                if (rect == null) return source.Clone();

                var (x, y, w, h) = Clamp(rect, source.Width, source.Height);
                if (w <= 0 || h <= 0) throw new ActionException("裁剪区域和图片没有交集");
                return new Mat(source, new Rect(x, y, w, h)).Clone();
            }

            case "resize":
            {
                // width / height 没有默认值：没填就是 Value.Empty，不能直接 Number（会报「」不是数字）。
                int width = args.Has("width") ? (int)Math.Round(args.Number("width")) : 0;
                int height = args.Has("height") ? (int)Math.Round(args.Number("height")) : 0;
                if (width <= 0 && height <= 0)
                    throw new ActionException("宽和高至少要填一个（另一个留 0 表示按比例）");

                if (width <= 0) width = (int)Math.Round(source.Width * (double)height / source.Height);
                if (height <= 0) height = (int)Math.Round(source.Height * (double)width / source.Width);
                width = Math.Max(1, width);
                height = Math.Max(1, height);

                var dst = new Mat();
                // 缩小用 Area（不产生摩尔纹），放大用 Cubic（边缘不糊）。
                var flags = width * height < source.Width * source.Height
                    ? InterpolationFlags.Area
                    : InterpolationFlags.Cubic;
                Cv2.Resize(source, dst, new OpenCvSharp.Size(width, height), 0, 0, flags);
                return dst;
            }

            case "rotate":
            {
                double angle = args.Number("angle");
                int normalized = ((int)Math.Round(angle) % 360 + 360) % 360;
                var dst = new Mat();

                if (normalized is 90 or 180 or 270)
                {
                    Cv2.Rotate(source, dst, normalized switch
                    {
                        90 => RotateFlags.Rotate90Clockwise,
                        180 => RotateFlags.Rotate180,
                        _ => RotateFlags.Rotate90Counterclockwise,
                    });
                    return dst;
                }

                var center = new Point2f(source.Width / 2f, source.Height / 2f);
                using Mat matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
                Cv2.WarpAffine(source, dst, matrix, source.Size(),
                    InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                return dst;
            }

            case "grayscale":
            {
                var dst = new Mat();
                Cv2.CvtColor(source, dst, source.Channels() == 4
                    ? ColorConversionCodes.BGRA2GRAY
                    : ColorConversionCodes.BGR2GRAY);
                return dst;
            }

            case "flip":
            {
                var dst = new Mat();
                bool vertical = args.Text("direction").Trim().Equals("vertical", StringComparison.OrdinalIgnoreCase);
                Cv2.Flip(source, dst, vertical ? FlipMode.X : FlipMode.Y);
                return dst;
            }

            case "convert":
                return source.Clone();

            default:
                throw new ActionException($"未知操作：{op}");
        }
    }

    /// <summary>把裁剪矩形夹进图片范围（截图边缘框选常见差一两个像素）。</summary>
    private static (int X, int Y, int W, int H) Clamp(int[] rect, int width, int height)
    {
        int x = Math.Clamp(rect[0], 0, width);
        int y = Math.Clamp(rect[1], 0, height);
        int w = Math.Min(rect[2], width - x);
        int h = Math.Min(rect[3], height - y);
        return (x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private static string NextCapturePath(string format)
    {
        Directory.CreateDirectory(ImageLibrary.CaptureDir);
        string ext = format.ToLowerInvariant() is "jpg" or "bmp" or "webp" ? format.ToLowerInvariant() : "png";
        string stem = $"edit-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(ImageLibrary.CaptureDir, $"{stem}.{ext}");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(ImageLibrary.CaptureDir, $"{stem}-{n}.{ext}");
        return path;
    }

    /// <summary>读图：优先 <c>ImRead</c>，失败再按字节解码（中文路径下 OpenCV 的 ANSI 读法会失手）。</summary>
    private static Mat Load(string path)
    {
        var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (!mat.Empty()) return mat;

        mat.Dispose();
        try
        {
            return Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
        }
        catch (Exception ex) when (ex is IOException or OpenCVException)
        {
            throw new ActionException($"读不了这张图：{path}", ex);
        }
    }

    private static void Save(Mat mat, string path, int quality)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool lossy = ext is ".jpg" or ".jpeg";

        Mat? converted = null;
        Mat output = mat;
        if (lossy && mat.Channels() == 4)
        {
            converted = new Mat();
            Cv2.CvtColor(mat, converted, ColorConversionCodes.BGRA2BGR);
            output = converted;
        }

        try
        {
            int[] parameters = lossy
                ? new[] { (int)ImwriteFlags.JpegQuality, quality }
                : ext == ".webp" ? new[] { (int)ImwriteFlags.WebPQuality, quality } : Array.Empty<int>();

            try
            {
                if (Cv2.ImWrite(path, output, parameters)) return;
            }
            catch (OpenCVException)
            {
                // 落到下面的按字节写（中文路径同样可能让 ImWrite 失手）。
            }

            Cv2.ImEncode(ext.Length > 1 ? ext : ".png", output, out byte[] buffer, parameters);
            File.WriteAllBytes(path, buffer);
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
