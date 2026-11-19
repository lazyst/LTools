using System.Windows;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro」里的「颜色预设…」入口与摘要。
/// </summary>
/// <remarks>
/// 照上游设置的三项颜色预设（描边 / 填充 / 荧光各 5 个），读写与默认值都在
/// <see cref="ColorPresets"/>，编辑器在 <see cref="CaptureColorPresetsWindow"/>。
/// 这里只负责入口与「现在用的是什么」那一行摘要。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把摘要素刷成当前三族预设的首色（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateCaptureColorPresets()
    {
        CaptureColorPresetsHint.Text =
            $"描边 {ColorPresets.GetHex(ColorPresetFamily.Stroke)[0]} · " +
            $"填充 {ColorPresets.GetHex(ColorPresetFamily.Fill)[0]} · " +
            $"荧光 {ColorPresets.GetHex(ColorPresetFamily.Highlight)[0]}，各 5 个；改完立刻生效。";

        CaptureSyncColorsBox.IsChecked = CaptureStyleStore.Current.SyncColors;
    }

    private void CaptureColorPresets_Click(object sender, RoutedEventArgs e)
    {
        CaptureColorPresetsWindow.Show(this);
        PopulateCaptureColorPresets();
    }

    /// <summary>
    /// 上游 <c>drawing/sync_tool_colors</c>：打开后改一处描边色 = 所有工具一起改。
    /// 开关本身记在 <c>ui.json</c> 的 <c>CaptureStyles</c>（与样式记忆同一份）。
    /// </summary>
    private void CaptureSyncColors_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        CaptureStyleStore.Current.SetSyncColors(CaptureSyncColorsBox.IsChecked == true);
        CaptureStyleStore.SaveSoon();
    }
}
