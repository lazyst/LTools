using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>
/// 拆解 / 拼接路径（<c>path.parts</c>）：目录、文件名、主名、扩展名、根。
/// </summary>
/// <remarks>
/// <para>
/// 「路径信息」（<c>file.exists</c>）问的是磁盘上的东西存不存在；这里只按字符串算，
/// 不碰磁盘——处理剪贴板里粘来的路径、给批量改名拼新文件名时用它。
/// </para>
/// <para>
/// 扩展名<b>不带点</b>（<c>txt</c>），因为拼 <c>a.{ext}</c> / 填文件筛选器时都是这个形状。
/// 解析不了（空串、非法字符）时 <c>found = false</c>，不报错——路径可能来自变量，先判再用。
/// </para>
/// </remarks>
public sealed class PathPartsUnit : IStepUnit
{
    private static readonly string[] Operations = { "parse", "combine" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parse"] = "拆解路径",
        ["combine"] = "拼接路径",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "path.parts",
        Name = "拆解路径",
        Category = "文件",
        Icon = "link",
        Description = "把路径拆成目录 / 文件名 / 主名 / 扩展名 / 根，或把目录和文件名拼起来（不碰磁盘）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("parse"), Options: Operations, Labels: OperationLabels),
            new InputSpec("path", "路径", ValueKind.text, InputEditor.text,
                VisibleWhen: "operation == 'parse'",
                Description: "要拆的路径；相对路径原样拆，不会解析成绝对路径"),
            new InputSpec("dir", "目录", ValueKind.text, InputEditor.folder,
                VisibleWhen: "operation == 'combine'",
                Description: "拼在前面的一段；留空 = 只用文件名"),
            new InputSpec("name", "文件名", ValueKind.text,
                VisibleWhen: "operation == 'combine'",
                Description: "文件名（可以带子目录，如 sub\\a.txt）"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "完整路径", ValueKind.text, "path",
                Description: "拆解：输入原样；拼接：拼好的路径"),
            new OutputSpec("dir", "目录", ValueKind.text,
                Description: "所在目录（没有目录部分时为空）"),
            new OutputSpec("name", "文件名", ValueKind.text,
                Description: "含扩展名的文件名"),
            new OutputSpec("stem", "主名", ValueKind.text,
                Description: "去掉扩展名的文件名"),
            new OutputSpec("ext", "扩展名", ValueKind.text,
                Description: "不带点（txt）；没有扩展名为空"),
            new OutputSpec("root", "根", ValueKind.text,
                Description: "盘符或 UNC 根（C:\\、\\\\server\\share）；相对路径为空"),
            new OutputSpec("found", "解析成功", ValueKind.@bool,
                Description: "路径为空或格式非法时为假（不报错）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();

        string path;
        if (op == "combine")
        {
            string dir = args.Text("dir").Trim();
            string name = args.Text("name").Trim();
            if (name.Length == 0) throw new ActionException("文件名不能为空");

            try
            {
                path = dir.Length == 0 ? name : Path.Combine(dir, name);
            }
            catch (ArgumentException ex)
            {
                throw new ActionException($"拼不出合法路径：{ex.Message}", ex);
            }
        }
        else
        {
            path = args.Text("path").Trim();
        }

        ctx.SetOutput("text", Value.FromText(path));

        if (path.Length == 0)
        {
            SetParts(ctx, "", "", "", "", "", false);
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path).TrimStart('.');
            string root = Path.GetPathRoot(path) ?? "";
            bool ok = name.Length > 0 || dir.Length > 0;
            SetParts(ctx, dir, name, stem, ext, root, ok);
        }
        catch (ArgumentException)
        {
            // 含 < > | 之类非法字符：路径来自变量时不该炸流程，交给 found 判断。
            SetParts(ctx, "", "", "", "", "", false);
        }
    }

    private static void SetParts(StepContext ctx, string dir, string name, string stem,
        string ext, string root, bool found)
    {
        ctx.SetOutput("dir", Value.FromText(dir));
        ctx.SetOutput("name", Value.FromText(name));
        ctx.SetOutput("stem", Value.FromText(stem));
        ctx.SetOutput("ext", Value.FromText(ext));
        ctx.SetOutput("root", Value.FromText(root));
        ctx.SetOutput("found", Value.FromBool(found));
    }
}
