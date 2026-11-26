using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Views.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>CaptureToolbar 的「完成动作布局（照上游 action_tools_layout）」部分。</summary>
public partial class CaptureToolbar
{
    // ============ 完成动作布局（照上游 action_tools_layout） ============

    /// <summary>当前工具条上按顺序排出来的完成动作（冒烟 / 设置页摘要用）。</summary>
    internal IReadOnlyList<CaptureActionTool> ActionOrder => _actionOrder;

    private readonly List<CaptureActionTool> _actionOrder = new();

    /// <summary>
    /// 编辑模式里用得到的完成动作：截图覆盖层有的长截图 / 录屏 / 实时贴图 / 更多 / 取消
    /// 在图片编辑窗里没有对应行为，按上游 <c>PinnedActionTools</c> 的口径直接跳过。
    /// </summary>
    internal static bool ActionAvailableIn(CaptureActionTool tool, ToolbarMode mode) => mode != ToolbarMode.Edit
        || tool is CaptureActionTool.Recognize
            or CaptureActionTool.Pin
            or CaptureActionTool.Copy
            or CaptureActionTool.Print
            or CaptureActionTool.QuickSave
            or CaptureActionTool.SaveAs;

    /// <summary>
    /// 按布局逐「位置」摆出完成动作（<see cref="CaptureActionLayoutStore"/>）：一叠一个按钮
    /// （多于一个动作时悬停弹出二级工具条），竖线位置画一条竖线。编辑模式里用不到的自动跳过。
    /// </summary>
    /// <param name="mode">哪条工具条（截图 / 图片编辑 / 全屏画布）。</param>
    /// <param name="separatorBeforeFirst">
    /// 第一组前面要不要竖线：截图模式前面已经有一条（「撤销 / 重做 │ 完成动作」），编辑模式没有。
    /// </param>
    private void AddActionGroups(ToolbarMode mode, bool separatorBeforeFirst)
    {
        bool pendingSeparator = separatorBeforeFirst;

        foreach (var position in CaptureActionLayoutStore.Current.Positions)
        {
            if (position.IsSeparator)
            {
                pendingSeparator = true;
                continue;
            }

            var buttons = new List<FrameworkElement>();
            foreach (var tool in position.Tools)
            {
                var button = ActionButtonFor(tool, mode);
                if (button == null) continue;
                _actionOrder.Add(tool);
                buttons.Add(button);
            }

            if (buttons.Count == 0) continue;

            if (pendingSeparator) AddSeparator();
            pendingSeparator = false;

            if (buttons.Count == 1) AddInline(buttons[0]);
            else AddActionStack(buttons);
        }
    }

    /// <summary>一个完成动作的按钮（布局里的键 → 图标 / 提示 / <see cref="CaptureAction"/>）。</summary>
    private FrameworkElement? ActionButtonFor(CaptureActionTool tool, ToolbarMode mode)
    {
        if (!ActionAvailableIn(tool, mode)) return null;

        bool edit = mode == ToolbarMode.Edit;
        return tool switch
        {
            CaptureActionTool.Recognize => ActionButton("IconRecognize",
                "识别文字 / 表格 / 二维码（Ctrl+R）", CaptureAction.Recognize),
            CaptureActionTool.QrCode => QrButton(),
            CaptureActionTool.Record => ActionButton("IconRecord",
                "录屏：把当前选区录成 MP4（3 秒倒计时后开始，控制条上可暂停 / 完成 / 取消）",
                CaptureAction.Record),
            CaptureActionTool.Pin => ActionButton("IconPin", "贴图到屏幕（Ctrl+T）", CaptureAction.Pin),
            CaptureActionTool.Live => ActionButton("IconLive",
                "实时贴图：贴一块跟着这个窗口刷新的图（Ctrl+W）", CaptureAction.Live),
            CaptureActionTool.Translate => ActionButton("IconTranslate",
                "识别并翻译：识别完自动按设置里的目标语言翻一遍（Ctrl+R 识别后在结果窗点翻译）",
                CaptureAction.Translate),
            CaptureActionTool.Scroll => ActionButton("IconScroll",
                "长截图：滚着接成一张长图（Ctrl+L）", CaptureAction.Scroll),
            CaptureActionTool.Print => ActionButton("IconPrint", "打印（Ctrl+P）", CaptureAction.Print),
            CaptureActionTool.QuickSave => ActionButton("IconQuickSave",
                edit
                    ? "快速保存到图片\\LTools 截图，不弹框（Ctrl+S）"
                    : "快速保存到图片\\LTools 截图，不弹框（Ctrl+Shift+S）",
                CaptureAction.QuickSave),
            CaptureActionTool.SaveAs => ActionButton("IconSave",
                edit ? "另存为…（Ctrl+Shift+S）" : "另存为…（Ctrl+S）", CaptureAction.Save),
            CaptureActionTool.More => MoreButton(),
            CaptureActionTool.Cancel => ActionButton("IconClose", "取消（Esc）", CaptureAction.Cancel,
                danger: true),
            CaptureActionTool.Copy => ActionButton("IconCopy",
                edit ? "复制到剪贴板（Ctrl+C）" : "复制到剪贴板并关闭（Enter）",
                CaptureAction.Copy, primary: true),
            _ => null,
        };
    }

