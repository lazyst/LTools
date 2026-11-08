using System.IO;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>
/// 「一组路径」的标准输出形状（<c>list</c> / <c>count</c> / <c>first</c> / <c>names</c> / <c>text</c> / <c>found</c>）。
/// </summary>
/// <remarks>
/// 剪贴板文件、选中的文件、资源管理器路径都用它，别再各写一份拼字符串 / 取文件名的代码。
/// 列表输出统一 <c>list</c> + <c>count</c> + 一个文本形式，找不到目标只是 <c>found = false</c>。
/// </remarks>
internal static class PathListOutput
{
    public static void Write(StepContext ctx, IReadOnlyList<string> paths)
    {
        ctx.SetOutput("list", Value.FromList(paths.Select(path => Value.FromText(path))));
        ctx.SetOutput("count", Value.FromNumber(paths.Count));
        ctx.SetOutput("first", Value.FromText(paths.Count > 0 ? paths[0] : ""));
        ctx.SetOutput("names", Value.FromList(paths.Select(path => Value.FromText(NameOf(path)))));
        ctx.SetOutput("text", Value.FromText(string.Join("\n", paths)));
        ctx.SetOutput("found", Value.FromBool(paths.Count > 0));
    }

    /// <summary>取最后一段名字；根目录（<c>C:\</c>）这类取不到名字时原样返回。</summary>
    public static string NameOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return path;

        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }
}
