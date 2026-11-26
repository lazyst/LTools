using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「窗口 / 控件探测与吸附」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 窗口 / 控件探测与吸附 ============

    /// <summary>滚轮在候选层级之间切换：向上滚 = 父级（更大范围），向下滚 = 更细的控件。</summary>
    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_pointMode || IsValid(_sel) || _hoverCandidates.Count < 2) return;

        int step = e.Delta > 0 ? 1 : -1;
        _hoverIndex = (_hoverIndex + step + _hoverCandidates.Count) % _hoverCandidates.Count;
        SetHover(_hoverCandidates[_hoverIndex]);
        e.Handled = true;
    }

    /// <summary>光标移动时更新悬停候选：窗口树即时，控件走「懒下钻」（只用已展开的层）。</summary>
    private void UpdateHover(Point p)
    {
        if (IsValid(_sel)) { SetHover(null); return; }

        var screen = ToScreen(p);
        IntPtr top = ScreenSnapTargets.TopWindowAtPoint(
            new WindowInteropHelper(this).Handle, screen);

        // 只读缓存（微秒级）；没读到的层交给后台再走一趟，绝不在这里发跨进程调用。
        SnapPath path = top == IntPtr.Zero
            ? SnapPath.Empty
            : _probe.Resolve(top, screen, _monitor, expand: false);
        ApplyPath(top, path);
        EnsureUiaTree(top, screen, path);

        _hoverCandidates.Clear();
        var seen = new HashSet<(int X, int Y, int W, int H)>();

        // 只保留最上层那个窗口及其子控件：窗口树里也包含被遮住的后台窗口，
        // 不过滤的话光标处可能挑中最上层看不见的小控件。
        void Collect(IReadOnlyList<SnapBox> boxes)
        {
            foreach (var box in boxes)
            {
                if (!box.Bounds.Contains(screen) || !OwnedBy(box, top)) continue;
                // Win32 窗口与 UIA 根元素常常是同一个矩形：只留一个，免得滚轮白转一圈。
                if (!seen.Add((box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height))) continue;
                _hoverCandidates.Add(box);
            }
        }

        Collect(_windowTargets);
        Collect(_uiaTargets);

        if (_hoverCandidates.Count == 0)
        {
            SetHover(null);
            return;
        }

        _hoverCandidates.Sort((a, b) => a.Area.CompareTo(b.Area));

        // 光标在同一处停留时不重置层级（滚轮选好的那一级要留住）
        if (Math.Abs(p.X - _hoverAt.X) > PressSlop || Math.Abs(p.Y - _hoverAt.Y) > PressSlop)
        {
            _hoverIndex = 0;
            _hoverAt = p;
        }
        if (_hoverIndex >= _hoverCandidates.Count) _hoverIndex = 0;
        SetHover(_hoverCandidates[_hoverIndex]);
    }

    private void SetHover(SnapBox? box)
    {
        if (Nullable.Equals(_hoverTarget, box)) return;
        _hoverTarget = box;
        UpdateChrome();
    }

    /// <summary>候选是否属于光标下最上层的那个窗口（拿不到顶层窗口时不过滤）。</summary>
    private static bool OwnedBy(SnapBox box, IntPtr top) =>
        top == IntPtr.Zero || box.Owner == IntPtr.Zero || box.Owner == top;

    /// <summary>
    /// 把这条支路接到吸附线 / 悬停候选上。矩形没变就不重建（每次鼠标移动都重算吸附线会很贵：
    /// 里面还要再枚举一次窗口树）。
    /// </summary>
    private void ApplyPath(IntPtr top, SnapPath path)
    {
        if (top == _uiaWindow && path.Signature == _uiaSignature && path.Stop == _uiaStop) return;

        _uiaWindow = top;
        _uiaSignature = path.Signature;
        _uiaTargets = path.Boxes;
        _uiaStop = path.Stop;
        RebuildSnapLines(top);
    }

    /// <summary>
    /// 缺层时在后台补一趟（<c>expand: true</c>）：一次只沿光标那条支路往下，缺几层补几层。
    /// 同一个窗口同时只排一趟；连续没进展的窗口由 <see cref="SnapProbe"/> 标记放弃。
    /// </summary>
    private void EnsureUiaTree(IntPtr top, System.Drawing.Point screen, SnapPath path)
    {
        if (top == IntPtr.Zero || path.Complete) return;
        if (_uiaPending == top) return;              // 已经有人在补这个窗口了
        if (!_probe.CanExpand(top)) return;          // 这个窗口读不动了，别每次移动都白排一趟

        _uiaPending = top;
        System.Drawing.Rectangle monitor = _monitor;

        Task.Run(() => _probe.Resolve(top, screen, monitor, expand: true))
            .ContinueWith(_ =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_uiaPending != top) return;
                    _uiaPending = IntPtr.Zero;

                    if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
                    {
                        var at = Clamp(dip);
                        if (ScreenSnapTargets.TopWindowAtPoint(
                                new WindowInteropHelper(this).Handle, ToScreen(at)) == top &&
                            !IsValid(_sel))
                            UpdateHover(at);
                    }

                    UpdateChrome();
                }));
            });
    }

    /// <summary>
    /// 收集吸附值（DIP）：只取光标下最上层窗口及子控件的边 + 屏幕四边。
    /// <paramref name="top"/> 给定时不再自己找一次顶层窗口（鼠标移动路径上已经在找了）。
    /// </summary>
    private void RebuildSnapLines(IntPtr? top = null)
    {
        var xs = new List<double>(_windowTargets.Count * 2 + 16);
        var ys = new List<double>(_windowTargets.Count * 2 + 16);

        IntPtr owner = top ?? IntPtr.Zero;
        if (top == null && Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out _))
            owner = ScreenSnapTargets.TopWindowAtPoint(
                new WindowInteropHelper(this).Handle, new System.Drawing.Point(cursor.X, cursor.Y));

        void Add(System.Drawing.Rectangle b)
        {
            var r = ToDipRect(b);
            if (r.Width < 1 || r.Height < 1) return;
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }

        // 后台窗口的边也吸的话，框会被「看不见的窗口」拽住——Quicker / Snipaste 都只吸看得见的那个。
        foreach (var box in _windowTargets)
            if (OwnedBy(box, owner)) Add(box.Bounds);
        foreach (var box in _uiaTargets)
            if (OwnedBy(box, owner)) Add(box.Bounds);
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

    private Rect SnapResize(Rect r, Handle handle)
    {
        if (!SnapEnabled) return r;

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (handle is Handle.Left or Handle.TopLeft or Handle.BottomLeft) left = SnapValue(left, _snapXs);
        if (handle is Handle.Right or Handle.TopRight or Handle.BottomRight) right = SnapValue(right, _snapXs);
        if (handle is Handle.Top or Handle.TopLeft or Handle.TopRight) top = SnapValue(top, _snapYs);
        if (handle is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight) bottom = SnapValue(bottom, _snapYs);

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
