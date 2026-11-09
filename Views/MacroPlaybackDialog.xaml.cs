using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 单个宏的回放设置（「键鼠宏」页每行的「回放设置…」）。
/// </summary>
/// <remarks>
/// 三项都是「留空 / 跟随默认」= 用「回放默认值」里的全局值，所以这里不复制全局值进来，
/// 只在提示里写清当前默认是多少——改了全局默认，所有没单独设过的宏立刻跟着变。
/// 点「确定」时直接写 <see cref="MacroStore.SavePlaybackSettings"/>，由调用方刷新列表。
/// </remarks>
public partial class MacroPlaybackDialog : Window
{
    private readonly string _macroId;
    private readonly string _name;

    public MacroPlaybackDialog(string macroId, string name, MacroMeta? meta)
    {
        InitializeComponent();
        _macroId = macroId;
        _name = name;

        Title = "宏回放设置 · " + name;

        // 全局默认写在提示里：用户能看到「留空」到底等于多少
        CountdownHint.Text = string.Create(CultureInfo.CurrentCulture,
            $"毫秒，0 = 立即；留空跟随默认（{MacroSettings.PlaybackCountdownMs} 毫秒）");
        SpeedHint.Text = string.Create(CultureInfo.CurrentCulture,
            $"0.1–5，2 = 两倍速；留空跟随默认（{MacroSettings.DefaultSpeed:0.##}×）");
        LoopsHint.Text = string.Create(CultureInfo.CurrentCulture,
            $"跟随默认 = {MacroSettings.PlayLoops} 次；无限 = 一直重放，按停止热键停");

        CountdownBox.Text = meta?.CountdownMs is int c ? c.ToString(CultureInfo.CurrentCulture) : "";
        SpeedBox.Text = meta is { Speed: > 0 } ? meta.Speed.ToString("0.##", CultureInfo.CurrentCulture) : "";

        // Loops：null / 0 = 跟随默认，-1 = 无限，1–99 = 次数
        if (meta?.Loops is not int loops || loops == 0)
        {
            LoopsModeBox.SelectedIndex = 0;
        }
        else if (loops < 0)
        {
            LoopsModeBox.SelectedIndex = 1;
        }
        else
        {
            LoopsModeBox.SelectedIndex = 2;
            LoopsBox.Text = loops.ToString(CultureInfo.CurrentCulture);
        }
        UpdateLoopsVisibility();

        Loaded += (_, _) => CountdownBox.Focus();
    }

    /// <summary>确定后的覆盖值（取消时为 null）。</summary>
    internal MacroStore.MacroPlaybackOverride? Result { get; private set; }

    private void LoopsMode_Changed(object sender, SelectionChangedEventArgs e) => UpdateLoopsVisibility();

    private void UpdateLoopsVisibility()
    {
        if (LoopsBox == null) return;   // InitializeComponent 期间 SelectedIndex 会先触发一次
        bool custom = SelectedLoopMode() == "count";
        LoopsBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom && LoopsBox.Text.Length == 0) LoopsBox.Text = "2";
    }

    private string? SelectedLoopMode() =>
        (LoopsModeBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 倒计时：留空 = 跟随默认；否则 0–10000 毫秒
        int? countdown = null;
        string countdownText = CountdownBox.Text.Trim();
        if (countdownText.Length > 0)
        {
            if (!int.TryParse(countdownText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int v)
                || v < 0 || v > 10_000)
            {
                ShowError("倒计时要填 0–10000 之间的整数（毫秒），或者留空跟随默认。");
                CountdownBox.Focus();
                return;
            }
            countdown = v;
        }

        // 速度：留空 = 跟随默认；否则 0.1–5
        double? speed = null;
        string speedText = SpeedBox.Text.Trim();
        if (speedText.Length > 0)
        {
            if (!double.TryParse(speedText, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
                || v < 0.1 || v > 5.0)
            {
                ShowError("速度倍率要填 0.1–5 之间的数，或者留空跟随默认。");
                SpeedBox.Focus();
                return;
            }
            speed = v;
        }

        // 循环：跟随默认 / 无限 / 指定次数
        int? loops = SelectedLoopMode() switch
        {
            "infinite" => -1,
            "count" => ParseLoops(),
            _ => null,
        };
        if (SelectedLoopMode() == "count" && loops == null)
        {
            ShowError("循环次数要填 1–99 之间的整数。");
            LoopsBox.Focus();
            return;
        }

        var overrides = new MacroStore.MacroPlaybackOverride(speed, loops, countdown);
        try
        {
            MacroStore.SavePlaybackSettings(_macroId, _name, overrides);
            Result = overrides;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError("保存失败：" + ex.Message);
        }
    }

    private int? ParseLoops()
    {
        string text = LoopsBox.Text.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int v)) return null;
        return v is >= 1 and <= 99 ? v : null;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        CountdownBox.Text = "";
        SpeedBox.Text = "";
        LoopsModeBox.SelectedIndex = 0;
        UpdateLoopsVisibility();
        HideError();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
