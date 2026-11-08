using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace LTools.Native;

/// <summary>剪贴板历史（Win+V）里的一条记录。</summary>
/// <param name="Id">系统给的 GUID，删除时用它定位（值模型没有句柄类型，只能按 id 重新查）。</param>
/// <param name="Kind">text / image / files / link / other。</param>
/// <param name="Text">文本条目的全文；其它类型为空串。</param>
internal sealed record ClipboardHistoryEntry(string Id, string Kind, string Text);

/// <summary>
/// Windows 剪贴板历史（Win+V）的只读 + 删除封装。
/// </summary>
/// <remarks>
/// <para>
/// 用的是 WinRT 的 <see cref="Windows.ApplicationModel.DataTransfer.Clipboard"/>，非打包桌面程序也能调
/// （实测提权进程 <c>GetHistoryItemsAsync</c> 返回 <c>Success</c>）；但**只能删，不能改**——
/// 历史条目是系统快照，想「改」只能删掉再写一条新的。
/// </para>
/// <para>
/// 历史条数上限由系统决定（默认 25 条），用户关掉「剪贴板历史记录」后接口返回
/// <c>AccessDenied</c> / <c>ClipboardDisabled</c>，这里把它翻成能照着做的提示，交给单元抛错。
/// </para>
/// <para>
/// WinRT 的异步方法在这里同步等待：动作线程是后台 MTA 线程，没有同步上下文，不会死锁。
/// </para>
/// </remarks>
internal static class ClipboardHistory
{
    /// <summary>历史是否可用（系统开关打开着）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            return WinClipboard.IsHistoryEnabled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读历史，最新的在前。<paramref name="limit"/> ≤ 0 = 不限。
    /// 失败时 <paramref name="error"/> 给出可直接展示的原因。
    /// </summary>
    public static IReadOnlyList<ClipboardHistoryEntry> List(int limit, out string? error)
    {
        try
        {
            IReadOnlyList<ClipboardHistoryEntry> entries =
                ListAsync(limit).GetAwaiter().GetResult();
            error = null;
            return entries;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Array.Empty<ClipboardHistoryEntry>();
        }
    }

    /// <summary>
    /// 删掉一条历史；<paramref name="id"/> 为空 = 删最新一条。
    /// 返回是否真的删掉了（找不到 / 历史为空都是 false，不算错误）。
    /// </summary>
    public static bool Delete(string id, out string? error)
    {
        try
        {
            bool deleted = DeleteAsync(id).GetAwaiter().GetResult();
            error = null;
            return deleted;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>清空全部历史记录。</summary>
    public static bool Clear(out string? error)
    {
        try
        {
            WinClipboard.ClearHistory();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task<IReadOnlyList<ClipboardHistoryEntry>> ListAsync(int limit)
    {
        ClipboardHistoryItemsResult result = await WinClipboard.GetHistoryItemsAsync();
        EnsureSuccess(result.Status);

        var entries = new List<ClipboardHistoryEntry>();
        foreach (ClipboardHistoryItem item in result.Items)
        {
            if (limit > 0 && entries.Count >= limit) break;
            entries.Add(new ClipboardHistoryEntry(
                item.Id, KindOf(item.Content), await TextOf(item.Content)));
        }

        return entries;
    }

    private static async Task<bool> DeleteAsync(string id)
    {
        ClipboardHistoryItemsResult result = await WinClipboard.GetHistoryItemsAsync();
        EnsureSuccess(result.Status);

        string wanted = (id ?? "").Trim();
        ClipboardHistoryItem? target = wanted.Length == 0
            ? result.Items.FirstOrDefault()
            : result.Items.FirstOrDefault(item =>
                string.Equals(item.Id, wanted, StringComparison.OrdinalIgnoreCase));

        if (target == null) return false;

        WinClipboard.DeleteItemFromHistory(target);
        return true;
    }

    /// <summary>把系统状态翻成用户能照着做的说法；成功什么都不做。</summary>
    private static void EnsureSuccess(ClipboardHistoryItemsResultStatus status)
    {
        if (status == ClipboardHistoryItemsResultStatus.Success) return;

        throw new InvalidOperationException(status switch
        {
            ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled =>
                "剪贴板历史已关闭（设置 → 系统 → 剪贴板 → 打开「剪贴板历史记录」）",
            ClipboardHistoryItemsResultStatus.AccessDenied =>
                "系统拒绝了剪贴板历史的访问（可能被组策略禁用）",
            _ => $"读取剪贴板历史失败（{status}）",
        });
    }

    private static string KindOf(DataPackageView content)
    {
        try
        {
            if (content.Contains(StandardDataFormats.Text)) return "text";
            if (content.Contains(StandardDataFormats.Bitmap)) return "image";
            if (content.Contains(StandardDataFormats.StorageItems)) return "files";
            if (content.Contains(StandardDataFormats.WebLink)) return "link";
        }
        catch
        {
            // 个别条目在服务端已经失效，按「其它」处理，别让整次读取失败。
        }

        return "other";
    }

    private static async Task<string> TextOf(DataPackageView content)
    {
        try
        {
            if (!content.Contains(StandardDataFormats.Text)) return "";
            return await content.GetTextAsync();
        }
        catch
        {
            return "";
        }
    }
}
