using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Native;
using ModelContextProtocol.Protocol;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具的实际实现。所有工具共用一个回执形状与审计入口，避免每个工具各写一份 try/catch。
/// </summary>
internal static partial class AiToolHandlers
{
    // ============ 截图 ============

    /// <summary>
    /// 截一张屏幕图：落盘到草稿 <c>files/</c>（或 <c>resources/images/</c>），并把图片**本身**附在回执里。
    /// </summary>
    /// <remarks>
    /// 返回 <see cref="CallToolResult"/> 而不是 JSON 文本，是为了带一个 <c>image</c> 内容块：
    /// agent 看不见用户的屏幕（A4），不给它图就只能瞎猜坐标，也做不出「找图」的模板图。
    /// 写入草稿与 <c>staging_write</c> 同口径（只要 <c>allowWrites</c>，不弹确认框）——
    /// 截的图只是草稿文件，要用户采纳才会进正式动作。
    /// </remarks>
    public static CallToolResult Screenshot(string? draftId, string? source, string? region,
        string? name, int maxWidth)
    {
        const string Tool = "ltools_screenshot";
        long started = Environment.TickCount64;
        try
        {
            RequireWrites();
            var shot = ScreenshotPayload(draftId, source, region, name, maxWidth);
            AiAudit.Write(Tool, draftId, "ok", Environment.TickCount64 - started);
            return new CallToolResult
            {
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = Serialize(shot.Payload) },
                    ImageContentBlock.FromBytes(shot.Thumbnail, "image/png"),
                },
            };
        }
        catch (Exception ex)
        {
            var (code, message) = Describe(ex, Tool);
            AiAudit.Write(Tool, draftId, code, Environment.TickCount64 - started);
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = Error(code, message) } },
            };
        }
    }

    private sealed record Shot(Dictionary<string, object?> Payload, byte[] Thumbnail);

    private static Shot ScreenshotPayload(string? draftId, string? source, string? region,
        string? name, int maxWidth)
    {
        string id = (draftId ?? "").Trim();
        if (id.Length > 0 && DraftStore.Find(id) == null)
            throw new AiToolException("not_found", $"草稿不存在：{id}");

        using var frame = AiScreen.Capture(source, region);

        byte[] full = AiScreen.EncodePng(frame.Bitmap);
        var (thumbnail, thumbWidth, thumbHeight) = AiScreen.Thumbnail(frame.Bitmap, maxWidth);

        string fileName = AiScreen.SafeFileName(name);
        string stored;
        string fullPath;
        string? revision = null;

        if (id.Length > 0)
        {
            // 写草稿 files/：路径写成 files/x.png，找图 / 图片点击直接用这个字面量，不用绝对路径。
            fullPath = DraftStore.WriteFile(id, fileName, full);
            stored = "files/" + fileName;
            revision = DraftStore.ComputeRevision(id);
        }
        else
        {
            fullPath = AiScreen.SaveToImages(full, fileName);
            stored = ImageLibrary.ToStoredPath(fullPath);
        }

        return new Shot(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["source"] = string.IsNullOrWhiteSpace(source) ? "screen" : source!.Trim().ToLowerInvariant(),
            ["file"] = stored,
            ["path"] = fullPath,
            ["bytes"] = full.Length,
            ["width"] = frame.Width,
            ["height"] = frame.Height,
            ["bounds"] = new Dictionary<string, object?>
            {
                ["left"] = frame.Left,
                ["top"] = frame.Top,
                ["width"] = frame.Width,
                ["height"] = frame.Height,
            },
            ["thumbnail"] = new Dictionary<string, object?>
            {
                ["width"] = thumbWidth,
                ["height"] = thumbHeight,
                ["scale"] = frame.Width == thumbWidth ? 1d
                    : Math.Round(thumbWidth / (double)frame.Width, 4),
            },
            ["coords"] = "屏幕物理像素 = 图片像素 ÷ thumbnail.scale + bounds.left/top",
            ["draftId"] = id.Length > 0 ? id : null,
            ["revision"] = revision,
        }, thumbnail);
    }

    // ============ 控件树 ============

    /// <summary>读一个窗口的 UI Automation 控件树快照（只读，不改任何东西）。</summary>
    public static Dictionary<string, object?> UiaTree(string? title, string? className, int depth, int limit) =>
        AiUia.Snapshot(title, className, depth, limit);

}
