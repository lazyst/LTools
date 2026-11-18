using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 悬停探测：光标下的窗口 / 控件高亮，单击即选中，滚轮在层级之间切换。
/// </summary>
/// <remarks>
/// Win32 窗口树打开时枚举一次（很快）；UIA 的控件子树按窗口在后台取一次并缓存——
/// 慢应用一次 UIA 查询能花几十到几百毫秒，鼠标移动只做本地包含判断。
/// 候选只保留「光标下最上层那个窗口」及其子控件，否则会挑中看不见的小控件。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private readonly List<SnapBox> _windowTargets = new();      // Win32 窗口树
    private readonly Dictionary<IntPtr, List<SnapBox>> _uiaTrees = new();
    private List<SnapBox> _uiaTargets = new();                  // 光标所在窗口的控件子树
    private readonly List<SnapBox> _hoverCandidates = new();
    private int _hoverIndex;
    private SnapBox? _hoverTarget;
    private Point _hoverAt = new(-9999, -9999);
    private IntPtr _uiaPending;

    /// <summary>滚轮在候选层级之间切换：向上滚 = 父级（更大范围），向下滚 = 更细的控件。</summary>
    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        // 长截图模式：滚轮归页面。覆盖层这时是透明壳，落在页面上的真滚轮根本到不了这里；
        // 还能收到 = 光标停在小面板 / 提示条上，或系统把滚轮路由给了前台窗口（覆盖层）——
        // 两种情况都不当「换层级」，顺手把前台顶回目标窗口，免得下一格又落空。
        if (_scrollMode)
        {
            e.Handled = true;
            if (!IsOnScrollPanel(e.OriginalSource as DependencyObject)) FocusScrollTarget();
            return;
        }

        // 光标停在信息条上：滚轮归字段（±1 像素），不换悬停层级
        if (BarFieldAt(e.OriginalSource as DependencyObject) is SelectionField field)
        {
            e.Handled = true;
            BarWheel(field, e.Delta);
            return;
        }

        if (IsValid(_sel) || _hoverCandidates.Count < 2) return;

        // 测角工具下滚轮改的是角度值（Shift = 1°，否则 5°）：选中了角就改它，没选就改最近那个。
        if (_tool == CaptureTool.Angle && AdjustAngleByWheel(e.Delta))
        {
            e.Handled = true;
            return;
        }

        int step = e.Delta > 0 ? 1 : -1;
        _hoverIndex = (_hoverIndex + step + _hoverCandidates.Count) % _hoverCandidates.Count;
        SetHover(_hoverCandidates[_hoverIndex]);
        e.Handled = true;
    }

    /// <summary>光标移动时更新悬停候选（窗口树即时，控件子树按窗口缓存、异步取一次）。</summary>
    private void UpdateHover(Point p)
    {
        if (BrowsingHistory) { SetHover(null); return; }
        if (IsValid(_sel)) { SetHover(null); return; }

        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        IntPtr top = ScreenSnapTargets.TopWindowAtPoint(hwnd, ToScreen(p));

        // 第一次进这个窗口：后台把它的控件子树取回来（之后移动鼠标纯本地判断）
        EnsureUiaTree(top);

        _hoverCandidates.Clear();
        var seen = new HashSet<(int X, int Y, int W, int H)>();
        var screen = ToScreen(p);

        void Collect(List<SnapBox> boxes)
        {
            foreach (var box in boxes)
            {
                if (!box.Bounds.Contains(screen) || !OwnedBy(box, top)) continue;
                // Win32 窗口与 UIA 根元素常常是同一个矩形：只留一个，免得滚轮白转一圈
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

    /// <summary>
    /// 按窗口缓存控件子树：UIA 是跨进程调用，整棵树只在**第一次进入某个窗口**时后台取一次。
    /// </summary>
    private void EnsureUiaTree(IntPtr top)
    {
        if (top == IntPtr.Zero) return;

        if (_uiaTrees.TryGetValue(top, out var cached))
        {
            if (ReferenceEquals(_uiaTargets, cached)) return;
            _uiaTargets = cached;
            RebuildSnapLines();
            return;
        }

        if (_uiaPending == top) return;   // 已经有人在查这个窗口了
        _uiaPending = top;

        var monitor = _area;
        Task.Run(() => ScreenSnapTargets.FromWindowTree(top, monitor))
            .ContinueWith(t =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_uiaPending != top) return;
                    _uiaPending = IntPtr.Zero;

                    if (t.Status != TaskStatus.RanToCompletion)
                    {
                        UpdateChrome();
                        return;
                    }

                    // 空结果也缓存：说明这个程序不暴露 UIA，别再反复问它
                    if (_uiaTrees.Count > 40) _uiaTrees.Clear();
                    _uiaTrees[top] = t.Result;

                    if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
                    {
                        var at = Clamp(dip);
                        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
                        if (ScreenSnapTargets.TopWindowAtPoint(hwnd, ToScreen(at)) == top)
                        {
                            _uiaTargets = t.Result;
                            RebuildSnapLines();
                            if (!IsValid(_sel)) UpdateHover(at);
                        }
                    }

                    UpdateChrome();
                }));
            });
    }
}
