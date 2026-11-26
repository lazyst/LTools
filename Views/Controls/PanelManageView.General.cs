using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>PanelManageView 的「常规」页部分。</summary>
public partial class PanelManageView : UserControl
{
    // ============ 常规 ============

    private void EnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyEnabledState();
        if (_loading) return;
        Settings.SetSuperPanelEnabled(EnabledBox.IsChecked == true);
    }

    /// <summary>面板关掉时长按阈值没有意义，整行置灰（点不动，但仍能看到当前值）。</summary>
    private void ApplyEnabledState()
        => ThresholdRow.IsEnabled = EnabledBox.IsChecked == true;

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText == null) return;   // XAML 设置 Minimum/Maximum 时控件还没建好
        ThresholdText.Text = $"{(int)ThresholdSlider.Value} ms";
        if (_loading) return;
        AppState.SuperPanelThresholdMs = (int)ThresholdSlider.Value;
        _thresholdTimer.Stop();
        _thresholdTimer.Start();
    }

    private void SaveThreshold()
    {
        try
        {
            int v = (int)ThresholdSlider.Value;
            PanelStore.Modify(ConfigStore.ConfigPath, p => p.SuperPanel.LongPressThresholdMs = v);
            SetStatus($"长按阈值已保存：{v} ms", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("保存长按阈值失败：" + ex.Message, ok: false);
        }
    }

    private void Prefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prefs = PanelLibrary.Current.Preferences;
        prefs.ShowGroupTitles = ShowGroupTitlesBox.IsChecked == true;
        prefs.CompactTiles = CompactTilesBox.IsChecked == true;
        PanelLibrary.Save();
    }

    /// <summary>悬浮偏好：立即写盘（悬浮窗在执行器里，它收到 config.changed 后自己重建）。</summary>
    private void FloatPrefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prefs = FloatingLibrary.Prefs;
        prefs.MiddleClickClose = FloatMiddleCloseBox.IsChecked == true;
        prefs.ShowTooltip = FloatTooltipBox.IsChecked == true;
        prefs.SnapToEdges = FloatSnapBox.IsChecked == true;
        FloatingLibrary.SavePreferences();
    }

    private void FloatCloseAll_Click(object sender, RoutedEventArgs e)
    {
        // 悬浮窗在提权进程里：转过去关（宿主里没有悬浮库）。连不上时桥自己写日志，别假装关掉了。
        if (PanelHostBridge.TryCloseAllFloating()) return;
        FloatingManager.CloseAll();
    }

    private void ResetHint_Click(object sender, RoutedEventArgs e)
    {
        PanelLibrary.Current.HintDismissed = false;
        PanelLibrary.Save();
        SetStatus("已恢复「尚未添加专用设置」提示", ok: true);
    }

}
