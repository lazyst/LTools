using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 组合键可视化选键对话框（交付后 UX 优化，§12）。
/// 替代原 InputDialog 裸文本框——提供修饰键复选 + 特殊键表格 + 预览实时校验，
/// 解决用户「不知道能输什么键名」的发现性问题。
/// </summary>
/// <remarks>
/// 交互规则：表格点击/勾修饰键/打字三种方式都只改预览，「确定」才入库。
/// 不含按键捕获（与「开始录制」功能重叠，会造成冗余困惑）。
/// 数据层零改动——仍存 stroke 字符串，<see cref="KeyStroke.TryParse"/> 校验。
/// </remarks>
public partial class ChordPickerDialog : Window
{
    /// <summary>确定的 stroke 字符串（仅 <see cref="DialogResult"/> 为 true 时有效）。</summary>
    public string Stroke { get; private set; } = "";

    /// <summary>末段主键的 stroke 名（可能来自表格点击或手动解析），null 表示未设。</summary>
    private string? _mainKey;

    /// <summary>同步锁：程序内改控件值时跳过事件回调，防递归。</summary>
    private bool _sync;

    public ChordPickerDialog()
    {
        InitializeComponent();
        BuildKeyButtons();
    }

    // —— 表格按钮定义：显示名 → stroke 主键名（箭头特殊映射）——
    // 显示名直接用 Content，Click 时 ToLower 作 stroke 名（"Enter"→"enter"）。
    private static readonly string[] EditKeys =
        { "Enter", "Tab", "Esc", "Space", "Back", "Del", "Ins", "Home", "End" };

    private static readonly (string Display, string Stroke)[] NavKeys =
    {
        ("PgUp", "pgup"), ("PgDn", "pgdn"),
        ("↑", "up"), ("↓", "down"), ("←", "left"), ("→", "right"),
    };

    private static readonly string[] FKeys = Enumerable.Range(1, 12)
        .Select(n => "F" + n).ToArray();

    private static readonly string[] F13Keys = Enumerable.Range(13, 12)
        .Select(n => "F" + n).ToArray();

    // OEM 符号键（显示名即 stroke 名的 ToLower）
    private static readonly string[] SymKeys =
        { "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/", "`" };

    private void BuildKeyButtons()
    {
        foreach (var k in EditKeys)
            EditRow.Children.Add(NewKeyButton(k));
        foreach (var (disp, _) in NavKeys)
            NavRow.Children.Add(NewKeyButton(disp));
        foreach (var k in FKeys)
            FRow.Children.Add(NewKeyButton(k));
        foreach (var k in F13Keys)
            F13Row.Children.Add(NewKeyButton(k));
        foreach (var k in SymKeys)
            SymRow.Children.Add(NewKeyButton(k));
    }

    private Button NewKeyButton(string display)
    {
        var b = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = display,
            MinWidth = display.Length <= 2 ? 36 : 46,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(6, 4, 6, 4),
        };
        b.Click += Key_Click;
        return b;
    }

    // —— 表格点击：取 Content 映射为 stroke 名，设为主键 → 同步预览 ——
    private void Key_Click(object sender, RoutedEventArgs e)
    {
        var name = ((sender as Button)?.Content?.ToString() ?? "").ToLowerInvariant();
        // 箭头显示符 → stroke 名
        name = name switch
        {
            "↑" => "up",
            "↓" => "down",
            "←" => "left",
            "→" => "right",
            _ => name,
        };
        _mainKey = name;
        SyncPreview();
    }

    // —— 修饰键复选变化 → 重算预览 ——
    private void Mod_Changed(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        SyncPreview();
    }

    /// <summary>根据修饰键复选 + <see cref="_mainKey"/> 重算预览文本（设 _sync 防递归）。</summary>
    private void SyncPreview()
    {
        _sync = true;
        var parts = new List<string>();
        if (CtrlBox.IsChecked == true) parts.Add("ctrl");
        if (ShiftBox.IsChecked == true) parts.Add("shift");
        if (AltBox.IsChecked == true) parts.Add("alt");
        if (WinBox.IsChecked == true) parts.Add("win");
        if (_mainKey != null)
        {
            parts.Add(_mainKey);
            PreviewBox.Text = string.Join("+", parts);
        }
        else
        {
            // 未设主键：带 "+" 占位（如 "ctrl+"），让 Validate 走「缺少主键」友好提示
            PreviewBox.Text = parts.Count > 0 ? string.Join("+", parts) + "+" : "";
        }
        _sync = false;
        Validate();
    }

    // —— 预览框手动编辑：解析成功则同步复选 + 主键，失败只报错（不动复选）——
    private void PreviewBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        _mainKey = null;
        var t = (PreviewBox.Text ?? "").Trim();

        if (t.Length == 0)
        {
            _sync = true;
            CtrlBox.IsChecked = ShiftBox.IsChecked = AltBox.IsChecked = WinBox.IsChecked = false;
            _sync = false;
            Validate();
            return;
        }

        if (KeyStroke.TryParse(t, out _, out _))
        {
            // 解析成功 → 从文本段同步复选与主键（别名归一：control→ctrl）
            _sync = true;
            var segs = t.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _mainKey = segs[^1];
            CtrlBox.IsChecked = segs.Take(segs.Length - 1).Any(m => m is "ctrl" or "control");
            ShiftBox.IsChecked = segs.Take(segs.Length - 1).Any(m => m is "shift");
            AltBox.IsChecked = segs.Take(segs.Length - 1).Any(m => m is "alt" or "menu");
            WinBox.IsChecked = segs.Take(segs.Length - 1).Any(m => m is "win" or "meta");
            _sync = false;
        }
        Validate();
    }

    /// <summary>实时校验预览：有效 → 清错误 + 启用确定；无效 → 红字 + 禁用确定。</summary>
    private void Validate()
    {
        var t = (PreviewBox.Text ?? "").Trim();

        if (t.Length == 0)
        {
            ErrText.Text = "请选择或输入按键";
            ErrText.Foreground = (Brush)FindResource("SecondaryTextBrush");
            OkBtn.IsEnabled = false;
            return;
        }

        if (KeyStroke.TryParse(t, out _, out var err))
        {
            ErrText.Text = "";
            OkBtn.IsEnabled = true;
        }
        else
        {
            // 仅修饰键无主键时友好提示
            bool modsOnly = t.EndsWith('+');
            ErrText.Text = modsOnly ? "缺少主键：点击下方按键或直接输入字母/数字" : err;
            ErrText.Foreground = (Brush)FindResource("DangerBrush");
            OkBtn.IsEnabled = false;
        }
    }

    private void F13Box_Changed(object sender, RoutedEventArgs e)
    {
        F13Row.Visibility = F13Box.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var t = (PreviewBox.Text ?? "").Trim();
        if (!KeyStroke.TryParse(t, out _, out var err))
        {
            ErrText.Text = err;
            ErrText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        Stroke = t;
        DialogResult = true;
    }
}
