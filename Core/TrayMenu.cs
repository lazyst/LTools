using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Features.Floating;
using LTools.Views.Controls;

namespace LTools.Core;

/// <summary>
/// 托盘图标的右键菜单：条目、顺序、状态点列与标题刷新都只在这里写一份。
/// </summary>
/// <remarks>
/// <para>
/// 抽成独立类型是为了能被冒烟直接验证（<c>tests/CaptureOverlaySmoke</c> 的 <c>tray</c> 段会真把菜单
/// 打开、用真鼠标悬停「截图」父项），<see cref="App"/> 只负责把它挂到托盘图标上。
/// </para>
/// <para>
/// 两条既定做法：① 本项目 MenuItem 模板只呈现 Header、不呈现 Icon，状态点必须放进 Header 的
/// <c>StackPanel</c>，所有条目共用这一列（文字不因开关状态左右跳动）；② 截图相关的入口收在
/// 「截图」二级菜单里，父项走 <see cref="MenuFx.Submenu"/>（悬停立即展开，不等系统的菜单延迟）。
/// </para>
/// </remarks>
internal sealed class TrayMenu
{
    private const double StatusDotSize = 9;
    private const double StatusDotGap = 7;

    private static readonly Brush EnabledDotBrush = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush PausedDotBrush = Frozen(0xEA, 0x58, 0x0C);

    private readonly List<(Ellipse Dot, Func<bool> IsOn, Brush? OffBrush)> _dots = new();
    private readonly TextBlock _macroText;
    private readonly TextBlock _triggerText;

    /// <summary>菜单本体（<c>TaskbarIcon.ContextMenu</c> 用它）。</summary>
    internal ContextMenu Menu { get; }

    /// <param name="exit">「退出 LTools」（走 <see cref="App.ExitApplication"/>，要关钩子线程）。</param>
    /// <param name="toggleTrigger">「暂停 / 恢复自动触发」（写紧急禁用空文件）。</param>
    internal TrayMenu(Action exit, Action toggleTrigger)
    {
        Menu = new ContextMenu();
        Menu.Items.Add(NewItem("速记 (CapsLock+N)", () => QuickNote.Toggle()));
        Menu.Items.Add(NewItem("动作面板 (CapsLock+T)", () => Features.PanelLauncher.Toggle()));
        Menu.Items.Add(NewItem("设置 (CapsLock+\\)", () => ConfigHelper.Toggle()));
        Menu.Items.Add(NewItem("辅助工具…", () => Views.InfoHelperWindow.Toggle()));
        Menu.Items.Add(NewItem("局域网共享…", () => LTools.Core.Share.ShareHelper.Open()));

        // 截图相关的入口收进二级菜单：平铺出来要占 5 行，日常更常用的其实是「截图」本身。
        var capture = MenuFx.Submenu(Menu, HeaderRow("截图"));
        capture.Items.Add(NewItem("截图…", () => RunCaptureFeature("common.capture")));
        capture.Items.Add(NewItem("延时截图…", () => RunCaptureFeature("ltools.captureDelay")));
        capture.Items.Add(new Separator());
        capture.Items.Add(NewItem("采集历史…", () => Views.CapturePro.CaptureHistoryWindow.Toggle()));
        capture.Items.Add(NewItem("贴图管理…", () => Views.CapturePro.PinManagerWindow.Toggle()));
        capture.Items.Add(new Separator());
        capture.Items.Add(NewItem("图片编辑…", () => ImageEditSession.OpenEmpty()));
        capture.Items.Add(NewItem("切图…", () => SplitSession.OpenEmpty()));
        capture.Items.Add(NewItem("图像拼接…", () => CollageSession.OpenEmpty()));

        Menu.Items.Add(new Separator());
        var macroItem = NewItem("开始 / 停止录制宏", () => MacroRecorder.Toggle());
        _macroText = TextOf(macroItem);
        Menu.Items.Add(macroItem);
        Menu.Items.Add(NewItem("键鼠宏…", ConfigHelper.OpenMacros));

        Menu.Items.Add(new Separator());
        Menu.Items.Add(NewItem("启用 CapsLock 修饰键", Settings.ToggleCapsLockModifier,
            () => AppState.CapsLockModifier));
        Menu.Items.Add(NewItem("启用超级面板", Settings.ToggleSuperPanel,
            () => AppState.IsSuperPanelEnabled));
        // 和上面两个开关项同一套状态点列：运行中绿点、紧急禁用橙点（不再用暂停 / 播放字符图标，
        // 那个字形在 Segoe UI 下会渲染成带方框的 emoji，和灰阶菜单、绿点列都不搭）。
        var triggerItem = NewItem("暂停自动触发（紧急禁用）", toggleTrigger,
            () => !TriggerService.EmergencyDisabled, PausedDotBrush);
        _triggerText = TextOf(triggerItem);
        Menu.Items.Add(triggerItem);

        Menu.Items.Add(new Separator());
        Menu.Items.Add(NewItem("关闭所有悬浮项", FloatingManager.CloseAll));
        Menu.Items.Add(new Separator());
        Menu.Items.Add(NewItem("退出 LTools", exit));

        // 状态点每次打开菜单时刷新：状态可能经设置页或托盘切换，托盘菜单要跟随
        Menu.Opened += (_, _) => Refresh();
    }

