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

/// <summary>PanelManageView 的「对外入口」部分（显示哪一页 / 刷新）。</summary>
public partial class PanelManageView : UserControl
{
    // ============ 对外入口 ============

    /// <summary>重新从 <see cref="PanelLibrary.Current"/> 回填整页。</summary>
    public void Reload()
    {
        _loading = true;
        var prefs = PanelLibrary.Current.Preferences;
        EnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
        ThresholdSlider.Value = Math.Clamp(AppState.SuperPanelThresholdMs, 100, 800);
        ThresholdText.Text = $"{(int)ThresholdSlider.Value} ms";
        ApplyEnabledState();
        ShowGroupTitlesBox.IsChecked = prefs.ShowGroupTitles;
        CompactTilesBox.IsChecked = prefs.CompactTiles;
        var fp = FloatingLibrary.Prefs;
        FloatMiddleCloseBox.IsChecked = fp.MiddleClickClose;
        FloatTooltipBox.IsChecked = fp.ShowTooltip;
        FloatSnapBox.IsChecked = fp.SnapToEdges;
        _loading = false;

        if (_selectedKey != CommonKey && PanelLibrary.FindScene(_selectedKey) == null)
            _selectedKey = CommonKey;

        BuildBlocked();
        BuildSceneList();
        RenderDetail();
    }

    /// <summary>选中指定场景（面板「为此程序创建场景」跳转用）。</summary>
    public void SelectScene(string? sceneId)
    {
        if (!string.IsNullOrEmpty(sceneId))
        {
            _selectedKey = sceneId!;
            _rulesExpanded = false;   // 新选中的场景先看规则摘要
        }
        ShowPage("scenes");
        Reload();
    }

    /// <summary>切换二级页：general / scenes / blocked（主导航「面板管理」下拉调用）。</summary>
    public void ShowPage(string page)
    {
        bool general = page == "general";
        bool blocked = page == "blocked";
        PageGeneral.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PageBlocked.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        PageScenes.Visibility = general || blocked ? Visibility.Collapsed : Visibility.Visible;
        if (PageScenes.Visibility == Visibility.Visible)
        {
            BuildSceneList();
            RenderDetail();
        }
    }

    /// <summary>设置搜索用：三个子页各自的逻辑树根（与可见性无关）。</summary>
    internal IEnumerable<(string Tag, FrameworkElement Root)> SearchRoots()
    {
        yield return ("superpanel-general", PageGeneral);
        yield return ("superpanel-scenes", PageScenes);
        yield return ("superpanel-blocked", PageBlocked);
    }

}
