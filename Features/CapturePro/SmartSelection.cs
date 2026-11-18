using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>智能选区当前认的是整扇窗口，还是窗口里的子元素（照上游 <c>ScreenshotIntelligentSelectionTarget</c>）。</summary>
internal enum SmartSelectionTarget
{
    Window,
    WindowSubElement,
}

/// <summary>
/// 智能选区的三档模式 + 命中层级（照上游 <c>ScreenshotIntelligentSelectionModel</c>）。
/// </summary>
/// <remarks>
/// <para>
/// **命中路径从最深的元素排到最外层窗口**：下标 0 = 光标下最细的那个控件，
/// 最后一个 = 整扇窗口。这正是 <c>Views/ScreenSnapTargets</c> 按面积升序给出的顺序。
/// </para>
/// <para>
/// 两种目标层级共用同一条路径，只是取哪一项不同：
/// <see cref="SmartSelectionTarget.WindowSubElement"/> 从 0 开始（滚轮可往父级走），
/// <see cref="SmartSelectionTarget.Window"/> 永远锁在最后一项。
/// <see cref="SmartSelectionMode.Disabled"/> 下整条路径都不收（只能手拖框选）。
/// </para>
/// <para>
/// 这是纯数据模型（只吃 <see cref="Rect"/>），没有 WPF 窗口依赖，所以 <c>--selftest</c>
/// 能直接按上游 tests 的口径断言；时间相关的过渡在 <see cref="SmartSelectionTransition"/>。
/// </para>
/// </remarks>
internal sealed class SmartSelectionModel
{
    private readonly List<Rect> _hits = new();
    private int _index = -1;
    private SmartSelectionMode _mode = SmartSelectionMode.Disabled;
    private bool _explicit;
    private bool _pressActive;
    private Point _pressPosition;
    private Rect _pressSelection = Rect.Empty;

    /// <summary>当前模式（打开覆盖层时由设置灌进来）。</summary>
    public SmartSelectionMode Mode => _mode;

    /// <summary>这次截图是否启用智能选区。</summary>
    public bool SmartSelectionEnabled => _mode != SmartSelectionMode.Disabled;

    /// <summary>当前目标层级：只有 <see cref="SmartSelectionMode.WindowSubElement"/> 认子元素，其余一律整窗。</summary>
    public SmartSelectionTarget Target => _mode == SmartSelectionMode.WindowSubElement
        ? SmartSelectionTarget.WindowSubElement
        : SmartSelectionTarget.Window;

    /// <summary>当前命中路径有几项（0 = 还没算过 / 被清掉了）。</summary>
    public int HitCount => _hits.Count;

    /// <summary>当前选中的层级下标（-1 = 没有）。</summary>
    public int Index => _index;

    /// <summary>当前这一层有没有几何。</summary>
    public bool HasCurrentSelection => _index >= 0 && _index < _hits.Count;

    /// <summary>当前这一层的矩形（窗口 DIP；没有 = <see cref="Rect.Empty"/>）。</summary>
    public Rect CurrentSelection => HasCurrentSelection ? _hits[_index] : Rect.Empty;

    /// <summary>按下过程中（用户还没拖动、也没抬起）。</summary>
    public bool PressActive => _pressActive;

    public Point PressPosition => _pressPosition;

    /// <summary>开始一次截图：清掉上次的临时状态，换上设置里的模式。</summary>
    public void BeginSession(SmartSelectionMode mode)
    {
        ClearTransientState();
        _mode = mode;
    }

    /// <summary>整份回到关闭态（自检 / 关窗用）。</summary>
    public void Reset()
    {
        ClearTransientState();
        _mode = SmartSelectionMode.Disabled;
    }

    public void ClearTransientState()
    {
        ClearHitPath();
        ClearPress();
    }

    public void ClearHitPath()
    {
        _explicit = false;
        _hits.Clear();
        _index = -1;
    }

    public void ClearPress()
    {
        _pressActive = false;
        _pressPosition = default;
        _pressSelection = Rect.Empty;
    }

    /// <summary>
    /// 换模式（设置页改动 / Tab 切换）：关掉就清状态；开着就回到该层级的默认项
    /// （窗口 = 最后一项，子元素 = 第 0 项）。返回是否真的变了。
    /// </summary>
    public bool UpdateMode(SmartSelectionMode mode)
    {
        if (_mode == mode) return false;
        _mode = mode;

        if (!SmartSelectionEnabled)
        {
            ClearTransientState();
            return true;
        }

        ResetTargetPreference();
        ClearPress();
        SetIndex(Target == SmartSelectionTarget.Window ? _hits.Count - 1 : 0);
        return true;
    }

