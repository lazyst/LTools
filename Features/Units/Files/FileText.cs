using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>文件文本读写的编码选择与路径解析。</summary>
internal static class FileText
{
    public static readonly string[] Encodings = { "utf8", "utf8bom", "unicode", "gbk", "auto" };

    public static string Resolve(string path, string workdir)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) throw new ActionException("文件路径为空");

        string wd = AppPaths.ResolveWorkdir(workdir);
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(wd, path));
    }

    public static string Read(string path, string encoding) => File.ReadAllText(path, GetEncoding(encoding));

    public static void Write(string path, string text, string encoding, bool append)
    {
        var enc = GetEncoding(encoding);
        if (append) File.AppendAllText(path, text, enc);
        else File.WriteAllText(path, text, enc);
    }

    private static Encoding GetEncoding(string name)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "utf8bom":
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            case "unicode":
                return Encoding.Unicode;
            case "gbk":
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(936);
                }
                catch
                {
                    return Encoding.UTF8;
                }
            case "auto":
                // UTF-8 读取时 StreamReader 会按 BOM 自动切换（UTF-8 / UTF-16）。
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            default:
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }
}
