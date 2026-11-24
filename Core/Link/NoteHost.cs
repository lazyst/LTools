using System;
using System.Threading.Tasks;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 写速记的**宿主侧**：<c>速记/</c> 的写者，也是速记窗口的宿主进程。
/// </summary>
/// <remarks>
/// 直接复用 <see cref="NoteActions.Write"/>（唯一的写实现），在链路线程上跑（文件 IO，不占 UI 线程）。
/// 失败原因原样回给执行器，由它变成这一步的错误——不在这里静默吞掉。
/// </remarks>
internal static class NoteHost
{
    /// <summary>处理一条写速记请求；不是该方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request)
    {
        if (request.Method != NoteMethods.Write) return null;

        NoteWriteRequest? req = request.Read<NoteWriteRequest>();
        if (req == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

        try
        {
            VarValue result = NoteActions.Write(req);
            AppLog.Info("Note", $"替主程序写入速记：{result.File}");
            return Task.FromResult(LinkMessage.Response(request,
                new NoteWriteReply(true, result.Title, result.Time, result.File, null)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Note", $"替主程序写速记失败：{ex.Message}");
            return Task.FromResult(LinkMessage.Response(request,
                new NoteWriteReply(false, null, null, null, ex.Message)));
        }
    }
}
