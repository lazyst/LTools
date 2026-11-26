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

/// <summary>CaptureToolbar 的「参数行控件（颜色 / 字号 / 字体 / 数字 / 开关 / 测距）」部分。</summary>
public partial class CaptureToolbar
{
    /// <summary>端点样式下拉的选项：上游 distance 家族的 14 种（不含方块 / 倒三角）。</summary>
    private static readonly Arrowhead[] DistanceEndpoints =
    {
        Arrowhead.None, Arrowhead.Arrow, Arrowhead.Bar, Arrowhead.Dot, Arrowhead.Circle,
        Arrowhead.CircleOutline, Arrowhead.IndentedTriangle, Arrowhead.Triangle,
        Arrowhead.TriangleOutline, Arrowhead.Diamond, Arrowhead.DiamondOutline,
        Arrowhead.CrowfootOne, Arrowhead.CrowfootMany, Arrowhead.CrowfootOneOrMany,
    };

    /// <summary>
    /// 实测值一格：选中一条测距时可直接填它「应该是多少」，写回的是系数
    /// （上游 <c>setDistanceValue</c>：<c>factor = 值 / 实测长度</c>，钳 0.01–1000）。
    /// 没选中时置灰、显示占位 <c>—</c>。
    /// </summary>
    private FrameworkElement MeasureValueRow()
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label("实测值"));

        var box = new TextBox
        {
            Width = 74,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = "选中一条测距后可以填实测值（回车生效），会换算成系数",
        };
        box.LostFocus += (_, _) => CommitMeasured(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitMeasured(box);
            e.Handled = true;
        };

        _measuredBox = box;
        _refreshers.Add(RefreshMeasured);
        RefreshMeasured();

        host.Children.Add(box);
        return host;
    }

    private TextBox? _measuredBox;

    /// <summary>选中那条测距的实测长度（屏幕物理像素）；0 = 没有可编辑的目标。</summary>
    private double _measuredLength;

    private void CommitMeasured(TextBox box)
    {
        if (_syncing || _measuredLength <= 0) return;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            RefreshMeasured();
            return;
        }

        if (!double.IsFinite(value) || value <= 0) { RefreshMeasured(); return; }
        DistanceValueSubmitted?.Invoke(value);
        RefreshMeasured();
    }

    private void RefreshMeasured()
    {
        if (_measuredBox == null) return;
        if (_measuredLength <= 0)
        {
            _measuredBox.IsEnabled = false;
            if (_measuredBox.Text.Length > 0) _measuredBox.Text = string.Empty;
            _measuredBox.ToolTip = "先选中一条测距，才能填它的实测值";
            return;
        }

        _measuredBox.IsEnabled = true;
        _measuredBox.ToolTip = "选中一条测距后可以填实测值（回车生效），会换算成系数";
        if (_measuredBox.IsKeyboardFocusWithin) return;
        double factor = _style?.DistanceFactor ?? 1;
        _measuredBox.Text = (_measuredLength * factor).ToString("0.###", CultureInfo.CurrentCulture);
    }

    /// <summary>覆盖层告诉工具栏「选中的这条测距有多长」（物理像素；0 = 没有）。</summary>
    internal void SetMeasuredDistance(double lengthPx)
    {
        _measuredLength = double.IsFinite(lengthPx) && lengthPx > 0 ? lengthPx : 0;
        RefreshMeasured();
    }

    /// <summary>小数位（0–3）：四个胶囊比加减号快。</summary>
    private FrameworkElement DecimalsRow(string label, Func<int> get, Action<int> set, string group) =>
        Chips(label, new[] { 0, 1, 2, 3 }, get, set,
            v => v == 0 ? "整数" : v + " 位", v => v == 0 ? "不保留小数" : $"保留 {v} 位小数", group);

    /// <summary>
    /// 选项太多的参数（端点 14 种）：一个按钮 + <see cref="ContextMenu"/>，比一排胶囊省地方。
    /// 点按钮不抢焦点、菜单项带勾选，选中即写回并抛 <see cref="StyleChanged"/>。
    /// </summary>
    private FrameworkElement Picker<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var button = new Button
        {
            Style = (Style)FindResource("StepBtn"),
            MinWidth = 58,
            Content = text(get()),
            ToolTip = tip(get()),
        };

        button.Click += (_, _) =>
        {
            var current = get();
            var menu = new ContextMenu();
            foreach (var value in values)
            {
                var item = new MenuItem
                {
                    Header = text(value),
                    IsCheckable = true,
                    IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                    ToolTip = tip(value),
                };
                var captured = value;
                item.Click += (_, _) =>
                {
                    set(captured);
                    RefreshValues();
                    StyleChanged?.Invoke();
                };
                menu.Items.Add(item);
            }

            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        _refreshers.Add(() =>
        {
            var current = get();
            button.Content = text(current);
            button.ToolTip = tip(current);
        });

        host.Children.Add(button);
        return host;
    }

    /// <summary>一格文字输入（水印内容）：失焦与回车都立刻写回样式，工具栏不自己存副本。</summary>
    private FrameworkElement Text(string label, Func<string> get, Action<string> set, string tip, double width = 110)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var box = new TextBox
        {
            Width = width,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = tip,
        };

        void Commit()
        {
            if (_syncing) return;
            set(box.Text);
            StyleChanged?.Invoke();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };

        _refreshers.Add(() =>
        {
            string value = get();
            if (!box.IsKeyboardFocusWithin && box.Text != value) box.Text = value;
        });

        host.Children.Add(box);
        return host;
    }

    /// <summary>
    /// 一排颜色胶囊：读 <paramref name="get"/>、写 <paramref name="set"/>，<c>GroupName</c> 分开。
    /// 主色（跟共享颜色开关联动）/ 填充色 / 文字描边色各来一排，**同一份实现**——
    /// 别为某个颜色再写一遍「造胶囊 + _syncing 回填」。
    /// </summary>
    /// <remarks>
    /// 三族的预设色（描边 / 填充 / 荧光）一次全建出来，按
    /// <paramref name="family"/> 决定哪一族可见——主色那一排在荧光笔与矩形荧光上换成荧光预设，
    /// 换工具时 <see cref="Sync"/> 会重跑 refresher，格子跟着换，不用重建参数条。
    /// </remarks>
    private FrameworkElement ColorRow(string label, string group, Func<Color> get, Action<Color> set,
        Func<ColorPresetFamily> family)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var rows = new List<(ColorPresetFamily Family, List<RadioButton> Chips)>();
        foreach (var preset in new[]
                 {
                     ColorPresetFamily.Stroke, ColorPresetFamily.Fill, ColorPresetFamily.Highlight,
                 })
        {
            var chips = new List<RadioButton>();
            string chipGroup = group + "-" + preset;

            foreach (var color in ColorPresets.Get(preset))
            {
                var chip = new RadioButton
                {
                    Style = (Style)FindResource("ColorChip"),
                    GroupName = chipGroup,
                    Background = ColorPresets.Swatch(color),
                    Tag = color,
                    ToolTip = ColorPresets.Format(color),
                    Visibility = Visibility.Collapsed,
                };
                chip.Checked += (_, _) =>
                {
                    if (_syncing) return;
                    set((Color)chip.Tag);
                    StyleChanged?.Invoke();
                };
                chips.Add(chip);
                host.Children.Add(chip);
            }

            rows.Add((preset, chips));
        }

        _refreshers.Add(() =>
        {
            var current = family();
            var value = get();
            _syncing = true;
            foreach (var (preset, chips) in rows)
            {
                bool active = preset == current;
                foreach (var chip in chips)
                {
                    chip.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    chip.IsChecked = active && (Color)chip.Tag == value;
                }
            }
            _syncing = false;
        });

        return host;
    }

    /// <summary>固定用某一族预设的颜色排（填充 / 文字描边 / 箭头外描边）。</summary>
    private FrameworkElement ColorRow(string label, string group, Func<Color> get, Action<Color> set,
        ColorPresetFamily family) => ColorRow(label, group, get, set, () => family);

    /// <summary>
    /// 字号：数字步进 + S / M / L / XL 四个预设（上游 <c>ScreenshotToolPaletteFontEditor</c> 的
    /// <c>fontSizes()</c> = 24 / 30 / 42 / 54，与画笔宽度预设共用同一组值）。
    /// </summary>
    private FrameworkElement FontSizeRow() => PresetSizeRow("字号",
        () => _style?.TextSize ?? TextInk.DefaultFontSize,
        v => { if (_style != null) _style.TextSize = v; },
        new (string, double)[] { ("S", 24), ("M", 30), ("L", 42), ("XL", 54) },
        "captureTextSize", TextInk.DefaultFontSize, TextInk.MinFontSize, TextInk.MaxFontSize);

    /// <summary>
    /// 水印字号：数字步进 + 四个预设（上游 <c>style_presets::watermarkFontSizes()</c> = 12 / 16 / 24 / 30）。
    /// </summary>
    private FrameworkElement WatermarkFontSizeRow() => PresetSizeRow("字号",
        () => _style?.FontSize ?? 16,
        v => { if (_style != null) _style.FontSize = v; },
        new (string, double)[] { ("12", 12), ("16", 16), ("24", 24), ("30", 30) },
        "captureWatermarkSize", 16, WatermarkSpec.MinFontSize, WatermarkSpec.MaxFontSize);

    /// <summary>字号一格：加减 / 滚轮 / 点数字复位 + 一排预设胶囊（文字族与水印族共用）。</summary>
    private FrameworkElement PresetSizeRow(string label, Func<double> get, Action<double> set,
        (string Label, double Size)[] presets, string group, double reset, double min, double max)
    {
        var row = Step(label, min, max, 2, get, set, v => v.ToString("0"), reset);
        if (row is not Panel host) return row;

        var chips = new List<RadioButton>();
        foreach (var (text, size) in presets)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "capture" + group,
                Content = text,
                Tag = size,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = $"{text}（{size:0}px）",
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(size);
                RefreshValues();
                StyleChanged?.Invoke();
            };
            chips.Add(chip);
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            double current = get();
            _syncing = true;
            foreach (var chip in chips) chip.IsChecked = Math.Abs((double)chip.Tag - current) < 0.01;
            _syncing = false;
        });

        return row;
    }

    /// <summary>单个开关胶囊（放大镜的「源轮廓」这类布尔参数）。</summary>
    private FrameworkElement Toggle(string label, Func<bool> get, Action<bool> set, string tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var chip = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "开",
            ToolTip = tip,
        };
        chip.Click += (_, _) =>
        {
            if (_syncing) return;
            set(chip.IsChecked == true);
            RefreshValues();
            StyleChanged?.Invoke();
        };

        host.Children.Add(chip);

        _refreshers.Add(() =>
        {
            bool on = get();
            _syncing = true;
            chip.IsChecked = on;
            chip.Content = on ? "开" : "关";
            _syncing = false;
        });

        return host;
    }

    /// <summary>整数参数一格（序号的「下一个序号」）：回车 / 失焦写回，滚轮 ±1。</summary>
    private FrameworkElement NumberBox(string label, Func<int> get, Action<int> set, string tip)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label(label));

        var box = new TextBox
        {
            Width = 54,
            Height = 22,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = tip,
        };

        void Commit()
        {
            if (_syncing) return;
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
                && value >= 0)
            {
                set(value);
                StyleChanged?.Invoke();
            }
            RefreshValues();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        box.PreviewMouseWheel += (_, e) =>
        {
            if (_syncing) return;
            set(Math.Max(0, get() + (e.Delta > 0 ? 1 : -1)));
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };

        _refreshers.Add(() =>
        {
            if (box.IsKeyboardFocusWithin) return;
            string text = get().ToString(CultureInfo.CurrentCulture);
            if (box.Text != text) box.Text = text;
        });

        host.Children.Add(box);
        return host;
    }

    /// <summary>
    /// 水印模板：一个胶囊 + <see cref="ContextMenu"/>（点名字套用、底部「添加模板」「删除模板」）。
    /// 套用时按上游规则展开 <c>{text}</c> 与时间占位，结果写进水印文字。
    /// </summary>
    private FrameworkElement WatermarkTemplates()
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Label("模板"));

        var button = new Button
        {
            Style = (Style)FindResource("StepBtn"),
            MinWidth = 66,
            Content = "模板",
            ToolTip = "水印模板：选中即套用（" + WatermarkTemplateText.Hint + "）",
        };

        button.Click += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu();

            foreach (var template in _styles?.WatermarkTemplates ?? Array.Empty<WatermarkTemplate>())
            {
                var captured = template;
                var item = new MenuItem { Header = captured.Name, ToolTip = captured.Value };
                item.Click += (_, _) => ApplyWatermarkTemplate(captured);
                menu.Items.Add(item);
            }

            if (menu.Items.Count > 0) menu.Items.Add(new Separator());

            var add = new MenuItem { Header = "添加模板…" };
            add.Click += (_, _) => AddWatermarkTemplate();
            menu.Items.Add(add);

            var removable = _styles?.WatermarkTemplates;
            if (removable is { Count: > 0 })
            {
                var delete = MenuFx.Submenu(menu, "删除模板");
                foreach (var template in removable)
                {
                    var captured = template.Name;
                    var item = new MenuItem { Header = captured };
                    item.Click += (_, _) => DeleteWatermarkTemplate(captured);
                    delete.Items.Add(item);
                }
            }

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        host.Children.Add(button);
        return host;
    }

    /// <summary>套用一个模板：<c>{text}</c> 换成当前文字、时间占位换成现在（上游 resolution 同一套）。</summary>
    private void ApplyWatermarkTemplate(WatermarkTemplate template)
    {
        if (_style == null) return;
        _style.WatermarkText = WatermarkTemplateText.Resolve(template.Value, _style.WatermarkText,
            DateTime.Now);
        RefreshValues();
        StyleChanged?.Invoke();
    }

    /// <summary>添加模板：先问名字、再问内容（内容里可以写 <c>{text}</c> 与时间占位）。</summary>
    private void AddWatermarkTemplate()
    {
        if (_styles == null) return;

        string suggested = string.IsNullOrWhiteSpace(_style?.WatermarkText)
            ? "模板 " + (_styles.WatermarkTemplates.Count + 1)
            : _style!.WatermarkText;

        var name = InputDialog.Show(Window.GetWindow(this), "新建水印模板", "模板名称", suggested);
        if (!name.ok || string.IsNullOrWhiteSpace(name.value)) return;

        string defaultValue = string.IsNullOrWhiteSpace(_style?.WatermarkText)
            ? "{text}"
            : _style!.WatermarkText;
        var value = InputDialog.Show(Window.GetWindow(this), "新建水印模板", "模板内容（" +
            WatermarkTemplateText.Hint + "）", defaultValue);
        if (!value.ok || string.IsNullOrWhiteSpace(value.value)) return;

        _styles.AddWatermarkTemplate(name.value, value.value);
        CaptureStyleStore.SaveSoon();
        ApplyWatermarkTemplate(new WatermarkTemplate(name.value.Trim(), value.value.Trim()));
    }

    private void DeleteWatermarkTemplate(string name)
    {
        if (_styles == null) return;
        if (!_styles.RemoveWatermarkTemplate(name)) return;
        CaptureStyleStore.SaveSoon();
    }


    /// <summary>
    /// 粗体 / 斜体：两个可以同时按下的胶囊（上游 <c>ScreenshotToolPaletteTextEmphasisEditor</c>）。
    /// </summary>
    private FrameworkElement Emphasis()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("粗细"));

        var bold = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "B",
            FontWeight = FontWeights.Bold,
            ToolTip = "粗体",
        };
        var italic = new ToggleButton
        {
            Style = (Style)FindResource("ToggleChip"),
            Content = "I",
            FontStyle = FontStyles.Italic,
            ToolTip = "斜体",
        };

        // 用 Click 而不是 Checked / Unchecked：一套处理，`_syncing` 回填不会触发它
        bold.Click += (_, _) =>
        {
            if (_syncing) return;
            if (_style != null) _style.TextBold = bold.IsChecked == true;
            StyleChanged?.Invoke();
        };
        italic.Click += (_, _) =>
        {
            if (_syncing) return;
            if (_style != null) _style.TextItalic = italic.IsChecked == true;
            StyleChanged?.Invoke();
        };

        host.Children.Add(bold);
        host.Children.Add(italic);

        _refreshers.Add(() =>
        {
            _syncing = true;
            bold.IsChecked = _style?.TextBold ?? false;
            italic.IsChecked = _style?.TextItalic ?? false;
            _syncing = false;
        });

        return host;
    }

    /// <summary>字体族下拉里「没有指定」的那一项（= 上游空 <c>fontFamily</c>）。</summary>
    private const string DefaultFontLabel = "默认（微软雅黑 UI）";

    /// <summary>系统字体族：量一次就缓存（<see cref="Fonts.SystemFontFamilies"/> 在部分机器上要几十毫秒）。</summary>
    private static IReadOnlyList<string>? _fontFamilies;

    private static IReadOnlyList<string> FontFamilies()
    {
        if (_fontFamilies != null) return _fontFamilies;

        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (var family in Fonts.SystemFontFamilies)
            {
                string name = family.Source;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
        }
        catch (Exception ex)
        {
            LTools.Core.AppLog.Warn("capture", "枚举系统字体失败：" + ex.Message);
        }

        _fontFamilies = names.ToList();
        return _fontFamilies;
    }

    /// <summary>
    /// 字体族：一个胶囊 + 可搜索的弹层（上游 <c>FontEditor</c> 的 searchable family select）。
    /// </summary>
    private FrameworkElement FontPicker()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("字体"));

        var caption = new TextBlock
        {
            Text = DefaultFontLabel,
            FontSize = 12,
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
        };
        var chevron = new ShapePath
        {
            Data = (Geometry)FindResource("IconChevronDown"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(caption);
        content.Children.Add(chevron);

        var button = new Button
        {
            Style = (Style)FindResource("BarPill"),
            Content = content,
            ToolTip = "字体（可以搜索）",
        };

        var search = new TextBox
        {
            Height = 24,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2A)),
            ToolTip = "输入字体名筛选（Enter 选第一个，Esc 关闭）",
        };

        var list = new ListBox
        {
            MaxHeight = 220,
            Width = 220,
            Margin = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        var stack = new StackPanel();
        stack.Children.Add(search);
        stack.Children.Add(list);

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = stack,
        };

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = card,
        };

        bool filling = false;

        void Fill(string filter)
        {
            filling = true;
            list.Items.Clear();
            list.Items.Add(DefaultFontLabel);
            foreach (string name in FontFamilies())
                if (filter.Length == 0 || name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    list.Items.Add(name);
            filling = false;
        }

        void Close() => popup.IsOpen = false;

        void Choose(string? name)
        {
            if (name == null) return;
            if (_style != null) _style.FontFamily = name == DefaultFontLabel ? string.Empty : name;
            Close();
            RefreshValues();
            StyleChanged?.Invoke();
        }

        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (popup.IsOpen)
            {
                Close();
                return;
            }

            search.Text = string.Empty;
            Fill(string.Empty);
            list.SelectedItem = null;
            popup.IsOpen = true;
            search.Focus();
        };

        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (list.Items.Count > 0)
                        Choose((list.SelectedItem as string) ?? list.Items[0] as string);
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                {
                    int delta = e.Key == Key.Down ? 1 : -1;
                    int next = Math.Clamp(list.SelectedIndex + delta, 0, Math.Max(0, list.Items.Count - 1));
                    if (list.Items.Count > 0) list.SelectedIndex = next;
                    e.Handled = true;
                    break;
                }
            }
        };

        list.SelectionChanged += (_, _) =>
        {
            if (filling || !popup.IsOpen) return;
            if (list.SelectedItem is not string name) return;
            Choose(name);
        };

        host.Children.Add(button);
        _fontPopup = popup;

        _refreshers.Add(() =>
        {
            string family = _style?.FontFamily ?? string.Empty;
            caption.Text = string.IsNullOrWhiteSpace(family) ? DefaultFontLabel : family;
            caption.ToolTip = caption.Text;
        });

        return host;
    }

}
