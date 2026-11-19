using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 「上次用过的绘图工具」的记忆（<c>configs/ui.json</c> 的 <c>CaptureLastTool</c>）。
/// </summary>
/// <remarks>
/// 照上游 <c>ScreenshotToolbarSettings().lastDrawingTool()</c>：只有
/// <see cref="DrawingSettings.RememberLastTool"/> 打开时，新开的截图会话 / 图片编辑窗
/// 才从这个工具开始（否则从「选择」开始）。
/// 只在工具真的变了时写一次 <c>ui.json</c>（一次工具点击一次原子写，不防抖——不像拖滑块那样高频）。
/// </remarks>
internal static class CaptureToolMemory
{
    private static readonly object Gate = new();

    private static string? _last;
    private static bool _loaded;

    /// <summary>这次会话从哪个工具开始：关着「记住上次工具」时一律 <paramref name="fallback"/>。</summary>
    public static CaptureTool Start(CaptureTool fallback)
    {
        if (!DrawingSettings.RememberLastTool) return fallback;
        return CaptureToolIds.Parse(Last()) ?? fallback;
    }

    /// <summary>记下这次用的工具（不是那 16 个可记忆的工具就只保留旧值）。</summary>
    public static void Remember(CaptureTool tool)
    {
        if (CaptureToolIds.Of(tool) is not { } id) return;
        if (string.Equals(Last(), id, StringComparison.Ordinal)) return;

        lock (Gate) _last = id;
        Write(id);
    }

    /// <summary>导入备份 / 冒烟切配置根之后丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _last = null;
            _loaded = false;
        }
    }

    private static string? Last()
    {
        lock (Gate)
        {
            if (_loaded) return _last;
        }

        string? value = null;
        try
        {
            string path = ConfigStore.ConfigPath;
            if (!string.IsNullOrEmpty(path)) value = UiStore.Read(path).CaptureLastTool;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.CaptureToolMemory", "读取上次工具失败：" + ex.Message);
        }

        lock (Gate)
        {
            _last = value;
            _loaded = true;
            return value;
        }
    }

    private static void Write(string id)
    {
        try
        {
            void Change(UiStateFile ui) => ui.CaptureLastTool = id;
            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) UiStore.Modify(Change);
            else UiStore.Modify(ConfigStore.ConfigPath, Change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.CaptureToolMemory", "保存上次工具失败：" + ex.Message);
        }
    }
}
