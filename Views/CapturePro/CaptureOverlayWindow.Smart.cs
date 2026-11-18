using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 智能选区：光标停在哪就预览哪个窗口 / 子元素，单击确认、滚轮换层级、Tab 切整窗 / 子元素、
/// 拖动 = 从按下点开始手拖框选（照上游 <c>ScreenshotIntelligentSelectionModel</c> +
/// <c>ScreenshotSmartSelectionTransition</c>）。
/// </summary>
/// <remarks>
/// <para>
/// **预选就是真选区**：<see cref="_regions"/> / <see cref="_sel"/> 与手工框选完全一样，
/// 所以 Enter / Ctrl+C / 复制这些出口不用另开分支；只是主工具条收起、信息条只读、
/// 画面几何走过渡插值（<see cref="SmartSelectionTransition"/>）。
/// </para>
/// <para>
/// 命中路径来自 <c>UpdateHover</c> 那份候选（按面积升序 = 最深元素 → 最外层窗口），
/// 与上游「路径从最细的元素排到整扇窗口」一致：下标 0 = 子元素、最后一项 = 整窗。
/// </para>
/// <para>
/// 过渡由「只在动画期间跑」的 8ms <c>DispatcherTimer</c> 喂时间：上游没有自己的定时器，
/// presentation 每帧带时间进来；这里同样按**绝对时间**算进度，帧率不齐也不会改变轨迹。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private readonly SmartSelectionModel _smart = new();
    private readonly SmartSelectionTransition _smartFx = new();
    private readonly Stopwatch _smartClock = Stopwatch.StartNew();

    private DispatcherTimer? _smartTicker;
    private bool _smartPreviewActive;
    private IntPtr _smartTop;                             // 上次命中的顶层窗口（换了就忘掉层级偏好）
    private List<SnapBox> _smartBoxes = new();            // 与命中路径一一对应（取标签用）
    private Rect _smartShown = Rect.Empty;                // 上一次交给过渡的目标（= 界面上正在显示的那块）

    private long SmartNow => _smartClock.ElapsedMilliseconds;

    /// <summary>智能选区的纯模型（自检 / 冒烟直接断言）。</summary>
    internal SmartSelectionModel SmartModel => _smart;

    /// <summary>换层级用的过渡插值（自检 / 冒烟直接断言）。</summary>
    internal SmartSelectionTransition SmartTransition => _smartFx;

    /// <summary>现在显示的是智能选区预选（还没确认）。</summary>
    internal bool SmartPreviewActive => _smartPreviewActive && SmartSelectionAvailable;

    /// <summary>当前预选的那一层叫什么（信息条 / 提示里用）。</summary>
    internal string SmartTargetLabel
    {
        get
        {
            int index = _smart.Index;
            return index >= 0 && index < _smartBoxes.Count ? _smartBoxes[index].Label : "元素";
        }
    }

    /// <summary>这次截图能不能智能选区：只在「矩形、选择工具、还没有选区」时生效。</summary>
    /// <remarks>
    /// 照上游 <c>interaction.intelligentSelecting()</c> 的边界：贴图内标注、翻历史、长截图、
    /// 打字、折线 / 自由形状、自动打码都不掺和；已经有选区时（用户确认过 / 手框过）也不再预选，
    /// 回到「选择模式」只能靠右键取消选区（上游 <c>returnToSelection</c> 同义）。
    /// </remarks>
    internal bool SmartSelectionAvailable =>
        _smart.SmartSelectionEnabled &&
        !EditOnly && !BrowsingHistory && !_scrollMode && !_textEditing &&
        _tool == CaptureTool.Select && _shape == SelectionMode.Rect &&
        !_selPolyActive &&
        (_regions.Count == 0 || _smartPreviewActive);

    /// <summary>打开覆盖层时把设置里的档位灌进模型（默认子元素 + 柔和过渡）。</summary>
    private void InitSmartSelection()
    {
        _smart.BeginSession(SelectionStore.SmartSelection);
        _smartFx.SetAnimation(SelectionStore.Transition, 0);
    }

    /// <summary>
    /// 光标下的命中路径换了：把它交给模型，变化了就更新预选。
    /// <paramref name="refine"/> = UIA 子树后到（比窗口树更细），只走「只增不减」的细化路径。
    /// </summary>
    private void UpdateSmartTarget(IntPtr top, bool refine)
    {
        // 按下期间冻结命中路径（上游 handleRefinement 遇到 pressActive 直接返回）
        if (_smart.PressActive) return;

        // 换了目标窗口 = 忘掉用户对层级的明确选择（上游 targetChanged → resetTargetPreference）
        if (top != _smartTop)
        {
            _smartTop = top;
            _smart.ResetTargetPreference();
        }

        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var path = new List<Rect>(_hoverCandidates.Count);
        var boxes = new List<SnapBox>(_hoverCandidates.Count);

        foreach (var box in _hoverCandidates)
        {
            Rect clipped = Rect.Intersect(ToDipRect(box.Bounds), bounds);
            if (clipped.IsEmpty || clipped.Width < MinSize || clipped.Height < MinSize) continue;
            if (path.Count > 0 && path[^1] == clipped) continue;   // 连续重复只留一个（模型里也会做）
            path.Add(clipped);
            boxes.Add(box);
        }

        if (path.Count == 0)
        {
            SetHover(null);
            if (HideSmartPreview()) UpdateChrome();
            return;
        }

        Rect before = _smartShown;

        bool accepted = refine && _smart.ApplyRefinementPath(path, bounds, MinSize);
        if (!accepted) accepted = _smart.ApplyHitPath(path, bounds, MinSize);

        // 预过滤与模型的 boundedPath 同一套规则（幂等），所以下标与 boxes 一一对应
        _smartBoxes = boxes;

        if (!accepted || !_smart.HasCurrentSelection)
        {
            SetHover(null);
            if (HideSmartPreview()) UpdateChrome();
            return;
        }

        Rect target = _smart.CurrentSelection;
        if (_smartPreviewActive && target == before) return;   // 界面上已经是这块：别每移动一像素就重建界面

        SetHover(null);
        ShowSmartPreview(target);
    }

    /// <summary>把某一层显示成预选：选区是真选区，画面几何交给过渡插值。</summary>
    private void ShowSmartPreview(Rect target)
    {
        if (!_smart.HasCurrentSelection || !SmartSelectionTransition.HasSelection(target))
        {
            if (HideSmartPreview()) UpdateChrome();
            return;
        }

        _smartPreviewActive = true;
        _smartShown = target;

        _regions.Clear();
        _regions.Add(SelectionRegion.FromRect(RegionMode.Add, target));
        RefreshSelection();

        _smartFx.Update(target, smartFraming: true, SmartNow);
        if (_smartFx.IsRunning) StartSmartTicker();
        else StopSmartTicker();

        UpdateChrome();
    }

    /// <summary>收起预选（选区一起清掉）；返回刚才是不是在预选。</summary>
    private bool HideSmartPreview()
    {
        StopSmartTicker();
        bool was = _smartPreviewActive;
        _smartPreviewActive = false;
        _smartShown = Rect.Empty;

        if (was)
        {
            _regions.Clear();
            _sel = null;
            _replaceOnCommit = false;
            _liveRect = null;
        }

        return was;
    }

    /// <summary>
    /// 结束预选但**保留这块选区**（切工具 / 局部调整）：之后按普通选区编辑，
    /// 模型清空 = 上游 <c>pauseIntelligentSelection</c>，要右键取消选区才会重新开始认。
    /// </summary>
    private void EndSmartPreviewKeepingSelection()
    {
        if (!_smartPreviewActive) return;
        StopSmartTicker();
        _smartPreviewActive = false;
        _smartShown = Rect.Empty;
        _smart.ClearTransientState();
        SyncArea();
    }

    /// <summary>确认这一块（单击预选内）：变成普通选区，智能选区暂停到下一次「选择模式」。</summary>
    private void ConfirmSmartSelection(Rect rect)
    {
        StopSmartTicker();
        _smartPreviewActive = false;
        _smartShown = Rect.Empty;
        _smart.ClearTransientState();
        SetSingleRect(rect);
        SyncArea();
        UpdateChrome();
    }

    /// <summary>从按下点开始手拖框选（上游 handleIntelligentSelectionMove 的拖动分支）。</summary>
    private void BeginSmartMarquee(Point anchor)
    {
        StopSmartTicker();
        _smartPreviewActive = false;
        _smartShown = Rect.Empty;
        _smart.ClearTransientState();

        _regions.Clear();
        _sel = null;
        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = new Rect(anchor, anchor);

        _mode = RegionMode.Add;
        _squareDrag = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _origin = anchor;
        _pressAt = anchor;
        _pressMoved = true;          // 之后松手是「框选提交」，不是单击选中
        _handle = FrameHandle.None;
        _drag = DragMode.New;

        SetHover(null);
        RebuildSnapLines();
    }

    /// <summary>滚轮换层级：往上 = 外层（整窗），往下 = 更细的子元素（上游 stepSelectionLevel）。</summary>
    private void StepSmartLevel(int step)
    {
        if (!_smartPreviewActive || !_smart.HasCurrentSelection || _smart.HitCount < 2) return;
        _smart.SelectIndex(_smart.Index + step);      // 模型自己夹到路径范围内
        ShowSmartPreview(_smart.CurrentSelection);
    }

    /// <summary>Tab：窗口 ↔ 窗口子元素（上游 toggle_selection_between_window_and_window_sub_element）。</summary>
    private void ToggleSmartTarget()
    {
        if (!_smartPreviewActive || _drag != DragMode.None) return;
        if (!_smart.ToggleSelectionTarget()) return;

        _smart.ClearPress();

        // 换层级之后重新命一次：窗口模式一定落到最外层
        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
        {
            UpdateHover(Clamp(dip));
            return;
        }

        if (_smart.HasCurrentSelection) ShowSmartPreview(_smart.CurrentSelection);
    }

    // ============ 过渡动画 ============

    private void StartSmartTicker()
    {
        _smartTicker ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };
        _smartTicker.Tick -= OnSmartTick;
        _smartTicker.Tick += OnSmartTick;
        if (!_smartTicker.IsEnabled) _smartTicker.Start();
    }

    private void StopSmartTicker()
    {
        if (_smartTicker is { IsEnabled: true }) _smartTicker.Stop();
    }

    private void OnSmartTick(object? sender, EventArgs e)
    {
        if (!SmartPreviewActive)
        {
            StopSmartTicker();
            return;
        }

        _smartFx.Advance(SmartNow);
        RepaintSmartFrame();
        if (!_smartFx.IsRunning) StopSmartTicker();
    }

    /// <summary>只重画「跟着插值走」的那几件（画面选区 + 把手 + 信息条），不重建整层界面。</summary>
    private void RepaintSmartFrame()
    {
        if (!SmartPreviewActive) return;

        Rect shown = _smartFx.Displayed;
        if (!SmartSelectionTransition.HasSelection(shown)) shown = _smart.CurrentSelection;

        Surface.Selection = shown;
        Surface.SelectionGeometry = new RectangleGeometry(shown);
        Surface.InvalidateVisual();

        PositionGrips(shown, true);
        ShowSelectionBar(shown);
    }
}
