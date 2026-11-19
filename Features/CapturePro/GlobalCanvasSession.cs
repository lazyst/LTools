using System;
using System.Threading;
using LTools.Core;

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
/// <see cref="IsOpen"/> 是给钩子线程读的（volatile 标记），不要在这里碰 WPF。
/// </para>
/// </remarks>
internal static class GlobalCanvasSession
{
    /// <summary>「打开 / 切换全屏画布」这条内置功能的引用（快捷键绑它）。</summary>
    public const string HotkeyRef = "ltools.globalCanvas";

    private static Views.CapturePro.GlobalCanvasWindow? _window;   // 只在 UI 线程读写
    private static int _open;

    /// <summary>画布是否开着（钩子线程可读）。</summary>
    public static bool IsOpen => Volatile.Read(ref _open) != 0;

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
        Volatile.Write(ref _open, 1);

        window.Closed += (_, _) =>
        {
            _window = null;
            Volatile.Write(ref _open, 0);
        };

        window.Show();
    }

    /// <summary>钩子线程：再按一次「打开全屏画布」= 切点击穿透（投递到 UI 线程执行）。</summary>
    public static void ToggleClickThrough()
    {
        Ui.Post(() =>
        {
            if (_window is { } window) window.ToggleClickThrough();
        });
    }
}