    /// <summary>
    /// 全屏画布右半边（照上游 <c>screenshottoolpalette.cpp</c> 的 <c>showGlobalCanvasActions</c>）：
    /// 撤销 / 重做 → 激光笔（可勾）· 点击穿透（可勾）→ 退出（主色）。没有取消 / 保存 / 复制：
    /// 画布是拿来「边讲边画」的，不是一次截图。
    /// </summary>
    private void BuildCanvasActions()
    {
        _undoBtn = AddAction("IconUndo", "撤销（Ctrl+Z）", () => UndoRequested?.Invoke());
        _redoBtn = AddAction("IconRedo", "重做（Ctrl+Y）", () => RedoRequested?.Invoke());

        var line = new Border { Style = (Style)FindResource("SeparatorBar") };
        _actionParts.Add(line);
        ToolRow.Children.Add(line);

        _laserBtn = AddToolToggle("IconRecTrail",
            "激光笔：指针走过的地方留一条红色拖尾（500ms 淡出）", () => LaserToggleRequested?.Invoke());
        _clickThroughBtn = AddToolToggle("IconMouse",
            "点击穿透：画布不再吃鼠标，点得到下面的窗口；再按一次热键切回",
            () => ClickThroughToggleRequested?.Invoke());

        _actionParts.Add(_laserBtn);
        _actionParts.Add(_clickThroughBtn);

        var exit = ActionButton("IconClose", "退出全屏画布（Esc）", CaptureAction.Exit, primary: true);
        _actionParts.Add(exit);
        ToolRow.Children.Add(exit);
    }

    private ToggleButton? _laserBtn;
    private ToggleButton? _clickThroughBtn;

    /// <summary>宿主告诉我们激光笔现在是开还是关（热键 / 别的入口改了也要同步按钮）。</summary>
    internal void SetLaserState(bool on)
    {
        if (_laserBtn == null) return;
        _syncing = true;
        _laserBtn.IsChecked = on;
        _syncing = false;
    }

    /// <summary>宿主告诉我们点击穿透现在是开还是关。</summary>
    internal void SetClickThroughState(bool on)
    {
        if (_clickThroughBtn == null) return;
        _syncing = true;
        _clickThroughBtn.IsChecked = on;
        _syncing = false;
    }

    /// <summary>两个裁剪格子互斥：按下哪个就只勾哪个；再点一次取消 → 返回 null（退出裁剪）。</summary>
    private CropAxis? ToggledCrop(CropAxis axis)
    {
        if (_cropRowsBtn == null || _cropColumnsBtn == null) return null;

        bool on = axis == CropAxis.Rows ? _cropRowsBtn.IsChecked == true : _cropColumnsBtn.IsChecked == true;

        _syncing = true;
        _cropRowsBtn.IsChecked = on && axis == CropAxis.Rows;
        _cropColumnsBtn.IsChecked = on && axis == CropAxis.Columns;
        _syncing = false;

        return on ? axis : null;
    }

