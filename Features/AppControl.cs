using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 「通用」页的开机自启开关：**唯一 UI 入口**（宿主 → 执行器，执行器 → <see cref="AutoStartService"/>）。
/// </summary>
/// <remarks>
/// 任务计划要 <c>RunLevel=HighestAvailable</c>（普通权限下创建会被拒），注册的又是执行器的
/// <c>LTools.exe</c>——所以这件事只能由提权进程做。返回 null 表示「没做成 / 问不到」，
/// <c>error</c> 是能直接显示给用户的原因；调用方据此把复选框摆回去，别假装成功。
/// </remarks>
internal static class AppControl
{
    /// <summary>查询自启状态（任务是否存在 / 是否指向别的拷贝）。</summary>
    public static AutoStartInfo? Query(out string? error)
    {
        error = null;
        if (AppRole.IsUiHost) return AppBridge.Query(out error);

        try { return AutoStartService.Query(); }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>启用 / 禁用；成功返回做完之后的任务现状。</summary>
    public static AutoStartInfo? SetEnabled(bool enable, out string? error)
    {
        error = null;

        if (AppRole.IsUiHost) return AppBridge.SetEnabled(enable, out error);

        try
        {
            bool ok = enable ? AutoStartService.Enable() : AutoStartService.Disable();
            if (!ok)
            {
                error = enable ? "启用失败：检查权限或任务计划服务" : "禁用失败：检查权限或任务计划服务";
                return null;
            }
            return AutoStartService.Query();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>清空一份记录（运行历史 / 触发记录）：只有产生它的那个进程能清干净。</summary>
    public static bool ClearHistory(string kind, out string? error)
    {
        error = null;

        if (AppRole.IsUiHost) return AppBridge.ClearHistory(kind, out error);

        try
        {
            if (kind == HistoryRecords.Run) RunHistory.Clear();
            else if (kind == HistoryRecords.Trigger) TriggerHistory.Clear();
            else
            {
                error = "未知的记录：" + kind;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