    /// <summary>
    /// 换一条命中路径（光标移到别的窗口 / 别的控件）。路径按「最深 → 最外层」给。
    /// </summary>
    /// <remarks>
    /// 路径没变时**保留用户滚轮选好的层级**（返回 true，但下标不动）；
    /// 路径真的换了就回到该层级的默认项——与上游 <c>applyCanvasHitPath</c> 同口径。
    /// </remarks>
    public bool ApplyHitPath(IReadOnlyList<Rect> hitRects, Rect selectableBounds, double minimumSize)
    {
        if (!SmartSelectionEnabled || hitRects.Count == 0)
        {
            ClearHitPath();
            return false;
        }

        var bounded = BoundedPath(hitRects, selectableBounds, minimumSize);
        if (bounded.Count == 0)
        {
            ClearHitPath();
            return false;
        }

        if (SamePath(bounded, _hits)) return SetIndex(_index);

        _hits.Clear();
        _hits.AddRange(bounded);
        return SetIndex(Target == SmartSelectionTarget.Window ? _hits.Count - 1 : 0);
    }

    /// <summary>
    /// 同一处光标下、UIA 子树后到（比窗口树更细）：只接受「在原有路径前面多出来几项」的扩展。
    /// </summary>
    /// <remarks>
    /// 这条路径只增不减，才能保住用户已经用滚轮 / Tab 选好的那一层：
    /// 用户选了父级元素，子树回来后仍然选父级；用户没动过，就自动落到新露出的最深元素
    /// （上游 <c>applyCanvasRefinementPath</c> 的 Windows 分支）。换了另一条 ancestry
    /// （路径不是单纯在前面加项）返回 false，交给 <see cref="ApplyHitPath"/> 整条替换。
    /// </remarks>
    public bool ApplyRefinementPath(IReadOnlyList<Rect> hitRects, Rect selectableBounds, double minimumSize)
    {
        if (_pressActive ||
            Target != SmartSelectionTarget.WindowSubElement ||
            _hits.Count == 0)
            return false;

        var refined = BoundedPath(hitRects, selectableBounds, minimumSize);
        if (refined.Count <= _hits.Count) return false;

        int added = refined.Count - _hits.Count;
        for (int i = 0; i < _hits.Count; i++)
        {
            if (refined[added + i] != _hits[i]) return false;
        }

        var selected = CurrentSelection;
        _hits.Clear();
        _hits.AddRange(refined);
        return SetIndex(_explicit ? _hits.IndexOf(selected) : 0);
    }

    /// <summary>把当前层级夹到路径里（窗口模式强制最后一项）。</summary>
    public bool SetIndex(int index)
    {
        if (_hits.Count == 0)
        {
            ClearHitPath();
            return false;
        }

        int maximum = _hits.Count - 1;
        _index = Target == SmartSelectionTarget.Window
            ? maximum
            : Math.Clamp(index, 0, maximum);
        return true;
    }

    /// <summary>用户明确选了一层（滚轮 / 点选）：之后 UIA 细化不再自动降回最深。</summary>
    public bool SelectIndex(int index)
    {
        _explicit = true;
        return SetIndex(index);
    }

    /// <summary>Tab：窗口 ↔ 子元素。关闭智能选区时什么也不做。</summary>
    public bool ToggleSelectionTarget()
    {
        if (!SmartSelectionEnabled) return false;
        return UpdateMode(_mode == SmartSelectionMode.Window
            ? SmartSelectionMode.WindowSubElement
            : SmartSelectionMode.Window);
    }

    /// <summary>光标换到另一处了：忘掉用户对层级的明确选择（上游 <c>resetTargetPreference</c>）。</summary>
    public void ResetTargetPreference() => _explicit = false;

    /// <summary>按下：记住按下位置与当时的几何（松手时拿它判断「是确认还是重挑」）。</summary>
    public void BeginPress(Point position, Rect selection)
    {
        _pressActive = true;
        _pressPosition = position;
        _pressSelection = selection;
    }

    /// <summary>按下之后移动了多远才算是拖框（小于这个距离算单击）。</summary>
    public bool ShouldStartManualDrag(Point position, double dragStartDistance)
    {
        if (!_pressActive) return false;

        double dx = position.X - _pressPosition.X;
        double dy = position.Y - _pressPosition.Y;
        if (dragStartDistance <= 0) return true;

        return dx * dx + dy * dy >= dragStartDistance * dragStartDistance;
    }

    /// <summary>取走按下的几何并结束按下状态。</summary>
    public Rect TakePressSelection()
    {
        var selection = _pressSelection;
        ClearPress();
        return selection;
    }

    /// <summary>
    /// 上游 <c>boundedPath</c>：裁到可选范围、丢掉小于最小边长的项、连续重复只留一个。
    /// </summary>
    private static List<Rect> BoundedPath(IReadOnlyList<Rect> rects, Rect bounds, double minimumSize)
    {
        var result = new List<Rect>(rects.Count);
        double min = Math.Max(0, minimumSize);

        foreach (var rect in rects)
        {
            var bounded = Rect.Intersect(rect, bounds);
            if (bounded.IsEmpty) continue;
            if (bounded.Width < min || bounded.Height < min) continue;
            if (result.Count > 0 && result[^1] == bounded) continue;
            result.Add(bounded);
        }

        return result;
    }

    private static bool SamePath(List<Rect> a, List<Rect> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
