using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 光标旁那块取色 / 坐标卡（照上游 <c>ScreenshotColorPickerWindow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 三行：上面 11 × 11 源像素按 12 倍放大（最近邻，中间那一格描边 = 取样点），
/// 中间 <c>X: … Y: …</c>，下面一条色带写当前格式的色值（黑 / 白字按底色自动挑）。
/// </para>
/// <para>
/// **选择工具与取色工具下都跟着光标**（上游的 <c>moveToolActive</c> 那一档）：
/// 显示时机仍由 <c>screenshot_ui/color_picker_display_mode</c> 说了算
/// （选区外不显示 / 总是显示 / 总是不显示），光标停在工具条 / 信息条上时收起。
/// </para>
/// <para>
/// 读的是冻结底图（<see cref="CaptureDocument.Base"/>），不是画好的成品——标注与压暗不参与；
/// 也不要求先框选：整个冻结画面都能取。取色工具下单击按当前格式复制颜色，**不关闭覆盖层**
/// （连着取几个是常态）；<c>Esc</c> 退回选择工具。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>光标与卡片之间的空（照上游 <c>kCursorGap</c>）。</summary>
    private const double PickerGap = 16;

    /// <summary>卡片离窗口边缘至少留多少。</summary>
    private const double PickerEdge = 8;

    /// <summary>预览格的圆角（上游 <c>kPanelRadius</c>；只有上面两个角真的被裁）。</summary>
    private const double PickerRadius = 5;

    private bool _pickerShown;

    /// <summary>上一次写进卡片的「取样点 + 色值格式」：同一点同一个格式就不重建预览。</summary>
    private (int X, int Y, string Format)? _pickerSample;

    /// <summary>预览格用的 11 × 11 位图（复用，不每次 new）。</summary>
    private WriteableBitmap? _pickerPatch;

    /// <summary>冻结底图的 BGRA32 视图（底图不是这个格式时转一次就缓存；重新截图换了底图会重转）。</summary>
    private BitmapSource? _pickerBase;
    private BitmapSource? _pickerBaseSource;

    /// <summary>卡片量好的尺寸（固定不变，量一次就够）。</summary>
    private Size _pickerSize;

    /// <summary>
    /// 按当前光标位置摆好这块卡片（该显示就显示、该收就收）。
    /// 鼠标移动、切工具、选区变化、松手之后都调它。
    /// </summary>
    private void ShowPickerAt(Point p)
    {
        if (!PickerVisibleHere(p))
        {
            HidePicker();
            return;
        }

        var (x, y) = ToImage(p);
        BitmapSource baseImage = PickerBase();
        if (PickerColor.Read(baseImage, x, y) is not Color color)
        {
            HidePicker();      // 越界（窗口边缘）只是没取到，不报错
            return;
        }

        string format = OverlayUiSettings.ColorPickerFormat;
        if (!_pickerShown || _pickerSample != (x, y, format))
        {
            _pickerSample = (x, y, format);
            UpdatePickerPreview(baseImage, x, y);

            PickerText.Text = PickerColor.Format(color, format);

            Color ink = PickerColor.ReadableTextColor(color);
            PickerColorBar.Background = new SolidColorBrush(color);
            PickerText.Foreground = new SolidColorBrush(ink);
            PickerCenterMark.Stroke = new SolidColorBrush(ink);
        }

        // 坐标每一下都可能变（相对模式还跟着选区走），不必跟着预览一起省
        PickerPosText.Text = PickerPositionText(x, y);

        PickerChip.Visibility = Visibility.Visible;
        _pickerShown = true;

        UpdatePickerReticle(p, color);
        PlacePickerChip(p);
    }

    /// <summary>冻结底图的 BGRA32 视图：不是这个格式就转一次缓存起来（重新截图换了底图会重转）。</summary>
    private BitmapSource PickerBase()
    {
        if (_pickerBase != null && ReferenceEquals(_pickerBaseSource, _doc.Base)) return _pickerBase;

        _pickerBaseSource = _doc.Base;
        _pickerBase = _doc.Base.Format == PixelFormats.Bgra32
            ? _doc.Base
            : new FormatConvertedBitmap(_doc.Base, PixelFormats.Bgra32, null, 0);
        return _pickerBase;
    }

    /// <summary>
    /// 现在这一刻该不该显示（照上游 <c>screenshotColorPickerVisible</c>）：
    /// 只有选择 / 取色工具下有这块卡片；光标停在工具条 / 信息条 / 文字输入上时收起。
    /// </summary>
    private bool PickerVisibleHere(Point p)
    {
        if (_doc.Base == null || _scrollMode || _textEditing) return false;
        if (_tool is not (CaptureTool.Select or CaptureTool.Picker)) return false;

        // 贴图内标注（EditOnly）没有「选择阶段」：只有取色工具下才出卡片
        if (EditOnly && _tool != CaptureTool.Picker) return false;

        if (IsOverToolbar || IsOverSelectionBar) return false;

        bool hasSelection = _sel is Rect s && IsValid(s);
        bool inside = false;

        if (hasSelection)
        {
            // 上游按 4 物理像素的容差判「在选区内」
            double tolerance = 4.0 / Math.Max(0.01, _doc.Scale);
            var area = _sel!.Value;
            area.Inflate(tolerance, tolerance);
            inside = area.Contains(p);
        }

        return OverlayUiSettings.PickerVisible(hasSelection, inside,
            intelligentSelecting: SmartPreviewActive, dragging: _drag != DragMode.None,
            manualSelecting: true);
    }

    /// <summary>选区 / 工具 / 光标状态变了：按最后一次光标位置重算这块卡片在不在。</summary>
    private void RefreshPicker()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _doc.Base == null) return;

        if (_tool is CaptureTool.Select or CaptureTool.Picker) ShowPickerAt(_guideCursor);
        else HidePicker();
    }

    /// <summary>把光标周围 11 × 11 的像素写进预览格（越界时边缘像素重复，照上游）。</summary>
    private void UpdatePickerPreview(BitmapSource image, int x, int y)
    {
        byte[]? block = PickerColor.ReadPatch(image, x, y);
        if (block == null) return;

        int size = PickerColor.PatchSize;
        _pickerPatch ??= new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        _pickerPatch.WritePixels(new Int32Rect(0, 0, size, size), block, size * 4, 0);

        // 预览格只把上面两个角裁圆（照上游 topRoundedRectPath）：底图是方的，
        // Border 的 CornerRadius 不会裁子元素，所以这里自己给一张裁剪几何。
        PickerPreview.Clip ??= TopRoundedClip(PickerColor.PatchCanvas, PickerColor.PatchCanvas,
            PickerRadius);

        PickerPreview.Source = _pickerPatch;
        PickerPreview.InvalidateVisual();
    }

    /// <summary>上面两个角是圆的、下面两个角是方的矩形裁剪区。</summary>
    private static Geometry TopRoundedClip(double width, double height, double radius)
    {
        double r = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext c = geometry.Open())
        {
            c.BeginFigure(new Point(r, 0), isFilled: true, isClosed: true);
            c.LineTo(new Point(width - r, 0), true, false);
            c.ArcTo(new Point(width, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(width, height), true, false);
            c.LineTo(new Point(0, height), true, false);
            c.LineTo(new Point(0, r), true, false);
            c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>摆在光标右下（放不下翻到左边 / 上边，再夹回窗口里），照上游 <c>cursorPanelPosition</c>。</summary>
    private void PlacePickerChip(Point p)
    {
        if (_pickerSize.IsEmpty)
        {
            PickerChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _pickerSize = PickerChip.DesiredSize;
        }

        double w = _pickerSize.Width, h = _pickerSize.Height;
        double left = p.X + PickerGap;
        double top = p.Y + PickerGap;

        if (left + w > ActualWidth - PickerEdge) left = p.X - PickerGap - w;
        if (top + h > ActualHeight - PickerEdge) top = p.Y - PickerGap - h;

        Canvas.SetLeft(PickerChip,
            Math.Clamp(left, PickerEdge, Math.Max(PickerEdge, ActualWidth - w - PickerEdge)));
        Canvas.SetTop(PickerChip,
            Math.Clamp(top, PickerEdge, Math.Max(PickerEdge, ActualHeight - h - PickerEdge)));
    }

    /// <summary>
    /// 取样点的十字与小方框：只在取色工具下画在画面上（选择工具下画面上已经有选框，
    /// 再叠一个十字只会碍事——卡片里那一格描边已经说明了取的是哪一格）。
    /// </summary>
    private void UpdatePickerReticle(Point p, Color color)
    {
        if (_tool != CaptureTool.Picker)
        {
            ClearPickerReticle();
            return;
        }

        Surface.PickerPoint = p;
        Surface.PickerGuideColor = GuideColor(OverlayUiSettings.PickerCenterGuideColor, p);
        Surface.PickerMarkerColor = GuideLines.Readable(color);
        Surface.InvalidateVisual();
    }

    private void ClearPickerReticle()
    {
        if (Surface.PickerPoint == null) return;

        Surface.PickerPoint = null;
        Surface.InvalidateVisual();
    }

    private string PickerPositionText(int imageX, int imageY)
    {
        int x = _doc.Monitor.X + imageX;
        int y = _doc.Monitor.Y + imageY;

        if (OverlayUiSettings.RelativePickerCoordinates && _sel is Rect sel && IsValid(sel))
        {
            var (x1, y1) = ToImage(sel.TopLeft);
            x -= x1;
            y -= y1;
        }

        // 与选区信息条同一个口径：物理像素 / 逻辑单位（dp）
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        return PickerColor.PositionText(OverlayUiSettings.DisplayValue(x, scale),
            OverlayUiSettings.DisplayValue(y, scale));
    }

    private void HidePicker()
    {
        if (_pickerShown)
        {
            _pickerShown = false;
            _pickerSample = null;
            PickerChip.Visibility = Visibility.Collapsed;
        }

        ClearPickerReticle();
    }

    /// <summary>
    /// 单击取色：按当前格式复制色值。**不关闭覆盖层**（连着取几个是常态），也不落标注。
    /// </summary>
    private void PickColor(Point p)
    {
        var (x, y) = ToImage(p);
        if (PickerColor.Read(_doc.Base, x, y) is not Color color)
        {
            ShowPickerAt(p);      // 越界（窗口边缘）只是没取到，不报错
            return;
        }

        ShowPickerAt(p);

        string text = PickerColor.Format(color, OverlayUiSettings.ColorPickerFormat);
        if (NativeClipboard.SetText(text))
            ToastService.Show($"已复制 {text}", "取色", 1600);
        else
            ToastService.Show("复制颜色失败（剪贴板被占用）", "取色", 0, ToastKind.error);
    }
}
