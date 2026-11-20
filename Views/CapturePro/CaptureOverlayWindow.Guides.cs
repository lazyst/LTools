using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 覆盖层的界面偏好接线：参考线（Alt 开关）、选区框 / 压暗色、提示条不透明度、
/// 工具条尺寸、取色 / 信息条的单位与格式。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot_ui/*</c>：参考线三条（光标虚线十字 / 显示器中心实线 / 选区中心实线），
/// 每条颜色按取样处自动反色；<c>Alt</c> 轻按开关（<c>toggle_guides</c> 的默认键），
/// 按住 Alt 去拖「减区」不算轻按。
/// </para>
/// <para>
/// 读写都在 <see cref="OverlayUiSettings"/>（落 <c>configs/settings.json</c> 的 <c>Screenshot</c> 段）。
/// 这里只负责把值搬到窗口 / 画布上，不自己存一份。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>这一次覆盖层要不要画参考线（初值取 <c>show_guides_by_default</c>，Alt 可随时切换）。</summary>
    private bool _guidesVisible;

    /// <summary>参考线的光标位置（窗口 DIP）。</summary>
    private Point _guideCursor;

    /// <summary>Alt 按下了、还没抬起。</summary>
    private bool _altHeld;

    /// <summary>这一次 Alt 已经被「减区」用掉了：抬起时不再切成参考线。</summary>
    private bool _altConsumed;

    /// <summary>Shift 按下了、还没抬起（取色器里轻按 Shift = 换色值格式）。</summary>
    private bool _shiftHeld;

    /// <summary>这一次 Shift 已经被「加区 / 锁正方形」用掉了。</summary>
    private bool _shiftConsumed;

    /// <summary>把界面偏好灌进覆盖层（构造里调一次）。</summary>
    private void InitOverlayUi()
    {
        _guidesVisible = OverlayUiSettings.ShowGuidesByDefault;
        _guideCursor = new Point(ActualWidth / 2, ActualHeight / 2);

        Surface.ShowGuides = _guidesVisible;
        Surface.GuideCursor = _guideCursor;
        Surface.SelectionMaskColor = OverlayUiSettings.SelectionMaskColor;
        Surface.SelectionBorderColor = OverlayUiSettings.SelectionBorderColor;

        HintBox.Opacity = OverlayUiSettings.ShortcutHintAlpha;

        // 工具条尺寸（上游 setPaletteScaleMultiplier：small = ×0.8）
        Toolbar.LayoutTransform = OverlayUiSettings.ToolbarTransform();
    }

    /// <summary>参考线上的一组颜色：每条都按取样处反色（<c>adaptive_guide_colors</c>）。</summary>
    private void UpdateGuideColors()
    {
        Surface.GuideCursorColor = GuideColor(OverlayUiSettings.CursorGuideColor, _guideCursor);

        var monitor = new Point(Math.Max(0, ActualWidth) / 2, Math.Max(0, ActualHeight) / 2);
        Surface.GuideMonitorColor = GuideColor(OverlayUiSettings.MonitorCenterGuideColor, monitor);

        if (_sel is Rect sel && IsValid(sel))
        {
            var center = new Point(sel.X + sel.Width / 2, sel.Y + sel.Height / 2);
            Surface.GuideSelectionColor = GuideColor(OverlayUiSettings.SelectionCenterGuideColor, center);
        }
        else
        {
            Surface.GuideSelectionColor = Colors.Transparent;
        }
    }

    /// <summary>取一个点的参考线颜色（跟背景太像就反色）；透明 / 关掉自适应时原样返回。</summary>
    private Color GuideColor(Color configured, Point dip)
    {
        if (configured.A == 0 || !OverlayUiSettings.AdaptiveGuideColors) return configured;

        var (x, y) = ToImage(dip);
        return GuideLines.Adaptive(configured, PickerColor.Read(_doc.Base, x, y));
    }

    /// <summary>鼠标动了：记住光标位置；参考线开着时再更新十字中心与三条颜色。</summary>
    private void UpdateGuides(Point p)
    {
        bool changed = p != _guideCursor;
        _guideCursor = p;
        if (!_guidesVisible || !changed) return;

        Surface.GuideCursor = p;
        UpdateGuideColors();
        Surface.InvalidateVisual();
    }

    /// <summary>Alt 轻按：参考线开 / 关（<c>toggle_guides</c> 的默认键）。</summary>
    private void ToggleGuides()
    {
        _guidesVisible = !_guidesVisible;

        if (_guidesVisible)
        {
            Surface.GuideCursor = _guideCursor;
            UpdateGuideColors();
        }

        Surface.ShowGuides = _guidesVisible;
        Surface.InvalidateVisual();

        RefreshHint();
        PlaceHint();
    }

    /// <summary>按下的修饰键是不是 Alt（WPF 把 Alt 报成 <c>Key.System</c> + <c>SystemKey</c>）。</summary>
    private static bool IsAltKey(KeyEventArgs e) =>
        e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
        (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt);

    /// <summary>按下的修饰键是不是 Shift。</summary>
    private static bool IsShiftKey(KeyEventArgs e) => e.Key is Key.LeftShift or Key.RightShift;

    /// <summary>Alt / Shift 按下了：记一笔，等抬起时判断这一下有没有被别的操作吃掉。</summary>
    private void TrackModifierDown(KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            _altHeld = true;
            _altConsumed = false;
        }
        else if (IsShiftKey(e))
        {
            _shiftHeld = true;
            _shiftConsumed = false;
        }
    }

    /// <summary>Alt / Shift 抬起了：轻按就走它们的「单击」语义（参考线 / 换色值格式）。</summary>
    private void TrackModifierUp(KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            bool tap = _altHeld && !_altConsumed && _drag == DragMode.None &&
                       !_textEditing && !_scrollMode &&
                       CaptureShortcutTable.BindsBareModifier(CaptureShortcutScope.Screenshot,
                           "toggle_guides", 0xA4);
            _altHeld = false;
            _altConsumed = false;
            if (tap) ToggleGuides();
        }
        else if (IsShiftKey(e))
        {
            bool tap = _shiftHeld && !_shiftConsumed && _drag == DragMode.None &&
                       !_textEditing && !_scrollMode;
            _shiftHeld = false;
            _shiftConsumed = false;

            // 取色器里轻按 Shift = 换色值格式（上游 keep_selection_width_and_height_consistent
            // 在取色时那一下「没用来拖」的轻按）；格式存进 settings 的 color_picker_format。
            // 选择工具下同样有效（光标旁那块取色卡本来就跟着走，上游 cycle_color_format 看 moveToolActive）。
            if (tap && _tool is CaptureTool.Picker or CaptureTool.Select &&
                CaptureShortcutTable.BindsBareModifier(CaptureShortcutScope.Screenshot,
                    "keep_selection_width_and_height_consistent", 0xA0))
            {
                OverlayUiSettings.SetColorPickerFormat(
                    OverlayUiSettings.NextColorFormat(OverlayUiSettings.ColorPickerFormat));
                RefreshHint();
                PlaceHint();
                ShowPickerAt(_guideCursor);
            }
        }
    }

    /// <summary>这一下拖动用掉了 Alt / Shift（减区 / 加区 / 锁正方形），别在抬起时再当轻按。</summary>
    private void ConsumeModifiers(bool alt, bool shift)
    {
        if (alt) _altConsumed = true;
        if (shift) _shiftConsumed = true;
    }
}
