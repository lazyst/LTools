using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Config;
using LTools.Features.Builtin;

namespace LTools.Views;

/// <summary>
/// 编辑一条自动触发规则（见 <c>docs/AUTO_TRIGGER.md</c> §9）。
/// 一期只暴露定时 / 启动后相关字段；事件触发、完整 Cron、Quicker 那几个启动选项、
/// 动作参数 / 命中后不再判断后续规则都属待决项，未定前不进界面（字段在模型里已留全）。
/// </summary>
public partial class TriggerRuleEditDialog : Window
{
    private readonly TriggerRule _rule;
    private string _actionRef = "";

    private TriggerRuleEditDialog(TriggerRule rule)
    {
        InitializeComponent();
        _rule = rule;
        _actionRef = rule.ActionRef;

        NoteBox.Text = rule.Note;
        EnabledBox.IsChecked = rule.Enabled;

        SelectKind(rule.Kind);
        // 间隔按分钟展示（支持小数，避免「打开→不改→确定」把非整分钟的值静默改掉）。
        IntervalBox.Text = rule.IntervalMs is > 0
            ? (rule.IntervalMs.Value / 60000.0).ToString(CultureInfo.InvariantCulture)
            : "";
        DailyBox.Text = rule.DailyAt ?? "";
        DelaySecondsBox.Text = rule.DelaySeconds.ToString(CultureInfo.InvariantCulture);
        OnlyFirstStartBox.IsChecked = rule.OnlyFirstStartToday;
        OnlyOnAutoStartBox.IsChecked = rule.OnlyOnAutoStart;

        ActionBox.Text = ActionName(_actionRef);

        var filter = rule.Scope ?? new AppFilter();
        ProcessBox.Text = string.Join(Environment.NewLine, filter.Processes);
        TitleBox.Text = string.Join(Environment.NewLine, filter.Titles);
        ClassBox.Text = string.Join(Environment.NewLine, filter.Classes);
        ScopeExpander.IsExpanded = filter is { IsEmpty: false };

        DelayMsBox.Text = rule.DelayMs.ToString(CultureInfo.InvariantCulture);
        DebounceBox.Text = rule.DebounceMs.ToString(CultureInfo.InvariantCulture);
        ThrottleBox.Text = rule.ThrottleMs.ToString(CultureInfo.InvariantCulture);
        AdvExpander.IsExpanded = rule.DelayMs > 0 || rule.DebounceMs > 0 || rule.ThrottleMs > 0;

        ApplyKindVisibility();
    }

    /// <summary>录入结果（仅 <see cref="Window.DialogResult"/> 为 true 时有效）。</summary>
    public TriggerRule Result { get; private set; } = new();

