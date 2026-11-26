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

/// <summary>ConfigHelperWindow 的「运行提示 / 双击托盘图标」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 运行提示（通用页） ============

    /// <summary>把配置里的提示策略回填到控件（回填期间不触发保存）。只读 settings 域，别整份读配置拖慢切页。</summary>
    private void PopulateRunNotice()
    {
        var cfg = SettingsStore.Read(ConfigPath);

        _loadingRunNotice = true;
        RunNoticeCombo.SelectedIndex = cfg.RunNotice switch
        {
            RunNoticeMode.off => 0,
            RunNoticeMode.always => 2,
            _ => 1,
        };
        ToastPositionCombo.SelectedIndex = cfg.ToastPosition switch
        {
            ToastPosition.bottomLeft => 1,
            ToastPosition.bottomCenter => 2,
            ToastPosition.topRight => 3,
            ToastPosition.topLeft => 4,
            ToastPosition.topCenter => 5,
            _ => 0,
        };
        _loadingRunNotice = false;
    }

    private void RunNotice_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRunNotice) return;

        SettingsStore.Modify(ConfigPath, s =>
        {
            s.RunNotice = RunNoticeCombo.SelectedIndex switch
            {
                0 => RunNoticeMode.off,
                2 => RunNoticeMode.always,
                _ => RunNoticeMode.onFailure,
            };
            s.ToastPosition = ToastPositionCombo.SelectedIndex switch
            {
                1 => ToastPosition.bottomLeft,
                2 => ToastPosition.bottomCenter,
                3 => ToastPosition.topRight,
                4 => ToastPosition.topLeft,
                5 => ToastPosition.topCenter,
                _ => ToastPosition.bottomRight,
            };
        });
    }

    // ============ 双击托盘图标（通用页） ============

    /// <summary>
    /// 回填「左键双击」下拉。<see cref="TrayDoubleClickAction"/> 显式赋值 0–5，与 XAML 里 6 个
    /// ComboBoxItem 一一对应，故 SelectedIndex 就是枚举值；读 <see cref="AppState.TrayDoubleClick"/>
    /// （双击回调读的同一份运行时缓存），不读整份配置。
    /// </summary>
    private void PopulateTrayDoubleClick()
    {
        _loadingTrayDoubleClick = true;
        int i = (int)AppState.TrayDoubleClick;
        TrayDoubleClickCombo.SelectedIndex =
            i >= 0 && i < TrayDoubleClickCombo.Items.Count ? i : 0;   // 越界/未知值兜底成「打开设置」
        _loadingTrayDoubleClick = false;
    }

    private void TrayDoubleClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTrayDoubleClick || _initializing) return;
        int i = TrayDoubleClickCombo.SelectedIndex;
        if (i < 0 || i >= TrayDoubleClickCombo.Items.Count) return;
        // 立即生效（同步 AppState）+ 只写 configs/settings.json，双击不用读盘
        Settings.SetTrayDoubleClick((TrayDoubleClickAction)i);
    }

}
