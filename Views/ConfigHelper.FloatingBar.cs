using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 截图 Pro →「常规」页里的悬浮工具条卡片（照 Snow Shot 的 <c>floating_toolbar/*</c>）。
/// </summary>
/// <remarks>
/// 改任何一项都写 <c>settings.json</c> 的 <c>FloatingToolbar</c> 段；工具条由
/// <see cref="FloatingToolbarService"/> 订阅 <see cref="FloatingToolbarStore.Changed"/> 重建，
/// 不需要重启。
/// </remarks>
public partial class ConfigHelperWindow
{
    private bool _floatingBarLoading;

    private void PopulateFloatingBar()
    {
        _floatingBarLoading = true;
        try
        {
            FloatingToolbarSettings settings = FloatingToolbarStore.Current;
            FloatingBarEnabledBox.IsChecked = settings.Enabled;
            SelectTag(FloatingBarModeCombo, settings.Mode);
            SelectTag(FloatingBarDropCombo, settings.DropBehavior);
            FloatingBarOpacityBox.Text = settings.Opacity.ToString(CultureInfo.InvariantCulture);
            FloatingBarHideFullscreenBox.IsChecked = settings.HideInFullscreen;
            FloatingBarHideCaptureBox.IsChecked = settings.HideDuringCapture;
            UpdateFloatingBarSummary();
        }
        finally { _floatingBarLoading = false; }
    }

    private void UpdateFloatingBarSummary()
    {
        FloatingToolbarLayout layout = FloatingToolbarStore.Layout;
        FloatingBarLayoutText.Text = string.Format(CultureInfo.CurrentCulture,
            "{0} 个按钮，{1} 叠；收起 {2} 个。当前布局：{3}",
            layout.VisibleCount, layout.Positions.Count, layout.Hidden.Count, layout.Describe());
    }

    private void FloatingBar_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _floatingBarLoading) return;

        FloatingToolbarStore.SetEnabled(FloatingBarEnabledBox.IsChecked == true);
        FloatingToolbarStore.SetHideInFullscreen(FloatingBarHideFullscreenBox.IsChecked == true);
        FloatingToolbarStore.SetHideDuringCapture(FloatingBarHideCaptureBox.IsChecked == true);
    }

    private void FloatingBarSelect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _floatingBarLoading) return;

        FloatingToolbarStore.SetMode(TagOf(FloatingBarModeCombo));
        FloatingToolbarStore.SetDropBehavior(TagOf(FloatingBarDropCombo));
    }

    private void FloatingBarOpacity_LostFocus(object sender, RoutedEventArgs e) => CommitFloatingBarOpacity();

    private void FloatingBarOpacity_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitFloatingBarOpacity();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitFloatingBarOpacity()
    {
        if (_initializing || _floatingBarLoading) return;

        string text = FloatingBarOpacityBox.Text.Trim();
        int value = FloatingToolbarStore.Current.Opacity;
        if (text.Length > 0 &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            value = parsed;

        value = FloatingToolbarStore.NormalizeOpacity(value);
        FloatingToolbarStore.SetOpacity(value);
        FloatingBarOpacityBox.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private void FloatingBarLayout_Click(object sender, RoutedEventArgs e)
    {
        var layout = LTools.Views.CapturePro.FloatingToolbarLayoutWindow.Show(this,
            FloatingToolbarStore.Layout);
        if (layout == null) return;

        FloatingToolbarStore.SaveLayout(layout);
        UpdateFloatingBarSummary();
    }
}