    private ToggleButton AddToolToggle(string icon, string tip, Action click)
    {
        var button = new ToggleButton
        {
            Style = (Style)FindResource("BarToggle"),
            Focusable = false,
            ToolTip = tip,
            Content = new ShapePath
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => click();
        ToolRow.Children.Add(button);
        return button;
    }

    /// <summary>「更多」按钮：点它弹出 <see cref="ShowMoreMenu"/>（本身不直接是个完成动作）。</summary>
    private Button MoreButton()
    {
        var button = new Button
        {
            Style = (Style)FindResource("ActBtn"),
            ToolTip = "更多：全屏（Ctrl+A）/ 采集历史：上一张 · 下一张（, .）/ 采集历史窗口…",
            Content = new ShapePath
            {
                Data = (Geometry)FindResource("IconMore"),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => ShowMoreMenu(button);
        return button;
    }

    /// <summary>「更多」菜单：只放**低频但已实现**的入口，点完仍旧走 <see cref="ActionRequested"/>。</summary>
    private void ShowMoreMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };

        MenuItem Item(string header, CaptureAction action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => ActionRequested?.Invoke(action);
            return item;
        }

        menu.Items.Add(Item("全屏（Ctrl+A）", CaptureAction.FullScreen));
        menu.Items.Add(Item("采集历史：上一张（,）", CaptureAction.HistoryPrev));
        menu.Items.Add(Item("采集历史：下一张（.）", CaptureAction.HistoryNext));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("录屏选项…（声音 / 摄像头 / 特效 / 编码）", CaptureAction.RecordOptions));
        menu.Items.Add(Item("采集历史窗口…", CaptureAction.HistoryWindow));

        menu.IsOpen = true;
    }

    /// <summary>画一条竖线（布局里的 <c>separator</c> 位置）。</summary>
    private void AddSeparator()
    {
        var line = new Border { Style = (Style)FindResource("SeparatorBar") };
        _actionParts.Add(line);
        ToolRow.Children.Add(line);
    }

    /// <summary>把一个按钮直接摆进工具条。</summary>
    private void AddInline(FrameworkElement button)
    {
        _actionParts.Add(button);
        ToolRow.Children.Add(button);
    }

