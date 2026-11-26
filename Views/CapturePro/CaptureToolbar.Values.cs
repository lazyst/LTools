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

/// <summary>CaptureToolbar 的「取值控件（多选胶囊 / 填充区域 / 步骤条 / 刷新）」部分。</summary>
public partial class CaptureToolbar
{
    /// <summary>
    /// 枚举型参数：一排文字胶囊，点了就换（单选）。
    /// 与颜色胶囊同一套「_syncing 防回环」写法，别让刷新把用户刚点的值又切回去。
    /// <paramref name="visible"/> 非空时按它显隐单个胶囊（思考气泡只在气泡工具里出现）。
    /// </summary>
    private FrameworkElement Chips<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip, string? group = null, Func<T, bool>? visible = null)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var chips = new List<(RadioButton Button, T Value)>();
        foreach (var value in values)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "captureChoice" + (group ?? label),
                Content = text(value),
                ToolTip = tip(value),
                Tag = value,
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(value);
                StyleChanged?.Invoke();
            };
            chips.Add((chip, value));
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            var current = get();
            _syncing = true;
            foreach (var (button, value) in chips)
            {
                bool shown = visible?.Invoke(value) ?? true;
                button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                button.IsChecked = shown && EqualityComparer<T>.Default.Equals(value, current);
            }
            _syncing = false;
        });

        return host;
    }

    /// <summary>
    /// 自动打码的「填充区域」下拉：按识别到的类型一次填满
    /// （上游 <c>available()</c> 不成立时置灰；菜单选完即关，它是一次性命令，不是持久开关）。
    /// </summary>
    private FrameworkElement FillRegions()
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label("填充"));

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock
        {
            Text = "填充区域",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
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
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(caption);
        content.Children.Add(chevron);

        var button = new Button
        {
            Style = (Style)FindResource("BarPill"),
            Content = content,
            // 首帧就是「还没有识别结果」（上游 available() = false）：别等第一次 UpdateChrome
            IsEnabled = false,
            Opacity = 0.45,
            ToolTip = "还没有识别结果：先在选区里等一下（识别中会扫过一道光带）",
        };
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : 0.45;
        button.Click += (_, e) =>
        {
            if (!button.IsEnabled) return;

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
            foreach (var category in RegionCategories.All)
            {
                string picked = category;
                var item = new MenuItem { Header = RegionCategories.Label(category) };
                item.Click += (_, _) => AutoFilterFillRequested?.Invoke(picked);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
            e.Handled = true;
        };

        _fillRegionsButton = button;
        host.Children.Add(button);
        return host;
    }

    /// <summary>
    /// 「填充区域」能不能点：识别到区域且选区没变过（上游 <c>available()</c>）。
    /// 工具栏不认识识别结果，由覆盖层在检测完成 / 选区变化时告诉它。
    /// </summary>
    internal void SetAutoFilterState(bool available)
    {
        if (_fillRegionsButton == null) return;
        _fillRegionsButton.IsEnabled = available;
        _fillRegionsButton.ToolTip = available
            ? "按类型一次填满所有识别到的区域（用当前打码方式与强度）"
            : "还没有识别结果：先在选区里等一下（识别中会扫过一道光带）";
    }

    private void AddHost(ToolParam param, FrameworkElement host)
    {
        _paramHosts[param] = host;
        ParamRow.Children.Add(host);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
        FontSize = 12,
        Margin = new Thickness(10, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement Step(string label, double min, double max, double step,
        Func<double> get, Action<double> set, Func<double, string> format, double reset)
    {
        // 透明底色：整格（不只是字形）都能滚轮 / 点击，不然「点在数字旁边空白处」没反应
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        host.Children.Add(Label(label));

        var value = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7)),
            FontSize = 12,
            MinWidth = 40,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            ToolTip = $"滚轮微调（{step:0.##}）· 点一下复位到 {format(reset)}",
        };

        Button Minus() => new()
        {
            Style = (Style)FindResource("StepBtn"),
            Content = "−",
        };

        var minus = Minus();
        var plus = Minus();
        plus.Content = "+";

        minus.Click += (_, _) =>
        {
            set(Math.Clamp(RoundTo(get() - step, step), min, max));
            RefreshValues();
            StyleChanged?.Invoke();
        };
        plus.Click += (_, _) =>
        {
            set(Math.Clamp(RoundTo(get() + step, step), min, max));
            RefreshValues();
            StyleChanged?.Invoke();
        };

        // 滚轮在数值上直接微调（照 Snow Shot：光标停在参数上滚就是调它）；
        // 点一下数值复位——「调过头了」比「找不到恢复按钮」常见得多。
        host.MouseWheel += (_, e) =>
        {
            double next = Math.Clamp(RoundTo(get() + (e.Delta > 0 ? step : -step), step), min, max);
            if (Math.Abs(next - get()) < 1e-9) return;
            set(next);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };
        value.MouseLeftButtonUp += (_, e) =>
        {
            set(reset);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };

        host.Children.Add(minus);
        host.Children.Add(value);
        host.Children.Add(plus);

        _refreshers.Add(() => value.Text = format(get()));
        return host;
    }

    /// <summary>按当前样式刷新所有参数格（覆盖层改了样式——如序号自增——也要能把界面拉回来）。</summary>
    public void RefreshValues()
    {
        if (_style == null) return;
        ClearMixedMarks();
        foreach (var refresh in _refreshers) refresh();
        MarkMixedValues();
    }

}
