using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏面板的弹层：第二行的悬停选项（<c>AdPopover</c>）与取色器（<c>AdColorPicker</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 悬停弹层照上游：`Trigger::Hover` + `Placement::Bottom`，白底圆角 8 + 阴影、内边距 12、行距 8。
/// 鼠标移开后 120ms 内还在按钮或弹层里就不收（直接 `IsMouseOver` 判定，两个都是真窗口）。
/// </para>
/// <para>
/// 这些弹层**也必须不入镜**（`WDA_EXCLUDEFROMCAPTURE`），否则摆着面板等用户点录制时，
/// 弹层会被录进第一帧。悬停弹层还不抢焦点（与工具条同一套 `WS_EX_NOACTIVATE`）；
/// 取色器要能打字，所以**故意允许激活**（只在空闲时可打开），但它同样不入镜。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    // ============ 悬停弹层 ============

    private FrameworkElement BuildCursorOptions()
    {
        _highlightBox = PopCheck("鼠标高亮", value => _plan.Options.MouseHighlight = value);
        _clicksBox = PopCheck("记录鼠标点击", value => _plan.Options.RecordClicks = value);
        _scrollBox = PopCheck("记录鼠标滚动", value => _plan.Options.RecordScroll = value);
        return PopStack(_highlightBox, _clicksBox, _scrollBox);
    }

    private FrameworkElement BuildKeyboardOptions()
    {
        _numberKeysBox = PopCheck("数字键", value => _plan.Options.RecordNumberKeys = value);
        _letterKeysBox = PopCheck("字母与符号", value => _plan.Options.RecordLetterKeys = value);
        _controlKeysBox = PopCheck("控制键", value =>
        {
            _plan.Options.RecordControlKeys = value;
            _preserveKeysBox.IsEnabled = value;
        });
        _preserveKeysBox = PopCheck("保留键盘快捷键", value => _plan.Options.PreserveShortcuts = value);

        return PopStack(_numberKeysBox, _letterKeysBox, _controlKeysBox, _preserveKeysBox);
    }

    private FrameworkElement BuildFollowOptions()
    {
        _smoothBox = PopCheck("平滑移动", value =>
        {
            _plan.FollowOptions = _plan.FollowOptions with { SmoothMovement = value };
        });
        _jitterBox = PopCheck("减少抖动", value =>
        {
            _plan.FollowOptions = _plan.FollowOptions with { ReduceJitter = value };
        });

        return PopStack(_smoothBox, _jitterBox);
    }

    private FrameworkElement BuildPostOptions()
    {
        _progressBarRadio = PopRadio("显示进度条", value =>
        {
            if (value) _plan.Options.PostProcessingProgressBar = true;
        });
        _playbackTimeRadio = PopRadio("显示播放时间", value =>
        {
            if (value) _plan.Options.PostProcessingProgressBar = false;
        });

        return PopStack(_progressBarRadio, _playbackTimeRadio);
    }

    private StackPanel PopStack(params UIElement[] items)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var item in items)
        {
            if (item is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(item);
        }

        if (stack.Children.Count > 0 && stack.Children[^1] is FrameworkElement last)
            last.Margin = new Thickness(0);

        return stack;
    }

    private CheckBox PopCheck(string text, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = text,
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = false,
        };

        box.Checked += (_, _) => { if (!_syncing) set(true); };
        box.Unchecked += (_, _) => { if (!_syncing) set(false); };
        return box;
    }

    private RadioButton PopRadio(string text, Action<bool> set)
    {
        var radio = new RadioButton
        {
            Content = text,
            GroupName = "recPostEffect",
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = false,
        };

        radio.Checked += (_, _) => { if (!_syncing) set(true); };
        return radio;
    }

    /// <summary>弹层的窗口句柄一到手就补上「不抢焦点 / 不入镜」（WPF 自己建的顶层窗口）。</summary>
    private void HookPopupWindow(Popup popup, bool noActivate)
    {
        if (popup.Child is not Visual child) return;

        var source = PresentationSource.FromVisual(child) as HwndSource;
        if (source == null || source.Handle == IntPtr.Zero) return;

        try
        {
            if (noActivate) OverlayShell.MakeNonActivating(source.Handle, excludeFromCapture: true);
            else Win32.SetWindowDisplayAffinity(source.Handle, Win32.WdaExcludeFromCapture);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "设置弹层窗口属性失败：" + ex.Message);
        }
    }

    /// <summary>一个悬停弹层：鼠标进按钮就开（可配延迟），离开按钮和弹层后关。</summary>
    private sealed class HoverPopover
    {
        private readonly ToggleButton _anchor;
        private readonly Border _card;
        private readonly Popup _popup;
        private readonly DispatcherTimer _leave;
        private readonly DispatcherTimer? _open;
        private readonly Action _refresh;
        private readonly StackPanel _content;

        public HoverPopover(ToggleButton anchor, FrameworkElement content, Action refresh,
            Action<Popup, bool> hook, int openDelayMs = 0, int closeDelayMs = 120)
        {
            _anchor = anchor;
            _refresh = refresh;
            _content = content as StackPanel ?? new StackPanel();
            _card = new Border
            {
                Style = (Style)anchor.FindResource("PopCard"),
                Child = content,
            };

            _popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Child = _card,
            };
            _popup.Opened += (_, _) => hook(_popup, true);

            _leave = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(closeDelayMs),
            };
            _leave.Tick += (_, _) =>
            {
                if (_anchor.IsMouseOver || _card.IsMouseOver) return;
                Hide();
            };

            if (openDelayMs > 0)
            {
                _open = new DispatcherTimer(DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(openDelayMs),
                };
                _open.Tick += (_, _) =>
                {
                    // 真离开会在 MouseLeave 里停掉这个计时器，这里不必再问 IsMouseOver
                    // （冒烟里是合成 MouseEnter，IsMouseOver 永远是 false，问了就永远开不了）
                    _open.Stop();
                    Show();
                };
            }

            anchor.MouseEnter += (_, _) =>
            {
                _leave.Stop();
                if (_open == null) Show();
                else
                {
                    _open.Stop();
                    _open.Start();
                }
            };
            anchor.MouseLeave += (_, _) =>
            {
                _open?.Stop();
                _leave.Start();
            };
            _card.MouseEnter += (_, _) => _leave.Stop();
            _card.MouseLeave += (_, _) => _leave.Start();
        }

        public bool IsOpen => _popup.IsOpen;

        public void Show()
        {
            if (!_anchor.IsEnabled) return;
            _open?.Stop();
            _refresh();
            _popup.IsOpen = true;
        }

        public void Hide()
        {
            _open?.Stop();
            _leave.Stop();
            _popup.IsOpen = false;
        }

        /// <summary>内容高度变了（勾选项禁用后行不动，这里只留给将来用）。</summary>
        public void Invalidate() => _content.InvalidateMeasure();
    }

    // ============ 取色器 ============

    /// <summary>
    /// 打开取色弹层：一排预设 + 颜色 / 不透明度两个输入框，改一下就写回（上游的
    /// `AdColorPicker`：预设、Hex 输入、透明度）。
    /// </summary>
    private void OpenColorPicker(FrameworkElement anchor, string title, Func<string> get,
        Action<string> set, string[] presets)
    {
        CloseColorPicker();

        string current = RecordOptions.NormalizeColor(get(), "#00000000");
        int currentAlpha = RecordOptions.AlphaOf(current);
        string currentRgb = current.Length == 9 ? current[3..] : "000000";

        var swatches = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var hexBox = new TextBox
        {
            Text = currentRgb,
            Width = 84,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var alphaBox = new TextBox
        {
            Text = currentAlpha.ToString(),
            Width = 52,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var colorLabel = new TextBlock { Text = "颜色", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var alphaLabel = new TextBlock { Text = "不透明度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
        Grid.SetColumn(hexBox, 1);
        Grid.SetColumn(alphaLabel, 2);
        Grid.SetColumn(alphaBox, 3);
        row.Children.Add(colorLabel);
        row.Children.Add(hexBox);
        row.Children.Add(alphaLabel);
        row.Children.Add(alphaBox);

        var hint = new TextBlock
        {
            Text = "颜色写 RRGGBB；不透明度 0 = 透明",
            FontSize = 11,
            Foreground = (Brush)FindResource("CaptureTextSecondary"),
        };

        var stack = new StackPanel { Width = 216 };
        stack.Children.Add(swatches);
        stack.Children.Add(row);
        stack.Children.Add(hint);

        var card = new Border
        {
            Style = (Style)FindResource("PopCard"),
            Child = stack,
            ToolTip = title,
        };

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = card,
        };
        popup.Opened += (_, _) =>
        {
            HookPopupWindow(popup, noActivate: false);
            hexBox.Focus();
            hexBox.SelectAll();
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_pickerPopup, popup)) _pickerPopup = null; };

        void Apply()
        {
            string text = (hexBox.Text ?? "").Trim().TrimStart('#');
            if (text.Length == 8) text = text[2..];      // 允许直接贴 AARRGGBB
            if (text.Length != 6 || !IsHex(text)) return;

            int alpha = currentAlpha;
            if (!int.TryParse((alphaBox.Text ?? "").Trim(), out alpha)) alpha = currentAlpha;
            alpha = Math.Clamp(alpha, 0, 255);

            string value = $"#{alpha:X2}{text.ToUpperInvariant()}";
            set(value);
            SyncUi();
        }

        foreach (string hex in presets)
        {
            string value = hex;
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = BrushOf(value),
                Tag = RecordOptions.AlphaOf(value) == 0 ? "none" : null,
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = DescribeColor(value),
            };
            swatch.Click += (_, _) =>
            {
                string normalized = RecordOptions.NormalizeColor(value, "#00000000");
                hexBox.Text = normalized[3..];
                alphaBox.Text = RecordOptions.AlphaOf(normalized).ToString();
                Apply();
            };
            swatches.Children.Add(swatch);
        }

        hexBox.TextChanged += (_, _) => Apply();
        alphaBox.TextChanged += (_, _) => Apply();
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { popup.IsOpen = false; e.Handled = true; }
        };
        alphaBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { popup.IsOpen = false; e.Handled = true; }
        };

        _pickerPopup = popup;
        popup.IsOpen = true;
    }

    private void CloseColorPicker()
    {
        var popup = _pickerPopup;
        _pickerPopup = null;
        if (popup != null) popup.IsOpen = false;
    }

    private static bool IsHex(string text)
    {
        foreach (char ch in text)
            if (!Uri.IsHexDigit(ch)) return false;

        return text.Length > 0;
    }
}
