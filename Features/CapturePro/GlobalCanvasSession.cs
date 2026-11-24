using System;
using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features.CapturePro;

/// <summary>
/// 全屏画布的单例入口（照上游 <c>GlobalCanvasController</c>）：已经开着就只切点击穿透，
/// 否则新建一个。
/// </summary>
/// <remarks>
/// <para>
/// 面板 / 快捷键 / 悬浮按钮调 <see cref="Activate"/>（可来自任何线程，内部投递到 UI 线程）；
/// 画布开着时再按一次「打开全屏画布」这条快捷键经 <see cref="ToggleClickThrough"/> 切穿透——
/// 这条路径由 <c>KeyboardHook</c> 用 <see cref="Core.Hotkeys.HotkeyRegistry.TryFireOnly"/>
/// 在钩子线程匹配后调用，所以只做投递、不碰窗口。
/// </para>
/// <para>
/// 「画布开着 = 钩子挂起全局快捷键」这件事已经搬到 <see cref="Core.InputGuard"/>：画布在宿主里，
/// 挂起状态由它推过来，这里只剩开窗与切穿透，**不再保存任何给钩子线程读的标记**。
/// </para>
/// </remarks>
internal static class GlobalCanvasSession
{
    /// <summary>「打开 / 切换全屏画布」这条内置功能的引用（快捷键绑它）。</summary>
    public const string HotkeyRef = "ltools.globalCanvas";

    private static Views.CapturePro.GlobalCanvasWindow? _window;   // 只在 UI 线程读写

    /// <summary>打开全屏画布；已经开着就切点击穿透并把它提到前面（UI 线程）。</summary>
    public static void Activate()
    {
        if (_window is { } open)
        {
            open.ToggleClickThrough();
            open.Activate();
            return;
        }

        var window = new Views.CapturePro.GlobalCanvasWindow();
        _window = window;

        window.Closed += (_, _) => _window = null;

        window.Show();
    }

    /// <summary>再按一次「打开全屏画布」= 切点击穿透（钩子线程 / 任何线程）。</summary>
    /// <remarks>
    /// 画布在宿主里、钩子在提权进程：提权进程这边只把意图转过去（宿主里画布已经开着，
    /// <see cref="Activate"/> 就是切穿透）。宿主没连上时才是本地那份窗口。
    /// </remarks>
    public static void ToggleClickThrough()
    {
        if (!AppRole.IsUiHost)
        {
            CaptureWindowBridge.Show(CaptureWindows.GlobalCanvas);
            return;
        }

        Ui.Post(() =>
        {
            if (_window is { } window) window.ToggleClickThrough();
        });
    }
}
