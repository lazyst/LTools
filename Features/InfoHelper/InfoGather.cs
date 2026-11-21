using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.Selection;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.InfoHelper;

/// <summary>截图的三种取法。</summary>
internal enum ShotMode
{
    /// <summary>整个虚拟桌面。</summary>
    FullScreen,

    /// <summary>框选一块区域。</summary>
    Region,

    /// <summary>点选一个窗口（截它的可见矩形）。</summary>
    Window,
}

/// <summary>
/// 把「辅助工具」上的按钮变成一张卡片：只负责取数据（Win32 / COM / 框选 / 截图落盘），
/// 文案与格式全在 <see cref="InfoCards"/>（纯函数，可自测）。
/// </summary>
/// <remarks>
/// <para>
/// 取窗口 / 区域 / 坐标 / 取色 / 截图都走仓库里既有的交互入口：
/// <see cref="WindowPickOverlay.Pick"/>（点击窗口选取，会临时收起 LTools 全部窗口）与
/// <see cref="ScreenPicker"/>（框选 / 取点 / 取色，同样先收起 LTools）。
/// <b>别再写一套遮罩或自己 EnumWindows</b>——坐标系、DPI 换算、鼠标钩子让路都在那两处。
/// </para>
/// <para>
/// 「选中的文件」不看前台窗口（辅助工具自己就是前台），走
/// <see cref="Selection.ShellBridge.SelectedAny"/>（z 序最上面有选中项的资源管理器窗口，桌面兜底）。
/// </para>
/// </remarks>
internal static class InfoGather
{
    // ============ 窗口 ============

    /// <summary>点选一个窗口并读它的信息；取消返回 null。</summary>
    public static InfoCard? PickWindow(Window? owner)
    {
        var (info, _) = WindowPickOverlay.Pick(owner);
        return info == null ? null : FromHandle(info.Handle);
    }

