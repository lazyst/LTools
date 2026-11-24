using System;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 写速记的**执行器侧**：<c>速记/</c> 的写者是普通权限宿主，动作单元把这条写请求转过去。
/// </summary>
/// <remarks>
/// <para>
/// 返回 <c>null</c> = **没转交**（宿主没起 / 没连上），调用方照旧本地写——与配置写入同一个降级口径。
/// </para>
/// <para>
/// 转出去了但宿主报错时**直接抛错、不回退**：宿主可能已经写了一半（新建了文件、复制了图片），
/// 再本地写一遍会多出一条速记 / 重复的图片。
/// </para>
/// </remarks>
internal static class NoteBridge
{
    /// <summary>写图片附件时可能拷几 MB，给足时间。</summary>
    private const int WriteTimeoutMs = 60_000;

    /// <summary>把「写一条速记」交给宿主；<c>null</c> = 没转交。</summary>
    public static NoteWriteReply? TryWrite(NoteWriteRequest request, System.Threading.CancellationToken token)
    {
        if (ConfigStore.ReadOnly) return null;              // 宿主自己就是写者
        if (!LinkWorker.IsConnected) return null;

        try
        {
            LinkMessage reply = LinkWorker.RequestAsync(NoteMethods.Write, request, WriteTimeoutMs, token)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
                throw new ActionException($"宿主没能写速记：{reply.Error}");

            NoteWriteReply? result = reply.Read<NoteWriteReply>()
                ?? throw new ActionException("宿主没有回速记的写入结果");

            if (!result.Ok)
                throw new ActionException($"写速记失败：{result.Error}");

            return result;
        }
        catch (OperationCanceledException)
        {
            throw new ActionAbortedException("已停止", userCancelled: true);
        }
        catch (ActionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ActionException($"写速记的请求失败：{ex.Message}");
        }
    }
}