    /// <summary>刷新状态点 + 两个会变的标题（打开菜单时、点完开关项后调用）。</summary>
    internal void Refresh()
    {
        RefreshDots();
        RefreshMacro();
        RefreshTrigger();
    }

    /// <summary>按各开关的当前状态刷新状态点。</summary>
    internal void RefreshDots()
    {
        foreach (var (dot, isOn, off) in _dots)
            dot.Fill = isOn() ? EnabledDotBrush : (off ?? Brushes.Transparent);
    }

    /// <summary>
    /// 宏菜单标题跟随录制状态：录制中显示「■ 停止录制宏（热键）」，让托盘始终是一个可用的停止入口。
    /// </summary>
    internal void RefreshMacro() =>
        _macroText.Text = MacroRecorder.IsRecording
            ? $"■ 停止录制宏（{MacroSettings.RecordHotkeyDisplay}）"
            : $"● 开始录制宏（{MacroSettings.RecordHotkeyDisplay}）";

    /// <summary>「暂停自动触发」标题跟随紧急禁用状态（绿 / 橙点由状态点列表达）。</summary>
    internal void RefreshTrigger() =>
        _triggerText.Text = TriggerService.EmergencyDisabled
            ? "恢复自动触发"
            : "暂停自动触发（紧急禁用）";

    /// <summary>
    /// 托盘菜单项：标题左侧统一留出状态点列（做法同系统菜单的勾选列，保证各行文字对齐）。
    /// <paramref name="isOn"/> 非空即开关项：启用 → 绿点（SuccessColor #16A34A），
    /// 禁用 → 有 <paramref name="offBrush"/> 就显示它（如紧急禁用的橙点），否则透明但不收列。
    /// </summary>
    private MenuItem NewItem(string header, Action onClick, Func<bool>? isOn = null, Brush? offBrush = null)
    {
        var headerRow = HeaderRow(header);
        var dot = (Ellipse)headerRow.Children[0];

        var mi = new MenuItem { Header = headerRow };
        mi.Click += (_, _) =>
        {
            onClick();
            RefreshDots();
        };
        if (isOn != null)
            _dots.Add((dot, isOn, offBrush));
        return mi;
    }

    /// <summary>
    /// 菜单标题行：左侧一个状态点（普通项透明、开关项上色），右侧文字。
    /// 托盘每一项都走这一份，状态点列才等宽。
    /// </summary>
    private static StackPanel HeaderRow(string header)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = StatusDotSize,
            Height = StatusDotSize,
            Margin = new Thickness(0, 0, StatusDotGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = Brushes.Transparent,
        });
        row.Children.Add(new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>标题行里的文字块（宏 / 自动触发两项的标题会变）。</summary>
    private static TextBlock TextOf(MenuItem item) => (TextBlock)((StackPanel)item.Header).Children[1];

    /// <summary>
    /// 托盘里点「截图」这类内置功能：把当前光标位置一起带进去（与悬浮工具条同一条路），
    /// 覆盖层按这个点所在显示器决定落点。菜单关掉之后才跑，覆盖层不会跟菜单抢前台。
    /// </summary>
    private static void RunCaptureFeature(string reference)
    {
        int x = 0, y = 0;
        if (Native.Win32.GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
        }

        ActionExecutor.RunRef(reference, x, y, RunSource.Manual);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
