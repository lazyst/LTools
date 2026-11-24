using LTools.Core;
using LTools.Core.Link;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图相关窗口的**唯一入口**：采集历史 / 贴图管理 / 图片编辑 / 切图 / 图像拼接，
/// 以及「恢复上一张关闭的贴图」「隐藏 / 显示所有贴图」这两条贴图命令。
/// </summary>
/// <remarks>
/// <para>
/// 这些窗口与它们写的域（<c>captures/</c> <c>pins/</c>）都在普通权限宿主里
/// （<c>docs/PROCESS_SPLIT.md</c> §15.4 第 3 步）。托盘菜单、内置功能、面板磁贴都只调
/// <see cref="Open"/>——宿主里直接开，提权进程里过链路（<see cref="CaptureWindowBridge"/>），
/// 宿主没连上才就地降级开一份（与面板 / 速记 / 辅助工具同一套口径）。
/// </para>
/// <para>
/// **窗口本身不在这里判断「谁该开」**：新增入口只加一个 <see cref="CaptureWindows"/> 的目标常量 +
/// 一行 switch，别在调用点各写一次 <c>ConfigStore.ReadOnly</c>。
/// </para>
/// </remarks>
internal static class CaptureWindows
{
    public const string History = "history";
    public const string PinManager = "pinManager";
    public const string ImageEdit = "imageEdit";
    public const string Split = "split";
    public const string Collage = "collage";
    public const string PinRestoreLast = "pinRestoreLast";
    public const string PinToggleAll = "pinToggleAll";

    /// <summary>贴图设置变了（自动识别 / 可划选 / 快速标注）：让**已经贴着的图**按新设置走一遍。</summary>
    public const string PinRefresh = "pinRefresh";

    /// <summary>打开一份截图相关窗口 / 执行一条贴图命令（任何线程）。</summary>
    public static void Open(string target)
    {
        if (ConfigStore.ReadOnly || !CaptureWindowBridge.TryShow(target)) OpenLocal(target);
    }

    /// <summary>在本进程里真正开窗（宿主用；提权进程里只在「宿主没连上」时被调）。</summary>
    public static void OpenLocal(string target)
    {
        Ui.Run(() =>
        {
            switch (target)
            {
                case History: CaptureHistoryWindow.Toggle(); break;
                case PinManager: PinManagerWindow.Toggle(); break;
                case ImageEdit: ImageEditSession.OpenEmpty(); break;
                case Split: SplitSession.OpenEmpty(); break;
                case Collage: CollageSession.OpenEmpty(); break;
                case PinRestoreLast: RestoreLastPin(); break;
                case PinToggleAll: ToggleAllPins(); break;
                case PinRefresh: CapturePinManager.RefreshRecognition(); break;
            }
        });
    }

    /// <summary>「恢复上一张关闭的贴图」：没有可恢复的记录就提示一句（不是错误，照上游静默语义）。</summary>
    private static void RestoreLastPin()
    {
        var pin = CapturePinManager.RestoreLastClosed();
        if (pin == null)
            ToastService.Show("还没有关掉的贴图可以恢复", "贴图", 2000, ToastKind.warning);
        else
            ToastService.Show("已贴回上一张关闭的贴图", "贴图", 1600, ToastKind.success);
    }

    /// <summary>「隐藏 / 显示所有贴图」：一次收起、再一次放回（不关窗、不写历史）。</summary>
    private static void ToggleAllPins()
    {
        if (CapturePinManager.AllHidden || CapturePinManager.Visible.Count == 0)
        {
            int shown = CapturePinManager.ShowAll();
            if (shown == 0)
                ToastService.Show("屏幕上没有可以放回的贴图", "贴图", 1800, ToastKind.warning);
            return;
        }

        int hidden = CapturePinManager.HideAll();
        ToastService.Show($"已收起 {hidden} 张贴图（再按一次放回来）", "贴图", 2000);
    }
}
