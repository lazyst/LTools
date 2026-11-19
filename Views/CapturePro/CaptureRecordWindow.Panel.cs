using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏面板的两行按钮（照 Snow Shot 的 <c>addRecordingControls</c> +
/// <c>createRecordingExportSettingsToolbar</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 第一行（<c>createMainToolbar</c>）：拖动把手 → 导出设置开关 → 十个绘制工具 → 撤销 / 重做 →
/// 录制 · 暂停 · 时长 · 麦克风 · 扬声器 → 打开文件夹 · 关闭 · 裁剪 · 另存 · 复制。
/// 度量照上游：按钮 32 / 图标 24、面板内边距 12×4、分隔线 1×16 两侧各 12、按钮之间没有额外间距
/// （图标 24 在 32 的格子里自然留出 8）。
/// </para>
/// <para>
/// 第二行（<c>createRecordingExportSettingsToolbar</c>）：格式下拉 → 激光笔（图标 + 当前色触发器 +
/// 5 个色卡）→ 点击（同结构）→ 光标 / 键盘 / 跟随 / 摄像头 / 后期处理 → 延迟 → 特效设置 → 齿轮。
/// 度量：按钮 28 / 图标 18、面板内边距 10×4、按钮间距 4、分组之间 8 + 分隔线。
/// </para>
/// <para>
/// **面板上的每一项都是真的**：全部写回 <see cref="RecordPlan"/> / <see cref="RecordOptions"/>。
/// 录制中第二行整体禁用（上游 <c>editable = Idle &amp;&amp; !busy</c>）；麦克风 / 扬声器在动图格式下也禁用
/// （上游：「动图格式不含音频」）。上游录制中点麦克风 / 扬声器是开增益浮层，本项目不支持增益，
/// 所以录制中直接禁用——这是唯一一处刻意偏差。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    // ============ 给端到端 harness / 会话用的入口（名字保留，别随手改） ============

    internal Button RecordBtn = null!;
    internal Button PauseBtn = null!;
    internal Button DoneBtn = null!;
    internal Button CancelBtn = null!;

    internal TextBlock ClockLabel = null!;
    internal TextBlock StatusText = null!;
    internal TextBlock MetaText = null!;

    // ============ 第一行 ============

    private ToggleButton _exportToggle = null!;
    private readonly List<RadioButton> _toolButtons = new();

    private Button _undoBtn = null!;
    private Button _redoBtn = null!;
    private Button _trimBtn = null!;
    private Button _saveBtn = null!;
    private Button _copyBtn = null!;

    private ToggleButton _micBtn = null!;
    private ToggleButton _speakerBtn = null!;

    // ============ 第二行 ============

    private Button _formatPill = null!;
    private TextBlock _formatText = null!;
    private ContextMenu? _formatMenu;
    private Button _delayPill = null!;
    private TextBlock _delayText = null!;

    private Button _laserTrigger = null!;
    private Button _clickTrigger = null!;
    private readonly List<(RadioButton Button, string Hex)> _laserPresets = new();
    private readonly List<(RadioButton Button, string Hex)> _clickPresets = new();

    private ToggleButton _cursorBtn = null!;
    private ToggleButton _keyboardBtn = null!;
    private ToggleButton _followBtn = null!;
    private ToggleButton _cameraBtn = null!;
    private ToggleButton _postBtn = null!;

    private Button _effectBtn = null!;
    private Button _settingsBtn = null!;

    private readonly List<HoverPopover> _popovers = new();
    private Popup? _pickerPopup;

    // 键盘弹层里那一排（PreserveShortcuts 跟着「控制键」一起禁用）
    private CheckBox _numberKeysBox = null!;
    private CheckBox _letterKeysBox = null!;
    private CheckBox _controlKeysBox = null!;
    private CheckBox _preserveKeysBox = null!;

    private CheckBox _highlightBox = null!;
    private CheckBox _clicksBox = null!;
    private CheckBox _scrollBox = null!;

    private CheckBox _smoothBox = null!;
    private CheckBox _jitterBox = null!;

    private RadioButton _progressBarRadio = null!;
    private RadioButton _playbackTimeRadio = null!;

    /// <summary>正在把状态写进控件（挡住 Click / Checked 回写，避免自我循环）。</summary>
    private bool _syncing;

    /// <summary>采集历史里有没有可用的录屏（裁剪 / 另存 / 复制）。每轮只查一次。</summary>
    private bool _hasLastRecord;

    // 色卡预设（照上游：拖尾 alpha 255、点击 alpha 128，第一个是「透明」）
    private static readonly string[] LaserPresets =
        { "#00000000", "#FFF5222D", "#FF52C41A", "#FF1677FF", "#FFFADB14" };

    private static readonly string[] ClickPresets =
        { "#00000000", "#80F5222D", "#8052C41A", "#801677FF", "#80FADB14" };

    // ============ 第一行 ============

    private void BuildActionRow()
    {
        var row = new RowBuilder(this, ActionRow, 8, small: false);

        // 拖动把手：18×32、图标 18，后面只留它自己的 4（kDragHandleTrailingSpacing）
        row.Add(DragHandle()).Glue();

        _exportToggle = new ToggleButton
        {
            Style = (Style)FindResource("BarToggle"),
            Content = MakeIcon("IconRecExport"),
            ToolTip = "导出设置：格式 / 色卡 / 键鼠特效 / 延迟",
            IsChecked = true,
        };
        _exportToggle.Checked += (_, _) => SetExportRow(true);
        _exportToggle.Unchecked += (_, _) => SetExportRow(false);
        row.Add(_exportToggle);

        // 绘制工具：单选；**初始不选中**（上游显示导出设置时 clearActiveToolState）
        bool first = true;
        foreach (var tool in RecordTools.All)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("BarToolBtn"),
                GroupName = "recTool",
                Content = MakeIcon(RecordTools.IconOf(tool)),
                ToolTip = RecordShortcuts.WithChord(RecordTools.TipOf(tool), RecordShortcuts.ActionOf(tool)),
                Tag = tool,
            };
            button.Checked += (_, e) =>
            {
                if (_syncing) return;
                Tool_Checked(button, e);

                // 上游：选了工具就收起导出设置（closeExportSettings）
                if (StyleBar.Visibility == Visibility.Visible) SetExportRow(false);
            };
            _toolButtons.Add(button);
            row.Add(button);

            // 上游 `select-separator`：选择工具和后面那组绘制工具之间有一条分组竖线
            if (first)
            {
                row.Separator();
                first = false;
            }
        }

        row.Separator();

        _undoBtn = AddButton("IconUndo", "撤销（Ctrl+Z）", (_, _) => Annotations?.Undo());
        _redoBtn = AddButton("IconRedo", "重做（Ctrl+Y）", (_, _) => Annotations?.Redo());
        _undoBtn.IsEnabled = false;
        _redoBtn.IsEnabled = false;
        row.Add(_undoBtn);
        row.Add(_redoBtn);

        row.Separator();

        // 录制 / 暂停 / 时长 / 麦克风 / 扬声器（上游一个槽位一个控件，间距与别的按钮一样是 8）
        RecordBtn = AddButton("IconRecStart", "开始录制",
            (_, _) => Record_Click(this, new RoutedEventArgs()), "BarBtnAccent");
        DoneBtn = RecordBtn;
        row.Add(RecordBtn);

        PauseBtn = AddButton("IconRecPause", "暂停（暂停的时间不进成品）",
            (_, _) => Pause_Click(this, new RoutedEventArgs()));
        PauseBtn.IsEnabled = false;
        row.Add(PauseBtn);

        ClockLabel = new TextBlock { Style = (Style)FindResource("RecClock"), Text = "00:00:00" };
        row.Add(ClockLabel);

        _micBtn = DeviceToggle("IconRecMicOff", "录麦克风（录制开始后不能改）");
        _micBtn.Checked += (_, _) => { if (_syncing) return; _plan.Options.Microphone = true; SyncUi(); };
        _micBtn.Unchecked += (_, _) => { if (_syncing) return; _plan.Options.Microphone = false; SyncUi(); };
        row.Add(_micBtn);

        _speakerBtn = DeviceToggle("IconRecAudioOff", "录系统声音（录制开始后不能改）");
        _speakerBtn.Checked += (_, _) => { if (_syncing) return; _plan.Options.SystemAudio = true; SyncUi(); };
        _speakerBtn.Unchecked += (_, _) => { if (_syncing) return; _plan.Options.SystemAudio = false; SyncUi(); };
        row.Add(_speakerBtn);

        row.Separator();

        // 收尾动作：打开文件夹 / 关闭 / 裁剪 / 另存 / 复制
        row.Add(AddButton("IconRecFolder", "打开录屏文件夹", (_, _) => OpenRecordsFolder()));

        CancelBtn = AddButton("IconClose", "关闭（取消这次录制）",
            (_, _) => Cancel_Click(this, new RoutedEventArgs()), "BarBtnDanger");
        row.Add(CancelBtn);

        _trimBtn = AddButton("IconRecTrim", "裁剪 / 倍速", (_, _) => OpenTrim());
        _saveBtn = AddButton("IconSave", "另存为", (_, _) => SaveLast());
        _copyBtn = AddButton("IconCopy", "复制", (_, _) => CopyLast());

        row.Add(_trimBtn);
        row.Add(_saveBtn);
        row.Add(_copyBtn);

        // 麦克风 / 系统声音的增益弹层（悬停开，录制中点按钮也开）
        InitGainPopovers();

        // 面板上没有位置、但会话与 harness 要看的两段文字（状态 / 格式摘要）
        StatusText = new TextBlock { Visibility = Visibility.Collapsed, Text = "" };
        MetaText = new TextBlock { Visibility = Visibility.Collapsed, Text = "" };
    }

    /// <summary>
    /// 按 Snow Shot 的度量往一行里排：条目之间留固定间距（第一行 8、第二行 4），
    /// 分组竖线**自带左右留白**（12 / 8），因此前后都不再补间距。
    /// </summary>
    private sealed class RowBuilder
    {
        private readonly CaptureRecordWindow _owner;
        private readonly StackPanel _row;
        private readonly double _spacing;
        private readonly bool _small;
        private bool _any;
        private bool _glued;

        public RowBuilder(CaptureRecordWindow owner, StackPanel row, double spacing, bool small)
        {
            _owner = owner;
            _row = row;
            _spacing = spacing;
            _small = small;
        }

        public RowBuilder Add(FrameworkElement element, bool spaced = true)
        {
            if (_any && spaced && !_glued) _row.Children.Add(new Border { Width = _spacing });

            _row.Children.Add(element);
            _any = true;
            _glued = false;
            return this;
        }

        /// <summary>下一个条目紧跟上（拖动把手后面只留它自己的 4）。</summary>
        public RowBuilder Glue()
        {
            _glued = true;
            return this;
        }

        public RowBuilder Separator()
        {
            _row.Children.Add(_owner.Separator(_small));
            _any = true;
            _glued = true;
            return this;
        }
    }

    /// <summary>最左边的拖动把手：18×32，图标 18（`kDragHandleWidth`），后面留 4。</summary>
    private FrameworkElement DragHandle()
    {
        var glyph = MakeIcon("IconDragHandle", small: true, fill: "CaptureText");
        glyph.Opacity = 0.45;

        return new Border
        {
            Width = 18,
            Height = 32,
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动工具条",
            Child = glyph,
        };
    }

    /// <summary>麦克风 / 扬声器：勾上 = 绿色 + 实心图标（上游只换图标颜色，不换底色）。</summary>
    private ToggleButton DeviceToggle(string iconOff, string tip) => new()
    {
        Style = (Style)FindResource("BarToggleAccent"),
        Content = MakeIcon(iconOff),
        ToolTip = tip,
        IsChecked = false,
    };

    /// <summary>导出设置那一行的显隐（上游 `setRecordingExportSettingsVisible`）。</summary>
    private void SetExportRow(bool visible)
    {
        if (StyleBar == null) return;

        StyleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // 第二行显隐会改整个面板的高 / 宽：贴边判定要重算
        ReconcileAutoHide();

        if (_exportToggle.IsChecked != visible)
        {
            bool was = _syncing;
            _syncing = true;
            _exportToggle.IsChecked = visible;
            _syncing = was;
        }

        if (!visible)
        {
            foreach (var popover in _popovers) popover.Hide();
            CloseColorPicker();
            return;
        }

        ClearToolState();
    }

    /// <summary>回到「没有选中任何工具」（上游 `clearActiveToolState`）：当前工具按选择处理。</summary>
    private void ClearToolState()
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            foreach (var button in _toolButtons) button.IsChecked = false;
        }
        finally
        {
            _syncing = was;
        }

        if (Annotations != null) Annotations.Tool = RecordTool.Select;
    }

    // ============ 第二行 ============

    private void BuildStyleRow()
    {
        var row = new RowBuilder(this, StyleRow, 4, small: true);

        row.Add(FormatPill());
        row.Separator();

        // 激光笔：静态图标 + 当前色触发器 + 5 个色卡
        row.Add(ColorGroupLabel("IconRecTrail", "激光笔拖尾"));
        _laserTrigger = ColorTrigger("激光笔拖尾颜色（点一下改颜色）",
            () => _plan.Options.LaserColor,
            hex => _plan.Options.LaserColor = hex,
            LaserPresets, "激光笔拖尾颜色");
        row.Add(_laserTrigger);
        AddPresets(row, _laserPresets, LaserPresets, "recLaser", () => _plan.Options.LaserColor,
            hex => _plan.Options.LaserColor = hex);

        row.Separator();

        // 鼠标点击：同结构，alpha 128
        row.Add(ColorGroupLabel("IconRecClick", "鼠标点击"));
        _clickTrigger = ColorTrigger("鼠标点击颜色（点一下改颜色）",
            () => _plan.Options.ClickColor,
            hex => _plan.Options.ClickColor = hex,
            ClickPresets, "鼠标点击颜色");
        row.Add(_clickTrigger);
        AddPresets(row, _clickPresets, ClickPresets, "recClick", () => _plan.Options.ClickColor,
            hex => _plan.Options.ClickColor = hex);

        row.Separator();

        // 光标 / 键盘 / 跟随 / 摄像头 / 后期处理
        _cursorBtn = StyleToggle("IconRecCursor", "在录制里显示光标（悬停看鼠标特效）",
            value => _plan.Options.ShowCursor = value);
        row.Add(_cursorBtn);
        _popovers.Add(new HoverPopover(_cursorBtn, BuildCursorOptions(), RefreshPopoverState, HookPopupWindow));

        _keyboardBtn = StyleToggle("IconRecKeyboard", "在录制里显示按键（悬停看收哪些键）",
            value => _plan.Options.Keystroke = value);
        row.Add(_keyboardBtn);
        _popovers.Add(new HoverPopover(_keyboardBtn, BuildKeyboardOptions(), RefreshPopoverState, HookPopupWindow));

        _followBtn = StyleToggle("IconRecFollow", "跟随鼠标：画面跟着光标平移",
            value => _plan.FollowMouse = value);
        row.Add(_followBtn);
        _popovers.Add(new HoverPopover(_followBtn, BuildFollowOptions(), RefreshPopoverState, HookPopupWindow));

        _cameraBtn = StyleToggle("IconRecCamera", "摄像头画中画（拖方块改位置、拖右上角改大小，录制开始后不能改）",
            value =>
            {
                _plan.Options.CameraDevice = value ? DefaultCameraDevice() : -1;
                ReconcileWebcam();
            });
        row.Add(_cameraBtn);

        _postBtn = StyleToggle("IconRecPost", "后期处理特效（悬停看显示什么）",
            value => _plan.Options.PostProcessing = value);
        row.Add(_postBtn);
        _popovers.Add(new HoverPopover(_postBtn, BuildPostOptions(), RefreshPopoverState, HookPopupWindow));

        row.Separator();

        row.Add(DelayPill());

        _effectBtn = AddButton("IconRecEffectSettings", "特效设置：声音 / 摄像头 / 编码器 / 录完裁剪 / 贴边收起",
            (_, _) => OpenOptions(), "BarBtnSm", small: true);
        row.Add(_effectBtn);

        _settingsBtn = AddButton("IconRecSettings", "打开设置", (_, _) => OpenSettings(), "BarBtnSm", small: true);
        row.Add(_settingsBtn);
    }

    /// <summary>摄像头打开时用哪个设备：设置里选过就用它，没选过就 0。</summary>
    private static int DefaultCameraDevice() =>
        AppState.Record.CameraDevice >= 0 ? AppState.Record.CameraDevice : 0;

    /// <summary>格式下拉：MP4 / GIF / APNG / WebP（宽 76，开录后不能改）。</summary>
    private FrameworkElement FormatPill()
    {
        _formatText = new TextBlock
        {
            Style = (Style)FindResource("RecPillText"),
            Text = RecordPlan.FormatName(_plan.Format),
            TextAlignment = TextAlignment.Left,
        };

        var caret = new ShapePath
        {
            Data = (Geometry)FindResource("IconChevronDown"),
            Stroke = (Brush)FindResource("CaptureTextSecondary"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            IsHitTestVisible = false,
        };

        // 内容宽度 = 胶囊 76 − 左 10 − 右 8：Dock 的右对齐要求 DockPanel 自己撑满这段宽度，
        // 否则它按内容收缩，折线箭头会紧跟在「MP4」后面（用户报过「箭头怎么贴在文字上」）。
        var content = new DockPanel { LastChildFill = false, Width = 58 };
        DockPanel.SetDock(_formatText, Dock.Left);
        content.Children.Add(_formatText);

        // 折线箭头贴到胶囊右边（上游下拉的箭头在右侧，不是紧跟文字）
        DockPanel.SetDock(caret, Dock.Right);
        content.Children.Add(caret);

        _formatPill = new Button
        {
            Style = (Style)FindResource("BarPill"),
            Content = content,
            ToolTip = "录制格式（录制开始后不能改）",
            Width = 76,
            Padding = new Thickness(10, 0, 8, 0),
        };
        _formatPill.Click += (_, _) => ShowFormatMenu(_formatPill);

        return _formatPill;
    }

    private void ShowFormatMenu(FrameworkElement anchor)
    {
        // 连点两次只留一个菜单（不然会叠出两个，点哪一项都像没反应）
        if (_formatMenu != null)
        {
            _formatMenu.IsOpen = false;
            _formatMenu = null;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

        foreach (var format in RecordPlan.Formats)
        {
            var item = new MenuItem
            {
                Header = RecordPlan.FormatName(format),
                IsCheckable = true,
                IsChecked = _plan.Format == format,
            };
            var value = format;
            item.Click += (_, _) => ApplyFormat(value);
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => { if (ReferenceEquals(_formatMenu, menu)) _formatMenu = null; };

        _formatMenu = menu;
        menu.IsOpen = true;
    }

    private void ApplyFormat(RecordFormat format)
    {
        _plan.Format = format;
        _formatText.Text = RecordPlan.FormatName(format);
        MetaText.Text = MetaSummary();

        // 动图格式不带音轨：麦克风 / 扬声器跟着禁用（上游 animatedFormat）
        SyncUi();
    }

    private string MetaSummary() =>
        $"{RecordPlan.FormatName(_plan.Format)} · {_plan.EffectiveFps} fps · {_area.Width} × {_area.Height}";

    /// <summary>录制延迟：点一下归零，滚轮 ±1 秒（上游 `RecordingDelayEditor`，宽 64）。</summary>
    private FrameworkElement DelayPill()
    {
        _delayText = new TextBlock
        {
            Style = (Style)FindResource("RecPillText"),
            Text = _plan.Countdown + "s",
            Margin = new Thickness(2, 0, 0, 0),
        };

        var icon = MakeIcon("IconRecDelay", small: true);
        icon.Margin = new Thickness(0, 0, 0, 0);

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(_delayText);

        _delayPill = new Button
        {
            Style = (Style)FindResource("BarPillBordered"),
            Content = content,
            ToolTip = "录制延迟：点一下归零，滚轮调 0–10 秒",
            Width = 64,
            Padding = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        _delayPill.Click += (_, _) =>
        {
            if (_syncing) return;
            _plan.Countdown = 0;
            SyncUi();
        };

        _delayPill.PreviewMouseWheel += (_, e) =>
        {
            if (_syncing || _recording) return;
            int next = _plan.Countdown + (e.Delta > 0 ? 1 : -1);
            _plan.Countdown = RecordPlan.NormalizeCountdown(next);
            SyncUi();
            e.Handled = true;
        };

        return _delayPill;
    }

    /// <summary>色卡组前面那个「这是哪一组」的图标：28 的方框里放 18 的图标，不响应鼠标。</summary>
    private FrameworkElement ColorGroupLabel(string icon, string tip)
    {
        // 这一格不是按钮，图标拿不到按钮前景色，得自己上色（否则是空白格）
        var glyph = MakeIcon(icon, small: true, fill: "CaptureText");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            Style = (Style)FindResource("BarLabel"),
            ToolTip = tip,
            Child = glyph,
        };
    }

    /// <summary>「当前颜色」触发器（28 的色卡外观，点开取色弹层）。</summary>
    private Button ColorTrigger(string tip, Func<string> get, Action<string> set,
        string[] presets, string title)
    {
        var button = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            Background = BrushOf(get()),
            ToolTip = tip,
        };

        button.Click += (_, _) =>
        {
            if (_recording) return;
            OpenColorPicker(button, title, get, set, presets);
        };

        return button;
    }

    /// <summary>一组预设色卡（单选；选中 = 浅主色底 + 主色描边）。</summary>
    private void AddPresets(RowBuilder row, List<(RadioButton Button, string Hex)> list, string[] presets,
        string group, Func<string> get, Action<string> set)
    {
        foreach (string hex in presets)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("SwatchRadio"),
                Background = BrushOf(hex),
                ToolTip = DescribeColor(hex),
                GroupName = group,
                Tag = RecordOptions.AlphaOf(hex) == 0 ? "none" : null,
                IsHitTestVisible = true,
            };

            swatch.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(hex);
                SyncUi();
            };

            list.Add((swatch, hex));
            row.Add(swatch);
        }
    }

    /// <summary>第二行的开关：勾上 = 整格主色（`setScreenshotToolPaletteButtonActive`）。</summary>
    private ToggleButton StyleToggle(string icon, string tip, Action<bool> set)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("BarToggleSm"),
            Content = MakeIcon(icon, small: true),
            ToolTip = tip,
        };

        toggle.Checked += (_, _) => { if (!_syncing) set(true); };
        toggle.Unchecked += (_, _) => { if (!_syncing) set(false); };
        return toggle;
    }

    // ============ 状态 ============

    /// <summary>
    /// 一次算清两行每个按钮的状态（时钟 / 图标 / 可用性 / 勾选 / 色卡）。**唯一实现**：
    /// 计时器、点按钮、改格式、打开选项窗口之后都调它，别在各处另写一份。
    /// </summary>
    private void SyncUi()
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            bool recording = _recording;
            bool finished = _controller.Finished;
            bool paused = recording && _controller.IsPaused;
            bool active = recording && !finished;
            bool idle = !recording;
            bool animated = _plan.IsAnimated;
            bool editable = idle && !finished;

            ClockLabel.Text = recording
                ? RecordPlan.FormatDuration((int)Math.Max(0, _controller.ElapsedMs))
                : "00:00:00";

            // 录制 / 暂停（同一个位置换图标，照上游的 start-stop / pause-resume 两个槽位）
            SetIcon(RecordBtn, active ? "IconRecStop" : "IconRecStart");
            RecordBtn.Foreground = active
                ? (Brush)FindResource("CaptureDanger")     // 停止：danger 红（上游 addActionButton(..., true)）
                : (Brush)FindResource("CapturePrimary");   // 开始：primary 蓝
            RecordBtn.ToolTip = active
                ? RecordShortcuts.WithChord("结束录制（存进采集历史）", RecordShortcutAction.Export)
                : RecordShortcuts.WithChord("开始录制", RecordShortcutAction.ToggleRecording);
            RecordBtn.IsEnabled = !finished;

            // 暂停：录制中才可用，可用时图标是警示黄（上游 `Pause()` + colorWarning）；
            // 暂停后换成蓝色「继续」，空闲时置灰。
            SetIcon(PauseBtn, paused ? "IconRecResume" : "IconRecPause");
            PauseBtn.Foreground = paused
                ? (Brush)FindResource("CapturePrimary")
                : (Brush)FindResource(active ? "CaptureWarning" : "CaptureTextDisabled");
            PauseBtn.ToolTip = paused
                ? RecordShortcuts.WithChord("继续录制", RecordShortcutAction.ToggleRecording)
                : RecordShortcuts.WithChord("暂停（暂停的时间不进成品）", RecordShortcutAction.ToggleRecording);
            PauseBtn.IsEnabled = active;

            // 麦克风 / 扬声器：图标形状 + 颜色。可用性照上游 `(idle || enabled) && !busy && !animated`：
            // 录制中只有本来开着的那一路还能点（点开的是增益弹层，不是切开关）。
            _micBtn.IsChecked = _plan.Options.Microphone;
            SetIcon(_micBtn, _plan.Options.Microphone ? "IconRecMic" : "IconRecMicOff");
            _micBtn.IsEnabled = !animated && !finished && (idle || _plan.Options.Microphone);
            _micBtn.ToolTip = animated ? "动图格式不含音频"
                : idle ? "录麦克风（悬停调增益）"
                : "麦克风增益：点一下或悬停打开（录制中可调）";

            _speakerBtn.IsChecked = _plan.Options.SystemAudio;
            SetIcon(_speakerBtn, _plan.Options.SystemAudio ? "IconRecAudio" : "IconRecAudioOff");
            _speakerBtn.IsEnabled = !animated && !finished && (idle || _plan.Options.SystemAudio);
            _speakerBtn.ToolTip = animated ? "动图格式不含音频"
                : idle ? "录系统声音（悬停调增益）"
                : "系统声音增益：点一下或悬停打开（录制中可调）";

            RefreshGainPanels();

            // 收尾动作：录制中可用；空闲时只要历史里有录屏也可用（LTools 作用在最近一条上）
            bool canFinish = active || _hasLastRecord;
            _trimBtn.IsEnabled = canFinish;
            _saveBtn.IsEnabled = canFinish;
            _copyBtn.IsEnabled = canFinish;
            _copyBtn.Foreground = canFinish
                ? (Brush)FindResource("CapturePrimary")
                : (Brush)FindResource("CaptureTextDisabled");

            _trimBtn.ToolTip = canFinish ? "裁剪 / 倍速：最近一条录屏" : "裁剪 / 倍速：还没有录屏";
            _saveBtn.ToolTip = canFinish ? "另存为：最近一条录屏" : "另存为：还没有录屏";
            _copyBtn.ToolTip = canFinish
                ? RecordShortcuts.WithChord("复制：最近一条录屏文件", RecordShortcutAction.CopyToClipboard)
                : "复制：还没有录屏";
            _undoBtn.ToolTip = RecordShortcuts.WithChord("撤销标注", RecordShortcutAction.Undo);
            _redoBtn.ToolTip = RecordShortcuts.WithChord("重做标注", RecordShortcutAction.Redo);
            CancelBtn.ToolTip = RecordShortcuts.WithChord("关闭（取消这次录制）", RecordShortcutAction.EndRecording);

            _undoBtn.IsEnabled = Annotations?.CanUndo == true;
            _redoBtn.IsEnabled = Annotations?.CanRedo == true;

            // 第二行整体：录制中禁用（上游 editable = Idle && !busy）
            StyleBar.IsEnabled = editable;
            if (!editable)
            {
                foreach (var popover in _popovers) popover.Hide();
                CloseColorPicker();
            }

            _formatText.Text = RecordPlan.FormatName(_plan.Format);
            _formatPill.IsEnabled = editable;

            SyncSwatches(_laserTrigger, _laserPresets, _plan.Options.LaserColor);
            SyncSwatches(_clickTrigger, _clickPresets, _plan.Options.ClickColor);

            _cursorBtn.IsChecked = _plan.Options.ShowCursor;
            _keyboardBtn.IsChecked = _plan.Options.Keystroke;
            _followBtn.IsChecked = _plan.FollowMouse;
            _cameraBtn.IsChecked = _plan.Options.CameraDevice >= 0;
            _postBtn.IsChecked = _plan.Options.PostProcessing;

            _delayText.Text = _plan.Countdown + "s";
            _delayPill.ToolTip = "录制延迟 " + _plan.Countdown + " 秒：点一下归零，滚轮调 0–10 秒";

            RefreshPopoverState();

            // 隐藏的状态行（会话 / harness 用；界面上没有位置）
            if (finished) StatusText.Text = "正在收尾…";
            else if (!recording) StatusText.Text = "等待开始 · 点录制按钮开录";
            else StatusText.Text = paused ? "已暂停 · 时间不再走" : "录制中 · 点方块结束并保存";
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>色卡组：触发器显示当前色，预设按当前值选中（找不到相同的就都不选）。</summary>
    private void SyncSwatches(Button trigger, List<(RadioButton Button, string Hex)> presets, string color)
    {
        string value = RecordOptions.NormalizeColor(color, "#00000000");

        trigger.Background = BrushOf(value);
        trigger.Tag = RecordOptions.AlphaOf(value) == 0 ? "none" : null;

        foreach (var (button, hex) in presets)
        {
            button.IsChecked = string.Equals(hex, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RefreshPopoverState()
    {
        if (_highlightBox != null)
        {
            _highlightBox.IsChecked = _plan.Options.MouseHighlight;
            _clicksBox.IsChecked = _plan.Options.RecordClicks;
            _scrollBox.IsChecked = _plan.Options.RecordScroll;

            _numberKeysBox.IsChecked = _plan.Options.RecordNumberKeys;
            _letterKeysBox.IsChecked = _plan.Options.RecordLetterKeys;
            _controlKeysBox.IsChecked = _plan.Options.RecordControlKeys;
            _preserveKeysBox.IsChecked = _plan.Options.PreserveShortcuts;
            _preserveKeysBox.IsEnabled = _plan.Options.RecordControlKeys;

            _smoothBox.IsChecked = _plan.FollowOptions.SmoothMovement;
            _jitterBox.IsChecked = _plan.FollowOptions.ReduceJitter;

            _progressBarRadio.IsChecked = _plan.Options.PostProcessingProgressBar;
            _playbackTimeRadio.IsChecked = !_plan.Options.PostProcessingProgressBar;
        }
    }

    // ============ 收尾动作 ============

    /// <summary>最近一条录屏（采集历史里按创建时间倒序的第一条）。</summary>
    private static CaptureEntry? LastRecord()
    {
        try
        {
            return CaptureCatalog.Load()
                .Where(e => e.IsRecord)
                .OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "读采集历史失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>捡一次「历史里有没有录屏」，并刷新三个收尾按钮的可用性。</summary>
    private void UpdateToolButtons()
    {
        _hasLastRecord = LastRecord() != null;
        SyncUi();
    }

    private void OpenTrim()
    {
        if (LastRecord() is not { } entry) return;

        try { RecordTrimSession.Open(entry); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Trim", "打开裁剪窗口失败：" + ex.Message); }
    }

    private void SaveLast()
    {
        if (LastRecord() is not { } entry) return;

        string path = CaptureCatalog.FilePath(entry);
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存录屏",
                FileName = RecordOptions.FileNameFor(_plan.Options.FilenameFormat, DateTime.Now)
                           + System.IO.Path.GetExtension(path),
                Filter = "视频 / 动图|*.mp4;*.gif;*.apng;*.webp|所有文件|*.*",
                InitialDirectory = System.IO.Directory.Exists(_plan.Options.SaveDirectory)
                    ? _plan.Options.SaveDirectory
                    : System.IO.Path.GetDirectoryName(path),
            };
            if (dlg.ShowDialog(this) != true) return;

            System.IO.File.Copy(path, dlg.FileName, overwrite: true);
            ToastService.Show("已另存到 " + dlg.FileName, "录屏", 2600, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("另存失败：" + ex.Message, "录屏", 3600, ToastKind.error);
        }
    }

    private void CopyLast()
    {
        if (LastRecord() is not { } entry) return;

        try
        {
            Clipboard.SetText(CaptureCatalog.FilePath(entry));
            ToastService.Show("已复制录屏文件路径", "录屏", 1800, ToastKind.success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "复制路径失败：" + ex.Message);
        }
    }

    private void OpenRecordsFolder() => RecordSession.OpenRecordsFolder();

    private void OpenOptions()
    {
        try
        {
            if (RecordOptionsWindow.Show(this, _plan.Options))
            {
                SyncUi();
                ReconcileAutoHide();   // 「贴边自动收起」可能刚被改掉
                ReconcileWebcam();     // 摄像头开关 / 尺寸 / 形状 / 方向可能刚被改掉
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "打开录屏选项失败：" + ex.Message);
        }
    }

    private void OpenSettings()
    {
        try { ConfigHelper.Open(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "打开设置失败：" + ex.Message); }
    }

    // ============ 小工具 ============

    /// <summary>
    /// 造一个图标（几何在 <c>CaptureIcons.xaml</c>）。
    /// <paramref name="fill"/> 只给**不在按钮里**的图标用：<c>IconPath</c> 的前景色是
    /// 从祖先 <see cref="ButtonBase"/> 取的，放在 <see cref="Border"/> 里会一个像素都画不出来。
    /// </summary>
    private ShapePath MakeIcon(string key, bool small = false, string? fill = null)
    {
        var path = new ShapePath
        {
            Data = (Geometry)FindResource(key),
            Style = (Style)FindResource(small ? "IconPathSm" : "IconPath"),
            IsHitTestVisible = false,
        };

        if (fill != null) path.Fill = (Brush)FindResource(fill);
        return path;
    }

    /// <summary>换掉按钮里的图标（两行的按钮都只装一个 <see cref="ShapePath"/>）。</summary>
    private void SetIcon(ContentControl control, string key)
    {
        if (control.Content is ShapePath path) path.Data = (Geometry)FindResource(key);
    }

    private Button AddButton(string icon, string tip, RoutedEventHandler click,
        string style = "BarBtn", bool small = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource(style),
            ToolTip = tip,
            Content = MakeIcon(icon, small),
        };
        button.Click += click;
        return button;
    }

    private Border Separator(bool small = false) =>
        new() { Style = (Style)FindResource(small ? "BarSeparatorSm" : "BarSeparator") };

    internal static Brush BrushOf(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    internal static string DescribeColor(string hex)
    {
        string text = RecordOptions.NormalizeColor(hex, "#00000000");
        return RecordOptions.AlphaOf(text) == 0 ? "透明" : "#" + text[^6..];
    }
}
