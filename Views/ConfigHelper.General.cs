using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「通用页（视觉 / 行为）」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 通用：开关 ============

    /// <summary>把「工具开关」的状态刷到控件（调用方负责 <c>_initializing</c>）。
    /// 超级面板的开关不在这里：它归「面板管理 → 常规 → 行为」。</summary>
    private void PopulateToggles()
    {
        CapsLockModifierBox.IsChecked = AppState.CapsLockModifier;
        EdgeWheelVolumeBox.IsChecked = AppState.EdgeWheelVolume;
        EdgeWheelEdgeCombo.SelectedIndex = (WheelVolumeEdge)AppState.EdgeWheelVolumeEdge switch
        {
            WheelVolumeEdge.top => 1,
            WheelVolumeEdge.both => 2,
            _ => 0,
        };
        ApplyEdgeWheelVisibility();
        PopulateScreenshotDelay();
        PopulatePointerActions();
        PopulatePinWindow();
        PopulateCaptureSound();
        PopulateCaptureColorPresets();
        PopulateOverlayUi();
        PopulateOcr();
        PopulateDrawing();
        PopulateBehavior();
        PopulateCaptureShortcuts();
        PopulateCaptureKeymap();
        PopulateRecord();
        PopulateTranslation();
        PopulateCaptureHistory();
        PopulatePinHistory();
        PopulateFloatingBar();
        PopulateSelectionExport();
    }

    /// <summary>
    /// 「延时截图」的秒数下拉：1–10 秒，项目只在第一次填充（之后只改 SelectedIndex）。
    /// 读的是运行时缓存 <see cref="AppState.ScreenshotDelaySeconds"/>，不读磁盘。
    /// </summary>
    private void PopulateScreenshotDelay()
    {
        if (ScreenshotDelayCombo.Items.Count == 0)
        {
            for (int seconds = LTools.Features.CapturePro.CaptureDelay.MinSeconds;
                 seconds <= LTools.Features.CapturePro.CaptureDelay.MaxSeconds; seconds++)
                ScreenshotDelayCombo.Items.Add(new ComboBoxItem { Content = $"{seconds} 秒" });
        }

        int value = LTools.Features.CapturePro.CaptureDelay.Normalize(AppState.ScreenshotDelaySeconds);
        ScreenshotDelayCombo.SelectedIndex = value - LTools.Features.CapturePro.CaptureDelay.MinSeconds;
    }

    private void ScreenshotDelay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        int index = ScreenshotDelayCombo.SelectedIndex;
        if (index < 0) return;
        Settings.SetScreenshotDelay(LTools.Features.CapturePro.CaptureDelay.MinSeconds + index);
    }

    /// <summary>边缘滚轮关掉时把「算哪条边」与说明一起收起，只留一行干净的开关。</summary>
    private void ApplyEdgeWheelVisibility()
    {
        var v = EdgeWheelVolumeBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EdgeWheelEdgeRow.Visibility = v;
        EdgeWheelVolumeHint.Visibility = v;
    }

    private void CapsLockModifierBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.SetCapsLockModifier(CapsLockModifierBox.IsChecked == true);
    }

    private void EdgeWheelVolumeBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ApplyEdgeWheelVisibility();
        Settings.SetEdgeWheelVolume(EdgeWheelVolumeBox.IsChecked == true);
    }

    private void EdgeWheelEdge_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.SetEdgeWheelVolumeEdge(EdgeWheelEdgeCombo.SelectedIndex switch
        {
            1 => WheelVolumeEdge.top,
            2 => WheelVolumeEdge.both,
            _ => WheelVolumeEdge.bottom,
        });
    }

    private void AutoStartBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool want = AutoStartBox.IsChecked == true;
        AutoStartInfo? info = AppControl.SetEnabled(want, out string? error);
        if (info == null)
        {
            _initializing = true;
            AutoStartBox.IsChecked = !want;
            _initializing = false;
            ConfirmDialog.Info(this, "开机自启", error ?? (want ? "启用失败。" : "禁用失败。"));
            return;
        }
        // 重新注册会把 Command 改成当前 exe，旧的「路径已失效」提示随之消失
        ApplyAutoStartState(info, null);
    }

    /// <summary>
    /// 把自启任务状态刷到「通用」页：勾选 = 任务存在；任务指向别的路径时
    /// （多为便携目录被挪走）给出红字说明与「改为当前程序」按钮。
    /// </summary>
    /// <param name="info">null = 问不到（宿主还没连上主程序），此时只把提示写清楚，状态保持不变。</param>
    private void ApplyAutoStartState(AutoStartInfo? info, string? error)
    {
        _initializing = true;
        AutoStartBox.IsChecked = info?.TaskExists == true;
        _initializing = false;

        if (info == null)
        {
            AutoStartFixButton.Visibility = Visibility.Collapsed;
            AutoStartHint.Foreground = (Brush)FindResource("HintTextBrush");
            AutoStartHint.Text = error ?? "读不到开机自启状态。";
            return;
        }

        AutoStartInfo state = info.Value;
        bool stale = state.State == AutoStartState.Stale;
        AutoStartFixButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        AutoStartHint.Foreground = (Brush)FindResource(stale ? "DangerBrush" : "HintTextBrush");
        AutoStartHint.Text = !stale
            ? "通过任务计划程序创建 ONLOGON / 最高权限任务，开机无 UAC 弹窗。"
            : state.RegisteredPathExists
                ? $"自启任务指向另一份拷贝 {state.RegisteredPath}，登录时启动的不是这一份。点下面「改为当前程序」统一到这一份（启动时也会自动对齐）。"
                : $"自启任务指向 {state.RegisteredPath}，该文件已不存在（多为挪动了程序目录），登录时不会启动。点下面「改为当前程序」修复（启动时也会自动对齐）。";
    }

    private void AutoStartFix_Click(object sender, RoutedEventArgs e)
    {
        AutoStartInfo? info = AppControl.SetEnabled(true, out string? error);
        if (info == null)
        {
            ConfirmDialog.Info(this, "开机自启", error ?? "修复失败，请检查权限或任务计划服务。");
            return;
        }
        ApplyAutoStartState(info, null);
    }

}