    /// <summary>按句柄读窗口信息；窗口已经关掉返回 null。</summary>
    public static InfoCard? FromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return null;

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);

        return InfoCards.Window(
            WindowFinder.GetText(hwnd),
            WindowFinder.GetClass(hwnd),
            ProcessName(pid),
            hwnd,
            (x, y, w, h),
            ClientBounds(hwnd),
            Win32.IsZoomed(hwnd),
            Win32.IsIconic(hwnd));
    }

    /// <summary>客户区矩形（屏幕物理像素：左上角用 <c>ClientToScreen</c> 换算）。</summary>
    private static (int X, int Y, int W, int H) ClientBounds(IntPtr hwnd)
    {
        if (!Win32.GetClientRect(hwnd, out var rc)) return (0, 0, 0, 0);
        var origin = new Win32.Point { X = 0, Y = 0 };
        Win32.ClientToScreen(hwnd, ref origin);
        return (origin.X, origin.Y,
            Math.Max(0, rc.Right - rc.Left), Math.Max(0, rc.Bottom - rc.Top));
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";   // 名单匹配写的是 notepad.exe，别只给 notepad
        }
        catch
        {
            return "?";
        }
    }

    // ============ 区域 / 坐标 / 取色 ============

    public static InfoCard? Region(Window? owner) =>
        ScreenPicker.TryPickRegion(owner, out Int32Rect area, out _)
            ? InfoCards.Region(area.X, area.Y, area.Width, area.Height)
            : null;

    public static InfoCard? Point(Window? owner) =>
        ScreenPicker.TryPickPoint(owner, "取坐标", out int x, out int y)
            ? InfoCards.Point(x, y)
            : null;

    public static InfoCard? Color(Window? owner) =>
        ScreenPicker.TryPickColor(owner, out string hex, out int x, out int y)
            ? InfoCards.Color(x, y, hex)
            : null;

    // ============ 屏幕环境 ============

    public static InfoCard Screen()
    {
        List<ScreenGeometry> all = Screens.All();
        var screens = new List<InfoScreen>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            ScreenGeometry s = all[i];
            screens.Add(new InfoScreen(i + 1, s.Primary, s.Dpi,
                s.Left, s.Top, s.Width, s.Height,
                s.WorkLeft, s.WorkTop, s.WorkWidth, s.WorkHeight));
        }

        Win32.GetCursorPos(out var cursor);
        return InfoCards.Screen(
            ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
            ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight,
            screens, cursor.X, cursor.Y);
    }

    // ============ 选中的文件 ============

    /// <summary>资源管理器 / 桌面里选中的项；一处都没选中返回 null（不是错误）。</summary>
    public static InfoCard? Selection()
    {
        var selection = ShellBridge.SelectedAny();
        return selection == null ? null : InfoCards.Selection(selection.Folder, selection.Paths);
    }

    // ============ 截图 ============

    public static InfoCard? Shot(Window? owner, ShotMode mode)
    {
        switch (mode)
        {
            case ShotMode.Region:
            {
                if (!ScreenPicker.TryPickRegion(owner, out Int32Rect area, out BitmapSource crop)) return null;
                return InfoCards.Shot("框选区域", InfoShotStore.Save(crop),
                    area.X, area.Y, area.Width, area.Height, crop);
            }

            case ShotMode.Window:
            {
                var (info, _) = WindowPickOverlay.Pick(owner);
                if (info == null || info.Handle == IntPtr.Zero) return null;

                var (x, y, w, h) = WindowFinder.GetVisibleBounds(info.Handle);
                if (w <= 0 || h <= 0) return null;

                // 点选器结束时已经把 LTools 的窗口恢复回来了，这里要再收一次，别把自己截进去。
                BitmapSource shot = CaptureHidden(owner, x, y, w, h);
                string name = info.Title.Length == 0 ? info.ProcessName : info.Title;
                return InfoCards.Shot($"窗口「{name}」", InfoShotStore.Save(shot), x, y, w, h, shot);
            }

            default:
            {
                int x = ScreenCapture.VirtualLeft, y = ScreenCapture.VirtualTop;
                int w = ScreenCapture.VirtualWidth, h = ScreenCapture.VirtualHeight;
                BitmapSource shot = CaptureHidden(owner, x, y, w, h);
                return InfoCards.Shot("整个屏幕", InfoShotStore.Save(shot), x, y, w, h, shot);
            }
        }
    }

    /// <summary>收起 LTools 全部窗口后截一块矩形（否则辅助工具自己会挡在画面里）。</summary>
    private static BitmapSource CaptureHidden(Window? owner, int x, int y, int w, int h)
    {
        var restore = TransientMinimize.MinimizeAll(owner);
        using var suppressed = ToastService.SuppressForCapture();   // 提示卡片是置顶窗口，别截进画面
        try
        {
            Thread.Sleep(TransientMinimize.SettleMs);
            using var bmp = ScreenCapture.Capture(x, y, w, h);
            return ScreenPicker.ToBitmapSource(bmp);
        }
        finally
        {
            restore();
        }
    }

    // ============ 再取 ============

    /// <summary>
    /// 按卡片自己的参数重取一次（「再取」按钮）：窗口用原句柄、截图按原矩形，
    /// 区域 / 坐标 / 取色的原值已经没法复原（那是一次性的交互），重新框 / 点一次。
    /// </summary>
    public static InfoCard? Refresh(InfoCard card, Window? owner) => card.Kind switch
    {
        InfoKind.Window => FromHandle(card.Handle),
        InfoKind.Region => Region(owner),
        InfoKind.Point => Point(owner),
        InfoKind.Color => Color(owner),
        InfoKind.Screen => Screen(),
        InfoKind.Selection => Selection(),
        InfoKind.Shot => RefreshShot(card, owner),
        _ => null,
    };

    private static InfoCard? RefreshShot(InfoCard card, Window? owner)
    {
        if (card.Area is not { } area || area.W <= 0 || area.H <= 0) return null;

        BitmapSource shot = CaptureHidden(owner, area.X, area.Y, area.W, area.H);
        string file = InfoShotStore.Save(shot);
        InfoShotStore.Delete(card.FilePath);   // 同一张卡换图，旧文件别留
        return InfoCards.Shot(card.How, file, area.X, area.Y, area.W, area.H, shot);
    }
}
