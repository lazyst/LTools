using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 覆盖层的坐标换算与吸附：窗口 DIP ↔ 画面像素 ↔ 屏幕物理像素。
/// </summary>
/// <remarks>
/// 冻结画面就是显示器 1:1 的物理像素，所以 DIP→像素一律按 <c>PixelWidth / ActualWidth</c> 现算，
/// 不写死显示器缩放比：混合 DPI 下 WPF 的 <c>Left/Top</c> 与真实像素可能差几个像素。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private List<double> _snapXs = new();
    private List<double> _snapYs = new();

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, ActualWidth - 1)),
        Math.Clamp(p.Y, 0, Math.Max(0, ActualHeight - 1)));

    /// <summary>窗口 DIP → 屏幕物理像素（吸附目标用物理像素记录）。</summary>
    private System.Drawing.Point ToScreen(Point dip)
    {
        var (ix, iy) = ToImage(dip);
        return new System.Drawing.Point(_doc.Monitor.X + ix, _doc.Monitor.Y + iy);
    }

    /// <summary>屏幕物理像素矩形 → 窗口 DIP。</summary>
    private Rect ToDipRect(System.Drawing.Rectangle b)
    {
        double s = _doc.Scale > 0 ? _doc.Scale : 1;
        return new Rect(
            _doc.Origin.X + (b.X - _doc.Monitor.X) / s,
            _doc.Origin.Y + (b.Y - _doc.Monitor.Y) / s,
            b.Width / s,
            b.Height / s);
    }

    /// <summary>DIP → 画面像素（底图可能被 <see cref="CaptureDocument.Origin"/> 挪过位置）。</summary>
    private (int X, int Y) ToImage(Point dip)
    {
        double s = _doc.Scale > 0 ? _doc.Scale : 1;
        int x = (int)Math.Round((dip.X - _doc.Origin.X) * s);
        int y = (int)Math.Round((dip.Y - _doc.Origin.Y) * s);
        return (Math.Clamp(x, 0, _doc.Base.PixelWidth - 1), Math.Clamp(y, 0, _doc.Base.PixelHeight - 1));
    }

    /// <summary>物理屏幕坐标 → 窗口 DIP（用文档的「物理 ÷ DIP」，与底图大小无关）。</summary>
    private bool TryToDip(Win32.Point screen, out Point dip)
    {
        dip = default;
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        double s = _doc.Scale > 0 ? _doc.Scale : 1;
        dip = new Point(
            _doc.Origin.X + (screen.X - _doc.Monitor.X) / s,
            _doc.Origin.Y + (screen.Y - _doc.Monitor.Y) / s);
        return true;
    }

    /// <summary>
    /// 收集吸附值（DIP）：只取光标下最上层窗口及子控件的边 + 屏幕四边。
    /// <paramref name="top"/> 给定时不再自己找一次顶层窗口（鼠标移动路径上已经在找了）。
    /// </summary>
    private void RebuildSnapLines(IntPtr? top = null)
    {
        var xs = new List<double>(_windowTargets.Count * 2 + 16);
        var ys = new List<double>(_windowTargets.Count * 2 + 16);

        // 历史图的底图与屏幕内容无关：吸附和悬停都别掺进来
        if (BrowsingHistory) { _snapXs = xs; _snapYs = ys; return; }

        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        IntPtr owner = top ?? IntPtr.Zero;
        if (top == null && Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out _))
            owner = ScreenSnapTargets.TopWindowAtPoint(hwnd, new System.Drawing.Point(cursor.X, cursor.Y));

        void Add(System.Drawing.Rectangle b)
        {
            var r = ToDipRect(b);
            if (r.Width < 1 || r.Height < 1) return;
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }

        // 后台窗口的边也吸的话，框会被「看不见的窗口」拽住——只吸看得见的那个
        foreach (var box in _windowTargets)
            if (OwnedBy(box, owner)) Add(box.Bounds);
        foreach (var box in _uiaTargets)
            if (OwnedBy(box, owner)) Add(box.Bounds);

        // 每块显示器的边：跨屏拖框时能贴住屏幕交界，也能直接框住某一块屏
        foreach (var monitor in ScreenShot.Monitors()) Add(ScreenShot.ToRectangle(monitor));

        xs.Add(0); xs.Add(ActualWidth);
        ys.Add(0); ys.Add(ActualHeight);

        _snapXs = Dedup(xs);
        _snapYs = Dedup(ys);
    }

    private static List<double> Dedup(List<double> values)
    {
        values.Sort();
        var result = new List<double>(values.Count);
        foreach (double v in values)
            if (result.Count == 0 || Math.Abs(v - result[^1]) > 0.5) result.Add(v);
        return result;
    }

    /// <summary>候选是否属于光标下最上层的那个窗口（拿不到顶层窗口时不过滤）。</summary>
    private static bool OwnedBy(SnapBox box, IntPtr top) =>
        top == IntPtr.Zero || box.Owner == IntPtr.Zero || box.Owner == top;

    /// <summary>是否启用吸附：有候选边，且用户没有按住 Ctrl 临时关闭。</summary>
    private bool SnapEnabled =>
        _snapXs.Count > 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    private Point SnapPoint(Point p) => SnapEnabled
        ? new Point(SnapValue(p.X, _snapXs), SnapValue(p.Y, _snapYs))
        : p;

    private Rect SnapMove(Rect r)
    {
        if (!SnapEnabled) return r;
        double dx = NearestDelta(new[] { r.Left, r.Right }, _snapXs);
        double dy = NearestDelta(new[] { r.Top, r.Bottom }, _snapYs);
        return new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
    }

    private Rect SnapResize(Rect r, FrameHandle handle)
    {
        if (!SnapEnabled) return r;

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (handle is FrameHandle.Left or FrameHandle.TopLeft or FrameHandle.BottomLeft) left = SnapValue(left, _snapXs);
        if (handle is FrameHandle.Right or FrameHandle.TopRight or FrameHandle.BottomRight) right = SnapValue(right, _snapXs);
        if (handle is FrameHandle.Top or FrameHandle.TopLeft or FrameHandle.TopRight) top = SnapValue(top, _snapYs);
        if (handle is FrameHandle.Bottom or FrameHandle.BottomLeft or FrameHandle.BottomRight) bottom = SnapValue(bottom, _snapYs);

        if (right - left < MinSize || bottom - top < MinSize) return r;
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double SnapValue(double v, List<double> lines)
    {
        double best = v, bestDelta = SnapDistance;
        foreach (double line in lines)
        {
            double d = Math.Abs(line - v);
            if (d < bestDelta) { bestDelta = d; best = line; }
        }
        return best;
    }

    private static double NearestDelta(double[] values, List<double> lines)
    {
        double bestDelta = 0, best = SnapDistance;
        foreach (double v in values)
            foreach (double line in lines)
            {
                double d = Math.Abs(line - v);
                if (d < best) { best = d; bestDelta = line - v; }
            }
        return bestDelta;
    }
}