    /// <summary>编辑一条规则（会被克隆，取消时不影响原对象）。</summary>
    public static TriggerRule? Show(Window? owner, TriggerRule rule)
    {
        var dlg = new TriggerRuleEditDialog(rule);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ============ 触发方式 ============

    private void SelectKind(TriggerKind kind)
    {
        string tag = kind switch
        {
            TriggerKind.startup => "startup",
            TriggerKind.@event => "event",
            _ => "timer",
        };
        foreach (ComboBoxItem item in KindBox.Items)
            if ((item.Tag as string) == tag) { KindBox.SelectedItem = item; return; }
        KindBox.SelectedIndex = 0;
    }

    private string SelectedKind() => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "timer";

    private void Kind_Changed(object sender, SelectionChangedEventArgs e) => ApplyKindVisibility();

    private void ApplyKindVisibility()
    {
        string tag = SelectedKind();
        TimerPanel.Visibility = tag == "timer" ? Visibility.Visible : Visibility.Collapsed;
        StartupPanel.Visibility = tag == "startup" ? Visibility.Visible : Visibility.Collapsed;
        EventPanel.Visibility = tag == "event" ? Visibility.Visible : Visibility.Collapsed;
    }

    private TriggerKind KindFromTag() => SelectedKind() switch
    {
        "startup" => TriggerKind.startup,
        "event" => TriggerKind.@event,
        _ => TriggerKind.timer,
    };

    // ============ 动作 ============

    private void PickAction_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ActionPoolPicker(this, "选择动作 / 功能", _actionRef);
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.Result)) return;
        _actionRef = picker.Result!;
        ActionBox.Text = ActionName(_actionRef);
    }

    private static string ActionName(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        if (!BindingRef.Exists(reference)) return $"（{reference}）已不存在";
        return BindingRef.Describe(reference);
    }

    // ============ 保存 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string tag = SelectedKind();
        if (tag == "event")
        {
            ShowError("事件触发将在二期开放，一期请选「定时」或「启动后」。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_actionRef) || !BindingRef.Exists(_actionRef))
        {
            ShowError("请选择一个存在的动作或功能。");
            return;
        }

        int? intervalMs = null;
        string? dailyAt = null;
        int delaySeconds = 0;
        // 两个启动选项只对「启动后」有意义：切到别的触发方式时不把残留勾选带进配置。
        bool onlyFirstStart = tag == "startup" && OnlyFirstStartBox.IsChecked == true;
        bool onlyOnAutoStart = tag == "startup" && OnlyOnAutoStartBox.IsChecked == true;

        if (tag == "timer")
        {
            // 间隔与每天时刻二选一：填了间隔优先，否则用每天时刻。
            string intervalText = IntervalBox.Text.Trim();
            string dailyText = DailyBox.Text.Trim();

            if (intervalText.Length > 0)
            {
                if (!double.TryParse(intervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes)
                    || minutes <= 0 || minutes * 60000 > int.MaxValue)
                {
                    ShowError("间隔请填大于 0 的分钟数（可带小数）。");
                    return;
                }
                intervalMs = (int)Math.Round(minutes * 60000);
            }
            else if (dailyText.Length > 0)
            {
                if (!TryDaily(dailyText, out int _, out int _))
                {
                    ShowError("每天时刻请填 HH:mm（如 09:00），小时 0–23、分钟 0–59。");
                    return;
                }
                dailyAt = dailyText;
            }
            else
            {
                ShowError("定时规则至少要填「间隔分钟」或「每天时刻」之一。");
                return;
            }
        }
        else   // startup
        {
            if (!int.TryParse(DelaySecondsBox.Text.Trim(), out delaySeconds) || delaySeconds < 0)
            {
                ShowError("延迟请填 0 或正整数秒数。");
                return;
            }
        }

        if (!int.TryParse(DelayMsBox.Text.Trim(), out int delayMs) || delayMs < 0
            || !int.TryParse(DebounceBox.Text.Trim(), out int debounceMs) || debounceMs < 0
            || !int.TryParse(ThrottleBox.Text.Trim(), out int throttleMs) || throttleMs < 0)
        {
            ShowError("高级参数请填 0 或正整数（毫秒）。");
            return;
        }

        var scope = new AppFilter
        {
            Processes = Lines(ProcessBox),
            Titles = Lines(TitleBox),
            Classes = Lines(ClassBox),
        };

        Result = new TriggerRule
        {
            Id = _rule.Id,
            Note = NoteBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            Kind = KindFromTag(),
            IntervalMs = intervalMs,
            DailyAt = dailyAt,
            DelaySeconds = delaySeconds,
            OnlyFirstStartToday = onlyFirstStart,
            OnlyOnAutoStart = onlyOnAutoStart,
            ActionRef = _actionRef,
            Scope = scope.IsEmpty ? null : scope,
            DelayMs = delayMs,
            DebounceMs = debounceMs,
            ThrottleMs = throttleMs,
            LastEditTimeUtc = DateTime.UtcNow.Ticks,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static List<string> Lines(TextBox box) => box.Text
        .Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();

    /// <summary>解析 <c>HH:mm</c>（与 TriggerScheduler.TryDaily 同规则）。</summary>
    private static bool TryDaily(string text, out int hh, out int mm)
    {
        hh = mm = 0;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hh) || !int.TryParse(parts[1], out mm)) return false;
        return hh is >= 0 and <= 23 && mm is >= 0 and <= 59;
    }
}
