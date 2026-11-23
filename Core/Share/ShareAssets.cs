using System.IO;

namespace LTools.Core.Share;

/// <summary>浏览器端单页：随包分发的 <c>Assets/Share/index.html</c>，进程内只读一次。</summary>
internal static class ShareAssets
{
    private const string Fallback =
        "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<body style=\"font:14px system-ui;padding:24px\">共享页面缺失，请重新安装 LTools。</body>";

    private static string? _html;

    public static string IndexHtml => _html ??= Load();

    private static string Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Share", "index.html");
            return File.Exists(path) ? File.ReadAllText(path) : Fallback;
        }
        catch
        {
            return Fallback;
        }
    }
}
