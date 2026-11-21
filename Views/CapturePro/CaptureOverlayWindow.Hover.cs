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
    private readonly SnapProbe _probe = new();                  // 按窗口缓存的「懒下钻」控件树
    private IReadOnlyList<SnapBox> _uiaTargets = Array.Empty<SnapBox>();   // 光标那条支路（深 → 浅）
    private SnapStop _uiaStop = SnapStop.complete;
    private int _uiaSignature;
    private IntPtr _uiaWindow;
    private readonly List<SnapBox> _hoverCandidates = new();
    private int _hoverIndex;
    private SnapBox? _hoverTarget;
    private Point _hoverAt = new(-9999, -9999);
    private IntPtr _uiaPending;

    /// <summary>滚轮在候选层级之间切换：向上滚 = 父级（更大范围），向下滚 = 更细的控件。</summary>
    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        // 二维码：气泡里的只读文本要自己滚（别拿去换候选层级），标记上滚轮不做任何事
        if (IsOnQrBubble(e.OriginalSource as DependencyObject)) return;
        if (IsOnQr(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        // 长截图模式：滚轮归页面。覆盖层这时是透明壳，落在页面上的真滚轮根本到不了这里；
        // 还能收到 = 光标停在小面板 / 提示条上，或系统把滚轮路由给了前台窗口（覆盖层）——
        // 两种情况都不当「换层级」，顺手把前台顶回目标窗口，免得下一格又落空。
        if (_scrollMode)
        {
            e.Handled = true;
            if (!IsOnScrollPanel(e.OriginalSource as DependencyObject)) FocusScrollTarget();
            return;
        }

        // 光标停在信息条上：滚轮归字段（±1 像素），不换悬停层级。
        // 智能选区预选态的信息条是**只读**的（上游 selectionEditable = false），滚轮归层级。
        if (!SmartPreviewActive &&
            BarFieldAt(e.OriginalSource as DependencyObject) is SelectionField field)
        {
            e.Handled = true;
            BarWheel(field, e.Delta);
            return;
        }

        // 智能选区：滚轮在命中路径上换一层（往上 = 外层整窗，往下 = 更细的子元素）
        if (SmartPreviewActive)
        {
            if (_smart.HitCount >= 2)
            {
                StepSmartLevel(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            }
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

    /// <summary>
    /// 光标移动时更新悬停候选：Win32 窗口树即时，控件走「懒下钻」（只用已展开的层，
    /// 缺哪层交给后台补），所以鼠标移动永远只做本地判断。
    /// <paramref name="refine"/> = 后台补齐之后的那一趟，只走「只增不减」的细化路径。
    /// </summary>
    private void UpdateHover(Point p, bool refine = false)
    {
        if (BrowsingHistory) { SetHover(null); return; }
        if (IsValid(_sel) && !_smartPreviewActive) { SetHover(null); return; }

        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
        var screen = ToScreen(p);
        IntPtr top = ScreenSnapTargets.TopWindowAtPoint(hwnd, screen);

        // 只读缓存（微秒级）；没读到的层交给后台再走一趟，绝不在这里发跨进程调用。
        SnapPath path = top == IntPtr.Zero
            ? SnapPath.Empty
            : _probe.Resolve(top, screen, _area, expand: false);
        ApplyPath(top, path);
        EnsureUiaTree(top, screen, path);

        _hoverCandidates.Clear();
        var seen = new HashSet<(int X, int Y, int W, int H)>();

        void Collect(IReadOnlyList<SnapBox> boxes)
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
            if (HideSmartPreview()) UpdateChrome();   // 上游 applyHitPath 失败 = setSelectionRect({})
            return;
        }

        _hoverCandidates.Sort((a, b) => a.Area.CompareTo(b.Area));

        // 智能选区：候选就是命中路径（面积升序 = 最深 → 最外层），交给模型挑层级
        if (SmartSelectionAvailable)
        {
            UpdateSmartTarget(top, refine);
            return;
        }

        if (HideSmartPreview()) UpdateChrome();

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
        System.Drawing.Rectangle monitor = _area;

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
                        IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
                        if (ScreenSnapTargets.TopWindowAtPoint(hwnd, ToScreen(at)) == top &&
                            (!IsValid(_sel) || _smartPreviewActive))
                            UpdateHover(at, refine: true);
                    }

                    UpdateChrome();
                }));
            });
    }
}
