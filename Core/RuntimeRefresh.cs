using System;
using LTools.Features.CapturePro;
using LTools.Features.Floating;

namespace LTools.Core;

/// <summary>
/// <c>configs/*.json</c> 刚被写过：把这一域背后的**运行期静态缓存**作废。
/// </summary>
/// <remarks>
/// <para>
/// 写配置的进程与「运行期状态」分在两个进程里（宿主写 `configs/`，执行器跑钩子 / 动作 / OCR），
/// 所以两边都要调：宿主写完刷自己那份（<see cref="Core.Link.DataHost.RefreshRuntime"/>），
/// 执行器收到 <c>config.changed</c> 后刷自己那份（<c>ConfigStore.ReloadRuntime</c>）。
/// </para>
/// <para>
/// 这里只放**读一次就记住的静态缓存**。领域对象（面板 / 动作清单 / 快捷键匹配表 / 宏元数据）
/// 由 <c>ConfigStore.ApplyRuntime</c> 负责，别在两边各来一套。
/// 漏一个的表现都是「设置里改了，另一个进程 / 下一次仍按旧值」，所以新增设置项时
/// 它背后的缓存类必须在这里登记一行。
/// </para>
/// </remarks>
internal static class RuntimeRefresh
{
    /// <summary>按被写的域文件名作废缓存；<paramref name="changedFile"/> 为 null = 整份重读。</summary>
    public static void Invalidate(string? changedFile)
    {
        if (Is(changedFile, "settings.json"))
        {
            // 截图 / 录屏那一整套「读一次就记住」的设置
            OverlayUiSettings.Invalidate();
            ScreenshotBehavior.Invalidate();
            CaptureSoundSettings.Invalidate();
            TextRecognitionStore.Invalidate();
            DrawingSettings.Invalidate();
            ColorPresets.Invalidate();
            PinSettings.Invalidate();
            PinToScreenStore.Invalidate();
            PointerSettings.Invalidate();
            CaptureShortcutTable.Invalidate();
            CaptureHistoryStore.Invalidate();
            PinnedHistoryStore.Invalidate();
            RecordAreaHistory.Invalidate();

            // 悬浮工具条：窗口在执行器里，设置页在宿主里——按新设置重建（没启动过 = 不归这个进程）
            FloatingToolbarStore.Invalidate();
            FloatingToolbarService.RefreshIfStarted();
        }

        if (Is(changedFile, "ui.json"))
        {
            // 界面偏好（工具条布局 / 标注样式 / 工具记忆 / 选区形状）
            ToolLayoutStore.Forget();
            CaptureStyleStore.Forget();
            CaptureActionLayoutStore.Forget();
            CaptureToolMemory.Invalidate();
            SelectionStore.Forget();
        }

        if (Is(changedFile, "floating.json"))
        {
            // 悬浮项的**偏好**在文件里；悬浮项本身只在执行器内存里，刷新窗口不会新建它们
            FloatingLibrary.Reload();
            FloatingManager.RefreshAll();
        }
    }

    private static bool Is(string? name, string domainFile) =>
        string.IsNullOrEmpty(name) || string.Equals(name, domainFile, StringComparison.OrdinalIgnoreCase);
}
