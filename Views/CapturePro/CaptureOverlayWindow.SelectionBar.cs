using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 顶部选区信息条：显示 X / Y / 宽 / 高 / 圆角 / 效果宽（屏幕物理像素），
/// 滚轮改 ±1、单击开参数框、锁图标切比例锁、比例下拉选快速集 / 预设。
/// </summary>
/// <remarks>
/// <para>
/// 行为照上游 <c>ScreenshotSelectionToolbarWidget</c>：字段滚轮走 <c>adjustFromToolbar</c>
/// （X / Y = 整体平移，W / H = 改对应边，锁比例时按比例联动、左上角锚定），
/// 圆角 / 效果宽各自 ±1 并夹到上限。摆位照 <c>selectionToolbarContentPosition</c>
/// （上 → 右 → 左 → 下 → 夹回可见区）。
/// </para>
/// <para>
/// 单击 = 打开参数对话框（<see cref="SelectionParamsWindow"/>），只点信息条上的字段才开，
/// 点条子的空白内边距什么都不做（别让它顺手起一个框选）。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>信息条的主色（上游 <c>PanelPrimaryColor</c> = #1677FF）。</summary>
    private static readonly Color BarPrimary = Color.FromRgb(0x16, 0x77, 0xFF);

    /// <summary>圆角对这种选区有没有意义：只有「单块矩形加区」（点列 / 自定义路径没有圆角）。</summary>
    private bool CornerRadiusApplicable =>
        _regions.Count == 1 && _regions[0] is { Mode: RegionMode.Add, IsPolygon: false };

    /// <summary>鼠标现在是不是停在信息条 / 比例下拉上（移动时别更新悬停候选 / 改光标）。</summary>
    private bool IsOverSelectionBar => SelBar.IsVisible &&
        (SelBar.IsMouseOver || (IsAspectMenuOpen && SelAspectMenu.IsMouseOver));

    /// <summary>比例下拉等「抬手」再开（按下就开会被这一下 MouseUp 立刻关掉）。</summary>
    private bool _pendingAspectMenu;

    /// <summary>比例下拉是不是开着。</summary>
    private bool IsAspectMenuOpen => SelAspectMenu.Visibility == Visibility.Visible;

    /// <summary>比例下拉面板的最大高度（照上游 token）。</summary>
    private const double AspectMenuMaxHeight = 320;

    /// <summary>事件源是不是落在比例下拉面板里（点它不能开始框选 / 也不能当成点外面）。</summary>
    private bool IsOnAspectMenu(DependencyObject? source) => IsWithin(source, SelAspectMenu);

    /// <summary>事件源是不是落在信息条上（点它不能开始框选 / 取消选区）。</summary>
    private bool IsOnSelectionBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, SelBar)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>事件源落在哪个可滚轮的字段上（不在任何字段上 = null）。</summary>
    private SelectionField? BarFieldAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { Tag: string tag })
            {
                SelectionField? field = tag switch
                {
                    "X" => SelectionField.X,
                    "Y" => SelectionField.Y,
                    "W" => SelectionField.Width,
                    "H" => SelectionField.Height,
                    "R" => SelectionField.Radius,
                    "E" => SelectionField.EffectWidth,
                    _ => null,
                };
                if (field != null) return field;
            }

            if (ReferenceEquals(source, SelBar)) return null;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>信息条这一下点击该怎么处理（由 <c>Window_MouseDown</c> 调用，自己置 Handled）。</summary>
    private void HandleSelectionBarClick(DependencyObject? source)
    {
        // 智能选区预选态：信息条只读（上游 selectionEditable = false），点它别把参数框拉起来
        if (SmartPreviewActive) return;

        if (IsWithin(source, SelLockBtn))
        {
            CloseAspectMenu();
            ToggleAspectLock();
            return;
        }

        if (IsWithin(source, SelAspectBtn))
        {
            // 再点一次 = 收起；否则在**抬起**时开（按下就开会被这一下 MouseUp 立刻关掉）
            if (IsAspectMenuOpen)
            {
                CloseAspectMenu();
                return;
            }

            _pendingAspectMenu = true;
            return;
        }

        if (BarFieldAt(source) is SelectionField field)
        {
            CloseAspectMenu();
            OpenSelectionParams(field);
        }
    }

    /// <summary>同步信息条上的数字 / 锁颜色 / 比例名（按「显示单位」换算：物理像素 px / 逻辑像素 dp）。</summary>
    private void SyncSelectionBar()
    {
        if (CurrentSelectionParams() is not SelectionParams p) return;

        double scale = _doc.Scale > 0 ? _doc.Scale : 1;

        SelXText.Text = OverlayUiSettings.DisplayValue(p.X, scale).ToString();
        SelYText.Text = OverlayUiSettings.DisplayValue(p.Y, scale).ToString();
        SelWText.Text = OverlayUiSettings.DisplayValue(p.Width, scale).ToString();
        SelHText.Text = OverlayUiSettings.DisplayValue(p.Height, scale).ToString();
        SelRText.Text = p.RadiusPixels.ToString();
        SelEText.Text = p.EffectWidthPixels.ToString();
        SelAspectText.Text = SelectionAspects.Label(_aspect);

        // 位置 / 尺寸的单位跟设置走（照上游两处 unit label）；圆角 / 效果宽永远是画布像素。
        string unit = OverlayUiSettings.UnitLabel;
        SelPosUnit.Text = unit;
        SelSizeUnit.Text = unit;

        string unitTip = OverlayUiSettings.LogicalUnits ? "逻辑像素（dp）" : "物理像素（px）";
        SelPosUnit.ToolTip = unitTip;
        SelSizeUnit.ToolTip = unitTip;

        SelLockIcon.Fill = new SolidColorBrush(_aspectLocked ? BarPrimary : Colors.White);

        // 自定义区域没有圆角（上游 m_cornerRadiusApplicable），字段变暗但照样能滚生效宽度
        SelFieldR.Opacity = CornerRadiusApplicable ? 1.0 : 0.45;
    }

    /// <summary>把信息条摆到选区旁边（上方优先，放不下依次换右 / 左 / 下）。</summary>
    private void ShowSelectionBar(Rect selection)
    {
        // 两个开关（照上游 selection_toolbar_hidden / show_edit_selection_toolbar）
        if (!OverlayUiSettings.ShouldShowSelectionBar(_tool))
        {
            HideSelectionBar();
            return;
        }

        SyncSelectionBar();
        SelBar.Visibility = Visibility.Visible;

        SelBar.InvalidateMeasure();
        SelBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(
            Math.Ceiling(SelBar.DesiredSize.Width),
            Math.Ceiling(SelBar.DesiredSize.Height));

        Point at = SelectionBarPlacement.Place(
            selection, size, new Rect(0, 0, ActualWidth, ActualHeight));

        Canvas.SetLeft(SelBar, Math.Round(at.X));
        Canvas.SetTop(SelBar, Math.Round(at.Y));
    }

    private void HideSelectionBar()
    {
        SelBar.Visibility = Visibility.Collapsed;
        CloseAspectMenu();
    }

    // ============ 滚轮 ============

    /// <summary>字段滚轮：显示单位下 ±1（物理像素 = 1px，逻辑像素 = 1dp，照上游 <c>handleFieldWheel</c>）。</summary>
    private void BarWheel(SelectionField field, int delta)
    {
        if (delta == 0) return;
        if (CurrentSelectionParams() is not SelectionParams current) return;

        int direction = delta > 0 ? 1 : -1;
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        int stepX = OverlayUiSettings.StepPixels(current.X, direction, scale);
        int stepY = OverlayUiSettings.StepPixels(current.Y, direction, scale);
        int stepW = OverlayUiSettings.StepPixels(current.Width, direction, scale);
        int stepH = OverlayUiSettings.StepPixels(current.Height, direction, scale);

        switch (field)
        {
            case SelectionField.X:
                AdjustBarSelection(stepX, 0, stepX, 0);
                break;
            case SelectionField.Y:
                AdjustBarSelection(0, stepY, 0, stepY);
                break;
            case SelectionField.Width:
                AdjustBarSelection(0, 0, stepW, 0);
                break;
            case SelectionField.Height:
                AdjustBarSelection(0, 0, 0, stepH);
                break;
            case SelectionField.Radius:
                if (CornerRadiusApplicable)
                {
                    int radius = _selStyle.RadiusPixels(_doc.Scale);
                    SetCornerRadiusPixels(radius + OverlayUiSettings.StepPixels(radius, direction, scale));
                }
                break;
            case SelectionField.EffectWidth:
            {
                int effect = _selStyle.EffectPixels(_doc.Scale);
                SetEffectWidthPixels(effect + OverlayUiSettings.StepPixels(effect, direction, scale));
                break;
            }
        }
    }

    /// <summary>
    /// 按物理像素改选区（照上游 <c>adjustFromToolbar</c>）：正数是「往右 / 往下」。
    /// 单块矩形改对应边，多块 / 点列只整体平移；锁比例时改宽 / 高按比例联动、左上角锚定。
    /// </summary>
    /// <remarks>
    /// 全程在**屏幕物理像素**里算（与界面上的数字、上游 <c>normalizedSelection()</c> 同一口径），
    /// 最后才换回窗口 DIP：滚轮 ±1 一定正好是数字 ±1，不会因为 DIP↔像素取整而走两步。
    /// </remarks>
    private void AdjustBarSelection(int minDx, int minDy, int maxDx, int maxDy)
    {
        if (CurrentSelectionParams() is not SelectionParams current) return;

        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        var area = current.Area;

        bool translateOnly = _regions.Count != 1 ||
                             _regions[0] is not { Mode: RegionMode.Add, IsPolygon: false };

        if (translateOnly)
        {
            if (minDx != maxDx || minDy != maxDy) return;   // 多块不支持改边（上游同义）

            int x = Math.Clamp(area.X + minDx, _doc.Monitor.X,
                Math.Max(_doc.Monitor.X, _doc.Monitor.X + _doc.Base.PixelWidth - area.Width));
            int y = Math.Clamp(area.Y + minDy, _doc.Monitor.Y,
                Math.Max(_doc.Monitor.Y, _doc.Monitor.Y + _doc.Base.PixelHeight - area.Height));
            int dx = x - area.X, dy = y - area.Y;
            if (dx == 0 && dy == 0) return;

            var shift = new Vector(dx / scale, dy / scale);
            for (int i = 0; i < _regions.Count; i++) _regions[i] = _regions[i].Offset(shift);
            RefreshSelection();
            SyncArea();
            UpdateChrome();
            return;
        }

        int left = area.X + minDx;
        int top = area.Y + minDy;
        int right = area.X + area.Width + maxDx;
        int bottom = area.Y + area.Height + maxDy;
        double width = Math.Max(1, right - left);
        double height = Math.Max(1, bottom - top);

        double ratio = _lockedRatio;
        bool resize = minDx == 0 && minDy == 0 && (maxDx != 0 || maxDy != 0);
        if (_aspectLocked && ratio > 0 && resize)
        {
            // 上游：按「宽²·ratio vs 高²/ratio」挑一轴推，再把整个框按比例夹进右下角区域
            int dv = maxDx + maxDy;
            if (dv > 0)
            {
                if (width * (width * ratio) > (height / ratio) * height) height = width * ratio;
                else width = height / ratio;
            }
            else
            {
                if (width * (width * ratio) < (height / ratio) * height) height = width * ratio;
                else width = height / ratio;
            }

            // 左上角锚定：装不下就整体缩，别把比例压歪
            int roomW = Math.Max(1, _doc.Monitor.X + _doc.Base.PixelWidth - left);
            int roomH = Math.Max(1, _doc.Monitor.Y + _doc.Base.PixelHeight - top);
            double shrink = Math.Min(1.0, Math.Min(roomW / width, roomH / height));
            width *= shrink;
            height *= shrink;
        }
        else
        {
            width = Math.Clamp(width, 1, Math.Max(1, _doc.Base.PixelWidth));
            height = Math.Clamp(height, 1, Math.Max(1, _doc.Base.PixelHeight));
            left = Math.Clamp(left, _doc.Monitor.X,
                Math.Max(_doc.Monitor.X, _doc.Monitor.X + _doc.Base.PixelWidth - (int)Math.Round(width)));
            top = Math.Clamp(top, _doc.Monitor.Y,
                Math.Max(_doc.Monitor.Y, _doc.Monitor.Y + _doc.Base.PixelHeight - (int)Math.Round(height)));
        }

        var next = new Int32Rect(left, top,
            Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));

        SetSingleRect(DipSelectionFrom(next));
        if (_aspectLocked) FinalizeAspect();
        SyncArea();
        UpdateChrome();
    }

    /// <summary>
    /// 屏幕物理像素矩形 → 窗口 DIP 里的选区矩形，与 <see cref="ToImage"/> 的「含两端像素」互逆。
    /// </summary>
    /// <remarks>
    /// 选区在图像里是 <c>x1..x2</c> **两端都算**（宽度 = x2 − x1 + 1），所以 DIP 右边界要落在
    /// <c>x2</c> 那一列上，宽度就是 <c>(W − 1) / scale</c>。用 <see cref="ToDipRect"/> 的
    /// <c>W / scale</c> 会让每次信息条改值都多算 1 px，滚一格加 5 px（实测）。
    /// </remarks>
    private Rect DipSelectionFrom(Int32Rect area)
    {
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        return new Rect(
            _doc.Origin.X + (area.X - _doc.Monitor.X) / scale,
            _doc.Origin.Y + (area.Y - _doc.Monitor.Y) / scale,
            Math.Max(0, area.Width - 1) / scale,
            Math.Max(0, area.Height - 1) / scale);
    }

    // ============ 参数 ============

    /// <summary>点信息条字段 / 双击选区内 = 开选区参数对话框（照上游 openSelectionResizeModal）。</summary>
    private void OpenSelectionParams(SelectionField field)
    {
        if (CurrentSelectionParams() is not SelectionParams current) return;

        var bounds = new Int32Rect(_doc.Monitor.X, _doc.Monitor.Y,
            Math.Max(2, _doc.Base.PixelWidth), Math.Max(2, _doc.Base.PixelHeight));

        var previousPreset = SelectionStore.Previous;
        var previous = previousPreset == null
            ? null
            : SelectionParams.From(previousPreset.Area, previousPreset.LockAspect,
                previousPreset.Style, _doc.Scale);

        var result = SelectionParamsWindow.Show(
            this, current, previous, SelectionStore.Presets,
            bounds, _doc.Scale, CornerRadiusApplicable);

        if (result != null) ApplySelectionParams(result);

        _ = field;      // 目前各字段都开同一个框（上游也是整份参数一起改）
    }

    /// <summary>
    /// 当前选区参数（屏幕物理像素）；没有选区 = null。
    /// </summary>
    /// <remarks>
    /// 从**活的选区几何**（<c>_sel</c>）算，而不是读 <c>_doc.Area</c>：后者只在确定 / 长截图 /
    /// 信息条改值这些时机同步，拖动过程中是上一拍的值，信息条会显示慢半拍的数字。
    /// </remarks>
    internal SelectionParams? CurrentSelectionParams()
    {
        if (_sel is not Rect s || s.Width < 0.5 || s.Height < 0.5) return null;

        var (x1, y1) = ToImage(s.TopLeft);
        var (x2, y2) = ToImage(s.BottomRight);
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Max(1, Math.Abs(x2 - x1) + 1);
        int h = Math.Max(1, Math.Abs(y2 - y1) + 1);

        var area = new Int32Rect(_doc.Monitor.X + x, _doc.Monitor.Y + y, w, h);
        return SelectionParams.From(area, _aspectLocked, _selStyle, _doc.Scale);
    }

    /// <summary>
    /// 套一份参数（快速集 / 参数框）：几何整块替换、外观按物理像素写回，
    /// 比例锁跟着表单走（锁着就把当前几何的比例当自定义比例）。
    /// </summary>
    internal void ApplySelectionParams(SelectionParams p)
    {
        var bounds = new Int32Rect(_doc.Monitor.X, _doc.Monitor.Y,
            Math.Max(2, _doc.Base.PixelWidth), Math.Max(2, _doc.Base.PixelHeight));
        var area = p.ClampTo(bounds).Area;

        var dip = DipSelectionFrom(area);
        SetSingleRect(dip);

        _aspectLocked = p.LockAspect;
        _aspect = p.LockAspect ? SelectionAspects.Closest(dip) : SelectionAspect.Free;
        _lockedRatio = p.LockAspect && area.Width > 0 ? (double)area.Height / area.Width : 0;
        SelectionStore.SetAspect(_aspectLocked, _aspect);

        ApplySelStyle(p.ApplyTo(_selStyle, _doc.Scale));
        SyncArea();
        UpdateChrome();
    }

    // ============ 比例下拉 ============

    /// <summary>打开比例下拉：现场重建条目（快速集可能在参数框里刚改过），再摆到比例按钮下面。</summary>
    private void OpenAspectMenu()
    {
        BuildAspectMenu();
        SelAspectMenu.Visibility = Visibility.Visible;

        SelAspectMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(
            Math.Ceiling(SelAspectMenu.DesiredSize.Width),
            Math.Min(AspectMenuMaxHeight, Math.Ceiling(SelAspectMenu.DesiredSize.Height)));

        // 左边缘对齐比例按钮，优先摆在信息条下面；下方放不下就翻到上面（照上游下拉的锚点）
        var target = SelAspectBtn.TransformToAncestor(Layer).TransformBounds(
            new Rect(0, 0, SelAspectBtn.ActualWidth, SelAspectBtn.ActualHeight));

        double x = target.X;
        double y = target.Bottom + 4;
        if (y + size.Height > Layer.ActualHeight) y = target.Top - 4 - size.Height;

        Canvas.SetLeft(SelAspectMenu, Math.Round(Math.Clamp(x, 0, Math.Max(0, Layer.ActualWidth - size.Width))));
        Canvas.SetTop(SelAspectMenu, Math.Round(Math.Clamp(y, 0, Math.Max(0, Layer.ActualHeight - size.Height))));
    }

    private void CloseAspectMenu()
    {
        if (!IsAspectMenuOpen) return;
        SelAspectMenu.Visibility = Visibility.Collapsed;
    }

    private void BuildAspectMenu()
    {
        SelAspectItems.Children.Clear();

        AddAspectGroup("选区预设");

        var previous = SelectionStore.Previous;
        AddAspectItem("上次选区", selected: false, enabled: previous != null,
            () => ApplySelectionParams(SelectionParams.From(
                previous!.Area, previous.LockAspect, previous.Style, _doc.Scale)));

        var presets = SelectionStore.Presets;
        foreach (var preset in presets)
        {
            var captured = preset;
            AddAspectItem(captured.Name, selected: false, enabled: true,
                () => ApplySelectionParams(SelectionParams.From(
                    captured.Area, captured.LockAspect, captured.Style, _doc.Scale)));
        }

        AddAspectGroup("比例预设");

        foreach (var aspect in SelectionAspects.All)
        {
            var captured = aspect;
            AddAspectItem(SelectionAspects.Label(aspect), selected: aspect == _aspect,
                enabled: true, () => SetAspectPreset(captured));
        }
    }

    private void AddAspectGroup(string title)
    {
        SelAspectItems.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontSize = 11.5,
            Margin = new Thickness(8, 6, 8, 2),
        });
    }

    private void AddAspectItem(string text, bool selected, bool enabled, Action click)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(selected ? BarPrimary : Colors.White),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 286,
        };

        var item = new Border
        {
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Child = label,
            Cursor = enabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Opacity = enabled ? 1.0 : 0.4,
        };

        item.MouseEnter += (_, _) =>
        {
            if (enabled) item.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        };
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;

        if (enabled)
        {
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseAspectMenu();
                click();
            };
        }

        SelAspectItems.Children.Add(item);
    }
}
