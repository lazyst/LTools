using System;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 面板唤起入口：面板只有一个家——普通权限宿主（<c>UiHost.exe</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 宿主连着 → 发 <c>panel.show</c> / <c>panel.toggle</c>；刚启动还没连上 → <see cref="LinkWorker"/>
/// 记着，连上后补发（不会丢那一下）。提权进程不再自己开面板：它只负责执行动作（<c>run.action</c>），
/// 界面归宿主。见 <c>docs/PROCESS_SPLIT.md</c> §14。
/// </para>
/// <para>
/// **所有**「显示 / 切换面板」的入口都走这里，别直接调 <see cref="SuperPanel"/>——那是宿主进程内部
/// 用来真正建窗口的类（`UiHost` 收到 <c>panel.show</c> 后调它），在提权进程里调等于开了第二份。
/// </para>
/// </remarks>
internal static class PanelLauncher
{
    /// <summary>在光标处显示面板（右键长按手势 / 其它显式入口）。</summary>
    public static void Show(IntPtr contextHwnd) => LinkWorker.RequestShow(contextHwnd);

    /// <summary>切换面板显示 / 关闭（CapsLock+T、托盘菜单）。</summary>
    public static void Toggle() => LinkWorker.RequestToggle();

    /// <summary>显示面板并停在「暂存区」（「在暂存区编辑…」、暂存区里的入口）。</summary>
    public static void ShowStash() => LinkWorker.RequestStash();

    /// <summary>
    /// 关掉面板（如果开着）。点悬浮按钮时用：那一下本来就算「点到面板外面」，
    /// 以前由鼠标钩子按矩形判定，现在面板在宿主里，只能由点击方说一声。
    /// </summary>
    public static void Close() => LinkWorker.RequestClose();
}