    /// <summary>
    /// 一叠动作（照上游 <c>stackPresentation</c>）：**只露第一个按钮**，鼠标停上去弹出二级工具条
    /// （这一叠的其余动作），点哪个执行哪个；移开 220ms 自动收起。
    /// </summary>
    private void AddActionStack(IReadOnlyList<FrameworkElement> buttons)
    {
        var trigger = buttons[0];

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Colors.Black,
            },
            Child = row,
        };

        var popup = new Popup
        {
            PlacementTarget = trigger,
            Placement = PlacementMode.Custom,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
            IsOpen = false,
        };
        CenterOn(popup, preferAbove: true);

        foreach (var button in buttons.Skip(1))
        {
            button.Margin = new Thickness(0, 0, 4, 0);
            row.Children.Add(button);
        }

        var close = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        close.Tick += (_, _) =>
        {
            close.Stop();
            popup.IsOpen = false;
        };

        trigger.MouseEnter += (_, _) =>
        {
            close.Stop();
            popup.IsOpen = true;
        };
        trigger.MouseLeave += (_, _) =>
        {
            close.Stop();
            close.Start();
        };
        border.MouseEnter += (_, _) => close.Stop();
        border.MouseLeave += (_, _) =>
        {
            close.Stop();
            close.Start();
        };

        // 点二级工具条里的按钮之后收起（按钮自己的 Click 先跑，这里只负责关）
        foreach (var button in buttons.Skip(1))
            button.MouseLeftButtonUp += (_, _) => popup.IsOpen = false;

        _actionPopups.Add(popup);
        _actionParts.Add(trigger);
        ToolRow.Children.Add(trigger);
    }

    private readonly List<Popup> _actionPopups = new();

    /// <summary>
    /// 把悬停弹层**居中**摆在触发按钮的正上方 / 正下方（<paramref name="preferAbove"/> 决定先试哪边），
    /// 两边都给候选：WPF 取第一个装得下的，贴屏幕边也不会被裁掉。
    /// </summary>
    /// <remarks>
    /// 别用 <see cref="PlacementMode.Bottom"/> / <see cref="PlacementMode.Top"/>：那两个把弹层的
    /// **左边缘**对齐按钮左边缘，比按钮宽的一排会整排往右甩，看起来「歪了、压到隔壁」。
    /// </remarks>
    private static void CenterOn(Popup popup, bool preferAbove)
    {
        const double gap = 8;

        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            double x = (targetSize.Width - popupSize.Width) / 2;
            var below = new CustomPopupPlacement(new Point(x, targetSize.Height + gap),
                PopupPrimaryAxis.Horizontal);
            var above = new CustomPopupPlacement(new Point(x, -popupSize.Height - gap),
                PopupPrimaryAxis.Horizontal);
            return preferAbove ? new[] { above, below } : new[] { below, above };
        };
    }

    /// <summary>重抓期间置灰二维码（上游 <c>setRecaptureBusy</c>；信息条上的「重新截图」由覆盖层自己置灰）。</summary>
    internal void SetRecaptureBusy(bool busy)
    {
        if (_qrBtn != null) _qrBtn.IsEnabled = !busy && _qrAvailable;
    }

    /// <summary>
    /// 「显示二维码」（照 Snow Shot 的 <c>m_showQrCodeButton</c>）：只在「选择」工具下露出来，
    /// **可勾选**（勾上 = 标记显示中），识别不到码 / 还没扫完时禁用、原因进 ToolTip。
    /// </summary>
    private ToggleButton QrButton()
    {
        var button = new ToggleButton
        {
            Style = (Style)FindResource("BarToggle"),
            Focusable = false,
            IsEnabled = false,
            ToolTip = "显示二维码",
            Content = new ShapePath
            {
                Data = (Geometry)FindResource("IconScanQr"),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => QrVisibilityRequested?.Invoke(button.IsChecked == true);
        _qrBtn = button;
        return button;
    }

    private ToggleButton? _qrBtn;
    private bool _actionsVisible = true;
    private bool _qrAvailable;
    private bool _qrVisible = true;
    private string _qrError = "";

    /// <summary>覆盖层告诉我们「二维码能不能看 / 是不是正在看 / 出没出错」（照上游 <c>setQrCodeState</c>）。</summary>
    internal void SetQrState(bool available, bool visible, string error)
    {
        _qrAvailable = available;
        _qrVisible = visible;
        _qrError = error ?? "";
        if (_qrBtn == null) return;

        _qrBtn.IsEnabled = available;
        _qrBtn.IsChecked = available && visible;
        _qrBtn.ToolTip = _qrError.Length > 0 ? _qrError : "显示二维码（自动识别到的码；Esc 退出）";
    }

    /// <summary>「显示二维码」只服务「选择」工具（画标注时它没有意义），完成动作收起时一起收。</summary>
    private void ApplyQrVisibility()
    {
        bool show = _actionsVisible && _tool == CaptureTool.Select;
        if (_qrBtn != null) _qrBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button ActionButton(string icon, string tip, CaptureAction action, bool primary = false,
        bool danger = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource(primary ? "BarBtnPrimary" : danger ? "BarBtnDanger" : "ActBtn"),
            ToolTip = tip,
            Content = new ShapePath
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => ActionRequested?.Invoke(action);
        return button;
    }

    /// <summary>
    /// 「完成动作」这一截显不显示：贴图内标注（`EditOnly`）没有出口栏——完成是 Enter / 双击，
    /// 结果交回那张贴图，别摆一排按了会被忽略的按钮。
    /// </summary>
    internal void SetActionsVisible(bool visible)
    {
        _actionsVisible = visible;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in _actionParts) part.Visibility = visibility;
        if (!visible) foreach (var popup in _actionPopups) popup.IsOpen = false;
        ApplyQrVisibility();     // 二维码按钮另有「只在使用选择工具时」这一层
    }

    private Button? _undoBtn, _redoBtn;

    private Button AddAction(string icon, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ActBtn"),
            ToolTip = tip,
            Content = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => click();
        ToolRow.Children.Add(button);
        return button;
    }

    private void BuildParams()
    {
        // 主色：共享颜色开着时改一处 = 全改（由 ToolStyleSet 统一处理）
        AddHost(ToolParam.Color, ColorRow("颜色", "captureColor",
            () => _style?.Color ?? ToolStyle.Palette[0],
            SetPrimaryColor,
            () => ColorPresets.ForTool(ParamTool)));

        // 文字族（上游 buildTextFamily）：字色 → 字号 + 字体族 → 粗 / 斜 → 水平对齐 →
        // 描边宽度 + 描边色 → 背景填充色 + 填充方式 → 背景圆角。
        // 顺序就是上游从左到右的顺序；别的工具用不到的格子收起来不占位。
        AddHost(ToolParam.TextSize, FontSizeRow());

        AddHost(ToolParam.FontFamily, FontPicker());

        AddHost(ToolParam.TextEmphasis, Emphasis());

        AddHost(ToolParam.TextAlign, Chips("对齐",
            new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right },
            () => _style?.TextAlign ?? TextAlign.Left,
            v => { if (_style != null) _style.TextAlign = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "textAlign"));

        AddHost(ToolParam.TextStrokeWidth, Step("描边", 0, TextInk.MaxStrokeWidth, 1,
            () => _style?.TextStrokeWidth ?? 0,
            v => { if (_style != null) _style.TextStrokeWidth = v; },
            v => v.ToString("0"), 0));

        AddHost(ToolParam.TextStrokeColor, ColorRow("描边色", "captureTextStroke",
            () => _style?.StrokeColor ?? TextInk.DefaultStroke,
            c => { if (_style != null) _style.StrokeColor = c; },
            ColorPresetFamily.Stroke));

        // 线宽 / 字号 / 块大小 / 透明度
        AddHost(ToolParam.Width, Step("线宽", 1, 50, 1,
            () => _style?.Width ?? 4, v => { if (_style != null) _style.Width = v; },
            v => v.ToString("0"), 4));

        AddHost(ToolParam.FontSize, WatermarkFontSizeRow());

        AddHost(ToolParam.Block, Step("块大小", 4, 24, 2,
            () => _style?.Block ?? 8, v => { if (_style != null) _style.Block = v; },
            v => v.ToString("0"), 8));

        AddHost(ToolParam.Opacity, Step("透明度", 10, 90, 5,
            () => (_style?.Opacity ?? 0.35) * 100, v => { if (_style != null) _style.Opacity = v / 100; },
            v => v.ToString("0") + "%", 35));

        // 形状族（上游 buildShapeFamily）：形状选择 / 描边线型 / 笔触风格 / 填充 / 圆角
        AddHost(ToolParam.ShapeKind, Chips("形状",
            new[] { ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Diamond, ShapeKind.ThoughtBubble },
            () => _style?.ShapeKind ?? ShapeKind.Rectangle,
            v => { if (_style != null) _style.ShapeKind = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "shapeKind",
            // 思考气泡只在气泡工具里出现（上游 buildShapeSelector 的第 4 项）
            v => v != ShapeKind.ThoughtBubble || ParamTool == CaptureTool.SpeechBubble));

        AddHost(ToolParam.StrokeStyle, Chips("描边",
            new[] { StrokeKind.Solid, StrokeKind.Dashed, StrokeKind.Dotted },
            () => _style?.StrokeStyle ?? StrokeKind.Solid,
            v => { if (_style != null) _style.StrokeStyle = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.Rendering, Chips("笔触",
            new[] { StrokeRendering.Architect, StrokeRendering.Artist, StrokeRendering.Cartoonist },
            () => _style?.Rendering ?? StrokeRendering.Architect,
            v => { if (_style != null) _style.Rendering = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.FillColor, ColorRow("填充色", "captureFillColor",
            () => _style?.FillColor ?? ToolStyle.DefaultFillColor,
            c => { if (_style != null) _style.FillColor = c; },
            ColorPresetFamily.Fill));

        AddHost(ToolParam.FillStyle, Chips("填充",
            new[] { FillKind.None, FillKind.Solid, FillKind.Line, FillKind.CrossLine },
            () => _style?.FillStyle ?? FillKind.None,
            v => { if (_style != null) _style.FillStyle = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.CornerRadius, Step("圆角", 0, TextInk.MaxCornerRadius, 2,
            () => _style?.CornerRadius ?? 6, v => { if (_style != null) _style.CornerRadius = v; },
            v => v.ToString("0"), 6));

        AddHost(ToolParam.LineType, Chips("线型",
            new[] { LineType.Straight, LineType.Curve },
            () => _style?.LineType ?? LineType.Curve,
            v => { if (_style != null) _style.LineType = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        AddHost(ToolParam.BrushMode, Chips("画笔",
            new[] { BrushMode.Regular, BrushMode.PressureSensitive },
            () => _style?.BrushMode ?? BrushMode.Regular,
            v => { if (_style != null) _style.BrushMode = v; },
            ShapeLabel.Chip, ShapeLabel.Name));

        // 序号的徽标类型与数字写法（枚举用文字胶囊：图标认不出「空心方 / 罗马数字」）
        AddHost(ToolParam.BadgeShape, Chips("徽标",
            new[]
            {
                SerialBadge.OutlinedCircle, SerialBadge.SolidCircle,
                SerialBadge.OutlinedSquare, SerialBadge.SolidSquare, SerialBadge.Circle,
            },
            () => _style?.Badge ?? SerialBadge.OutlinedCircle,
            v => { if (_style != null) _style.Badge = v; },
            SerialLabel.Chip, SerialLabel.Name, "badgeShape"));

        AddHost(ToolParam.Numbering, Chips("写法",
            new[]
            {
                SerialNumbering.Arabic, SerialNumbering.Roman, SerialNumbering.LowercaseLetters,
                SerialNumbering.UppercaseLetters, SerialNumbering.Chinese,
            },
            () => _style?.Numbering ?? SerialNumbering.Arabic,
            v => { if (_style != null) _style.Numbering = v; },
            SerialLabel.Chip, v => "数字写法：" + SerialLabel.Name(v), "numbering"));

        AddHost(ToolParam.SerialNumber, NumberBox("序号",
            () => _style?.SerialNumber ?? 1,
            v => { if (_style != null) _style.SerialNumber = Math.Max(0, v); },
            "下一个序号（滚轮微调，落一个自动 +1）"));

        AddHost(ToolParam.SerialFontSize, Step("字号", SerialSpec.MinFontSize, SerialSpec.MaxFontSize, 2,
            () => _style?.FontSize ?? 24, v => { if (_style != null) _style.FontSize = v; },
            v => v.ToString("0"), 24));

        // 滤镜类型与强度
        AddHost(ToolParam.FilterKind, Chips("类型",
            new[]
            {
                FilterKind.Blur, FilterKind.Grayscale, FilterKind.Invert, FilterKind.Emboss,
                FilterKind.Brighten, FilterKind.SmartErase,
            },
            () => _style?.FilterMode ?? FilterKind.Blur,
            v => { if (_style != null) _style.FilterMode = v; },
            SerialLabel.Name, v => "滤镜：" + SerialLabel.Name(v)));

        AddHost(ToolParam.FilterStrength, Step("强度", 0, 100, 5,
            () => _style?.FilterStrength ?? 50, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 50));

        // 自动打码：类型（不含智能擦除，跟矩形滤镜共用同一份样式状态）+ 强度 + 「填充区域」下拉
        AddHost(ToolParam.AutoFilterKind, Chips("类型",
            new[]
            {
                FilterKind.Mosaic, FilterKind.Blur, FilterKind.Grayscale, FilterKind.Invert,
                FilterKind.Emboss, FilterKind.Brighten,
            },
            () => _style?.FilterForAutoFill ?? FilterKind.Mosaic,
            v => { if (_style != null) _style.FilterMode = v; },
            SerialLabel.Name, v => "打码方式：" + SerialLabel.Name(v), "autoFilterKind"));

        AddHost(ToolParam.AutoFilterStrength, Step("强度", 0, 100, 5,
            () => _style?.FilterStrength ?? 50, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 50));

        AddHost(ToolParam.FillRegions, FillRegions());

        // ============ 放大镜族（上游 buildMagnifierFamily） ============
        // 顺序：形状（3，没有思考气泡）→ 色 → 线宽 → 倍率 → 连接方式 → 源轮廓 →
        // 箭头比例 → 引导线端点 → 圆角
        AddHost(ToolParam.Factor, Step("倍率", MagnifierSpec.MinFactor, MagnifierSpec.MaxFactor, 0.5,
            () => _style?.Factor ?? 2, v => { if (_style != null) _style.Factor = v; },
            v => v.ToString("0.#") + "×", 2));

        AddHost(ToolParam.Connection, Chips("连接",
            new[] { MagnifierConnection.None, MagnifierConnection.LeaderLine, MagnifierConnection.Gradient },
            () => _style?.Connection ?? MagnifierConnection.Gradient,
            v => { if (_style != null) _style.Connection = v; },
            MagnifierLabel.Chip, MagnifierLabel.Name, "magnifierConnection"));

        AddHost(ToolParam.SourceOutline, Toggle("源轮廓",
            () => _style?.SourceOutline ?? true,
            v => { if (_style != null) _style.SourceOutline = v; },
            "画出源区域的外框（与镜头重合时会被镜头盖住）"));

        AddHost(ToolParam.ArrowRatio, Step("箭头比例", ArrowInk.MinRatio, ArrowInk.MaxRatio, 0.1,
            () => _style?.ArrowRatio ?? 1, v => { if (_style != null) _style.ArrowRatio = v; },
            v => v.ToString("0.0") + "×", 1));

        AddHost(ToolParam.LeaderArrowhead, Picker("箭头",
            DistanceEndpoints,
            () => _style?.EndpointStyle ?? Arrowhead.None,
            v => { if (_style != null) _style.EndpointStyle = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 箭头族（上游 buildArrowFamily） ============
        // 顺序：类型（3）→ 外描边（宽 + 色）→ 箭头比例 → 起止端点 → 杆型
        AddHost(ToolParam.ArrowKind, Chips("类型",
            new[] { ArrowType.Straight, ArrowType.Curve, ArrowType.Elbow },
            () => _style?.ArrowKind ?? ArrowType.Curve,
            v => { if (_style != null) _style.ArrowKind = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "arrowKind"));

        AddHost(ToolParam.ArrowOutlineWidth, Step("外描边", 0, ArrowSpec.MaxOutlineWidth, 1,
            () => _style?.ArrowOutlineWidth ?? 0,
            v => { if (_style != null) _style.ArrowOutlineWidth = v; },
            v => v.ToString("0"), 0));

        AddHost(ToolParam.ArrowOutlineColor, ColorRow("描边色", "captureArrowOutline",
            () => _style?.ArrowOutlineColor ?? ArrowSpec.DefaultOutline,
            c => { if (_style != null) _style.ArrowOutlineColor = c; },
            ColorPresetFamily.Fill));

        AddHost(ToolParam.StartArrowhead, Picker("起点",
            ArrowGeometry.Heads,
            () => _style?.StartArrowhead ?? Arrowhead.None,
            v => { if (_style != null) _style.StartArrowhead = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        AddHost(ToolParam.ArrowShaftType, Chips("杆型",
            new[] { ArrowShaft.Plain, ArrowShaft.Tapered },
            () => _style?.ArrowShaftType ?? ArrowShaft.Plain,
            v => { if (_style != null) _style.ArrowShaftType = v; },
            ShapeLabel.Chip, ShapeLabel.Name, "arrowShaft"));

        AddHost(ToolParam.EndArrowhead, Picker("终点",
            ArrowGeometry.Heads,
            () => _style?.EndArrowhead ?? Arrowhead.Arrow,
            v => { if (_style != null) _style.EndArrowhead = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 水印族（上游 buildWatermarkFamily） ============
        // 顺序：色 → 文字 → 字号 + 字体族 → 模板 → 角度 → 间距 → 不透明度
        AddHost(ToolParam.WatermarkText, Text("文字",
            () => _style?.WatermarkText ?? string.Empty,
            v => { if (_style != null) _style.WatermarkText = v; },
            "水印文字（回车或失焦生效；留空就什么都不画）", 150));

        AddHost(ToolParam.WatermarkTemplate, WatermarkTemplates());

        AddHost(ToolParam.WatermarkAngle, Step("角度", WatermarkSpec.MinAngle, WatermarkSpec.MaxAngle, 15,
            () => _style?.WatermarkAngle ?? 30, v => { if (_style != null) _style.WatermarkAngle = v; },
            v => v.ToString("0") + "°", 30));

        AddHost(ToolParam.WatermarkGap, Step("间距", WatermarkSpec.MinGap, WatermarkSpec.MaxGap, 8,
            () => _style?.WatermarkGap ?? 56, v => { if (_style != null) _style.WatermarkGap = v; },
            v => v.ToString("0"), 56));

        AddHost(ToolParam.WatermarkOpacity, Step("不透明度", 0, 100, 4,
            () => (_style?.WatermarkOpacity ?? 0.16) * 100,
            v => { if (_style != null) _style.WatermarkOpacity = v / 100; },
            v => v.ToString("0") + "%", 16));

        // ============ 测距族（上游 buildDistanceFamily） ============
        // 顺序：描边（色 / 线宽）→ 实测值 → 单位 → 小数位 → 端点比例 → 端点样式
        AddHost(ToolParam.DistanceValue, MeasureValueRow());

        AddHost(ToolParam.DistanceUnit, Chips("单位",
            new[]
            {
                DistanceUnit.Px, DistanceUnit.Cm, DistanceUnit.M, DistanceUnit.Km, DistanceUnit.Mm,
                DistanceUnit.Pt, DistanceUnit.In, DistanceUnit.Ft, DistanceUnit.Um,
            },
            () => _style?.DistanceUnit ?? DistanceUnit.Cm,
            v => { if (_style != null) _style.DistanceUnit = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "distanceUnit"));

        AddHost(ToolParam.DistanceDecimals, DecimalsRow("小数位",
            () => _style?.DistanceDecimals ?? 0,
            v => { if (_style != null) _style.DistanceDecimals = v; }, "distanceDecimals"));

        AddHost(ToolParam.EndpointScale, Step("端点比例", ArrowInk.MinRatio, ArrowInk.MaxRatio, 0.1,
            () => _style?.EndpointScale ?? 1, v => { if (_style != null) _style.EndpointScale = v; },
            v => v.ToString("0.0") + "×", 1));

        AddHost(ToolParam.EndpointStyle, Picker("端点",
            DistanceEndpoints,
            () => _style?.EndpointStyle ?? Arrowhead.Bar,
            v => { if (_style != null) _style.EndpointStyle = v; },
            MeasureLabel.Chip, MeasureLabel.Name));

        // ============ 测角族（上游 buildAngleFamily） ============
        AddHost(ToolParam.AngleKind, Chips("类型",
            new[] { AngleKind.Directed, AngleKind.Interior },
            () => _style?.AngleKind ?? AngleKind.Directed,
            v => { if (_style != null) _style.AngleKind = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "angleKind"));

        AddHost(ToolParam.AngleUnit, Chips("单位",
            new[] { AngleUnit.Degrees, AngleUnit.Radians },
            () => _style?.AngleUnit ?? AngleUnit.Degrees,
            v => { if (_style != null) _style.AngleUnit = v; },
            MeasureLabel.Chip, MeasureLabel.Name, "angleUnit"));

        AddHost(ToolParam.AngleDecimals, DecimalsRow("小数位",
            () => _style?.AngleDecimals ?? 0,
            v => { if (_style != null) _style.AngleDecimals = v; }, "angleDecimals"));

        // ============ 橡皮族（上游 buildEraserFamily） ============
        // 三种模式共用一个选择器：点另一个模式就是换工具（上游 modeSelector 同一语义）
        AddHost(ToolParam.EraserMode, Chips("模式",
            new[] { CaptureTool.Eraser, CaptureTool.RectEraser, CaptureTool.BrushEraser },
            () => _tool,
            v => { if (v != _tool) ToolChanged?.Invoke(v); },
            EraserLabel.Chip, EraserLabel.Name, "eraserMode"));
    }

    // ============ 「只取选区」的确认按钮（只有参数框选用） ============

    private readonly List<FrameworkElement> _pickParts = new();   // 竖线 + 使用这块区域 + 取消
    private Button? _pickConfirmBtn;

    /// <summary>用户点了「使用这块区域」（覆盖层按 Enter 同一条出口处理）。</summary>
    internal event Action? PickConfirmed;

    /// <summary>用户点了「取消」。</summary>
    internal event Action? PickCancelled;

    /// <summary>
    /// 「使用这块区域 · 取消」：摆在工具条最右边、**跟着工具条走**（工具条本来就贴着选区摆）。
    /// 别再另起一扇浮层去跟工具条抢同一条道——实测那样按钮会被工具条盖住，用户点了没反应。
    /// 平时整组收起：普通截图有自己的完成动作那一排。
    /// </summary>
    private void BuildPickActions()
    {
        var line = new Border { Style = (Style)FindResource("SeparatorBar") };
        _pickParts.Add(line);
        ToolRow.Children.Add(line);

        var confirm = new Button
        {
            Style = (Style)FindResource("PickBtnPrimary"),
            Content = "使用这块区域",
            ToolTip = "Enter：把这块区域交给调用方（不复制、不记采集历史）",
            IsEnabled = false,
        };
        confirm.Click += (_, _) => PickConfirmed?.Invoke();
        _pickConfirmBtn = confirm;
        _pickParts.Add(confirm);
        ToolRow.Children.Add(confirm);

        var cancel = new Button
        {
            Style = (Style)FindResource("PickBtn"),
            Content = "取消",
            ToolTip = "Esc / 右键退回（不交回任何区域）",
        };
        cancel.Click += (_, _) => PickCancelled?.Invoke();
        _pickParts.Add(cancel);
        ToolRow.Children.Add(cancel);

        SetPickMode(false, false);
    }

    /// <summary>参数框选模式：露出这组按钮，并按「有没有选区」决定确认能不能点。</summary>
    internal void SetPickMode(bool on, bool canConfirm)
    {
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var part in _pickParts) part.Visibility = visibility;
        if (_pickConfirmBtn != null) _pickConfirmBtn.IsEnabled = on && canConfirm;
    }

    /// <summary>冒烟 / 排查用：确认按钮（没有就是 null）。</summary>
    internal Button? PickConfirmButton => _pickConfirmBtn;
}
