using System.Windows;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro」里的「全局快捷键…」入口与摘要。
/// </summary>
/// <remarks>
/// 摘要是 <see cref="CaptureShortcuts.Summary"/> 的结果（读运行期热键表，不读磁盘）；
/// 真正的编辑在 <see cref="CaptureShortcutsWindow"/>，读写的是同一份 <c>configs/hotkeys.json</c>。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把摘要刷成当前绑定（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateCaptureShortcuts()
    {
        CaptureShortcutsSummary.Text = "已绑：" + CaptureShortcuts.Summary(HotkeyRegistry.File);
    }

    private void CaptureShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CaptureShortcutsWindow.Show(this);
        PopulateCaptureShortcuts();
    }
}
