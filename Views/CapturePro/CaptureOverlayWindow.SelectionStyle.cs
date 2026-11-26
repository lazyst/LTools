using System;
using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 选区外观（圆角 / 阴影 / 边框）与比例锁：读写 <see cref="SelectionStore"/>，
/// 拖动时套 <see cref="AspectLock"/>，完成时把这一次的几何 + 外观记成「上次选区」。
/// </summary>
/// <remarks>
/// <para>
/// 长度（圆角 / 效果宽）在 <see cref="SelectionStyle"/> 里是**窗口 DIP**，界面显示与导出用物理像素；
/// **X / Y / W / H 一直是物理像素**（与界面、历史、快速集同一套），别在这里换算。
/// </para>
/// <para>
/// 比例几何一律存「高 / 宽」（<see cref="SelectionAspects.HeightOverWidth"/>），
/// 与上游 <c>screenshotSelectionAspectRatioHeightOverWidth</c> 同一口径：
/// <c>_lockedRatio = 0</c> 表示「锁着、但还没定比例」（上游 <c>m_lockedAspectRatio = 0</c>），
/// 这时框选按正方形拖（上游 <c>kNewMarqueeAspectRatio = 1.0</c>），松手后取新选区的比例。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private SelectionStyle _selStyle = SelectionStyle.None;
    private bool _aspectLocked;
    private SelectionAspect _aspect = SelectionAspect.Free;
    private double _lockedRatio;         // 高 / 宽；0 = 还没定

    /// <summary>当前选区外观（预览与导出都读文档里这一份）。</summary>
    internal SelectionStyle SelStyle => _selStyle;

    /// <summary>比例锁是不是开着（上游 <c>aspectRatioLocked()</c>）。</summary>
    internal bool AspectLocked => _aspectLocked;

    /// <summary>下拉里选中的比例预设（自由 = 自定义 / 没锁）。</summary>
    internal SelectionAspect AspectPreset => _aspect;

    /// <summary>打开覆盖层时把上次的外观 / 比例读回来（不落盘，只读缓存）。</summary>
    private void InitSelectionStyle()
    {
        _selStyle = SelectionStore.Style;
        _aspect = SelectionStore.Aspect;
        _aspectLocked = SelectionStore.AspectLocked;
        _lockedRatio = _aspect == SelectionAspect.Free ? 0 : SelectionAspects.HeightOverWidth(_aspect);
        _doc.Style = _selStyle;
        // 导出选项（覆盖层预览不压缩，见 docs/CAPTURE_PARITY.md §5）
        _doc.CompactLayout = SelectionStore.Compaction;
        _doc.CompactSpacing = SelectionStore.CompactionSpacing;
    }

    /// <summary>
    /// 换外观：夹到上游上限、写文档（预览立刻变）并防抖落盘。
    /// 效果宽 / 圆角用「物理像素」改（界面上的数字就是物理像素），这里换回 DIP 存。
    /// </summary>
    private void ApplySelStyle(SelectionStyle style, bool save = true)
    {
        _selStyle = style.Clamp();
        _doc.Style = _selStyle;
        if (save) SelectionStore.SetStyle(_selStyle);
        Surface.InvalidateVisual();
        UpdateChrome();
    }

    /// <summary>
    /// 历史「重新编辑截图」/ 覆盖层里翻历史：把那条记录当时的选区外观（含版面压缩）摆回来。
    /// <c>save: false</c>——采自历史的不是「我上次用的」，别覆盖掉设置里的偏好。
    /// </summary>
    internal void LoadCaptureStyle(CaptureStyleDto? dto)
    {
        if (dto == null) return;

        ApplySelStyle(dto.ToStyle(), save: false);
        _doc.CompactLayout = dto.CompactLayout;
        _doc.CompactSpacing = dto.CompactSpacing;
        SyncArea();
    }

    /// <summary>只改圆角（界面给的是物理像素）。</summary>
    private void SetCornerRadiusPixels(int pixels) =>
        ApplySelStyle(_selStyle with { CornerRadius = DipFromPixels(pixels) });
    /// <summary>只改当前生效的效果宽度（界面给的是物理像素）。</summary>
    private void SetEffectWidthPixels(int pixels) =>
        ApplySelStyle(_selStyle.WithEffectWidth(DipFromPixels(pixels)));

    /// <summary>
    /// 物理像素 → DIP。**不取整**：125% 屏上 1 物理像素 = 0.8 DIP，取整会让滚轮每一下都改不动
    /// （上游的长度本来就是浮点）。显示时再由 <see cref="SelectionStyle.RadiusPixels"/> 换回整数。
    /// </summary>
    private double DipFromPixels(int pixels)
    {
        double s = _doc.Scale > 0 ? _doc.Scale : 1;
        return Math.Max(0, Math.Max(0, pixels) / s);
    }

    // ============ 比例锁 ============

    /// <summary>
    /// 开关比例锁（照上游 <c>setAspectRatioLockEnabled</c>）：
    /// 打开时优先用当前预设，没有预设就取当前选区的比例；关掉时把预设也清成「自由」。
    /// </summary>
    private void ToggleAspectLock()
    {
        SetAspectLocked(!_aspectLocked);
    }

    private void SetAspectLocked(bool locked)
    {
        if (!locked)
        {
            _aspectLocked = false;
            _aspect = SelectionAspect.Free;
            _lockedRatio = 0;
        }
        else
        {
            _aspectLocked = true;
            if (_aspect != SelectionAspect.Free)
            {
                _lockedRatio = SelectionAspects.HeightOverWidth(_aspect);
            }
            else if (_sel is Rect sel && sel.Width >= MinSize && sel.Height >= MinSize)
            {
                _lockedRatio = sel.Height / sel.Width;
            }
            else
            {
                _lockedRatio = 0;      // 还没选区：锁定生效，框选按正方形拖
            }
        }

        SelectionStore.SetAspect(_aspectLocked, _aspect);
        UpdateChrome();
    }

    /// <summary>
    /// 选一个比例（照上游 <c>setAspectRatioPreset</c>）：自由 = 解锁；
    /// 其余先把当前选区套成那个比例（单块矩形才改几何），再锁定。
    /// </summary>
    private void SetAspectPreset(SelectionAspect aspect)
    {
        if (aspect == SelectionAspect.Free)
        {
            SetAspectLocked(false);
            return;
        }

        double ratio = SelectionAspects.HeightOverWidth(aspect);
        if (ratio <= 0) return;

        // 只有「单块矩形」能直接套比例（多块 / 自由 / 折线仍各自保留几何，上游 resizable() 同义）
        if (_sel is Rect sel && sel.Width >= MinSize && sel.Height >= MinSize &&
            _regions.Count == 1 && _regions[0] is { Mode: RegionMode.Add, IsPolygon: false })
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            var resized = AspectLock.Apply(sel, bounds, ratio, MinSize);
            if (!resized.IsEmpty && resized.Width >= MinSize && resized.Height >= MinSize)
            {
                SetSingleRect(resized);
                SyncArea();      // 文档里的物理像素跟着走：信息条 / 预览 / 导出都读它
            }
        }

        _aspect = aspect;
        _aspectLocked = true;
        _lockedRatio = ratio;
        SelectionStore.SetAspect(true, aspect);
        UpdateChrome();
    }

    /// <summary>
    /// 这一次拖动该用的「高 / 宽」：0 = 不锁。
    /// 框选在「锁着但还没定比例」时按正方形（上游 <c>kNewMarqueeAspectRatio</c>）。
    /// 另外按住「锁当前比例」的键（<c>selection_aspect_ratio_snap</c>，默认 Q）也临时锁住
    /// 按下那一刻的比例——上游允许提前按住再拖。
    /// </summary>
    private double DragRatio(bool marquee)
    {
        if (_aspectLocked)
        {
            if (_lockedRatio > 0) return _lockedRatio;
            return marquee ? 1.0 : 0.0;
        }

        if (CaptureShortcutTable.Held(CaptureShortcutScope.Screenshot, "selection_aspect_ratio_snap"))
        {
            if (_originRect.Width > 0.5) return _originRect.Height / _originRect.Width;
            return 1.0;
        }

        return 0;
    }

    /// <summary>
    /// 一次拖动 / 框选结束后的收尾（照上游 <c>finalizeAspectRatio</c>）：
    /// 锁着但还没定比例 → 取这次的选区比例；预设与结果对不上 → 降级成自定义比例（不动下次的预设）。
    /// </summary>
    private void FinalizeAspect()
    {
        if (!_aspectLocked) return;
        if (_sel is not Rect sel || sel.Width < MinSize || sel.Height < MinSize) return;

        double ratio = sel.Height / sel.Width;
        if (_aspect != SelectionAspect.Free)
        {
            double preset = SelectionAspects.HeightOverWidth(_aspect);
            if (Math.Abs(ratio - preset) > 0.002)
            {
                // 某次手势盖过了预设：保留结果当自定义锁，但别改「下次用什么预设」的偏好
                _aspect = SelectionAspect.Free;
                _lockedRatio = ratio;
                SelectionStore.SetAspect(true, SelectionAspect.Free);
                return;
            }

            _lockedRatio = preset;
            return;
        }

        if (_lockedRatio <= 0)
        {
            _lockedRatio = ratio;
            SelectionStore.SetAspect(true, SelectionAspect.Free);
        }
    }

    /// <summary>完成一次截图：把选区几何 + 外观记成「上次选区」，供下次快速集取用。</summary>
    private void RememberSelection()
    {
        SelectionStore.Commit(_doc.Area, _selStyle, _aspectLocked);
    }
}
