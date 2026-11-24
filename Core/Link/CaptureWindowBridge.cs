using System;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 截图相关窗口的**执行器侧**：把「开采集历史 / 贴图管理 / 图片编辑 / 切图 / 拼图 / 全屏画布」
/// 以及「这张贴图收到某个快捷键动作」转给普通权限宿主。
/// </summary>
/// <remarks>
/// 这些窗口和它们写的域（<c>captures/</c> <c>pins/</c>）都在宿主里，提权进程这边只转意图。
/// 「开窗」是瞬时动作、不需要答复，所以走事件（与 <c>info.toggle</c> 同一套）；
/// 宿主没连上时返回 <c>false</c>，调用方就地开一份（降级）。
/// </remarks>
internal static class CaptureWindowBridge
{
    /// <summary>把「打开某个截图窗口 / 执行某条贴图命令」的意图交给宿主；<c>false</c> = 没连上。</summary>
    public static bool Show(string target, string? arg = null, long hwnd = 0)
    {
        if (!LinkWorker.IsConnected) return false;

        try
        {
            _ = LinkWorker.NotifyAsync(LinkMethods.EventCaptureWindow, new CaptureWindowEvent(target, arg, hwnd));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"转交截图窗口失败（{target}）：{ex.Message}");
            return false;
        }
    }
}
