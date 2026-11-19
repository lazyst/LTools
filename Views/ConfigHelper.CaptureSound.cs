using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro」里的两个快门音开关。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>screenshot.shutter-sound-notification</c>（直接截前台窗口 / 当前显示器，默认开）
/// 与 <c>screenshot.screenshot-sound-notification</c>（覆盖层开始截图，默认关）两项设置。
/// 读写与缓存都在 <see cref="CaptureSoundSettings"/>，播放与音频路径在 <see cref="ShutterSound"/>，
/// 这里只负责把控件和它接起来（与 <c>Views/ConfigHelper.Pointer.cs</c> 同一套写法）。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把两个勾选框与音频可用状态刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateCaptureSound()
    {
        CaptureShutterSoundBox.IsChecked = CaptureSoundSettings.Shutter;
        CaptureScreenshotSoundBox.IsChecked = CaptureSoundSettings.Screenshot;
        CaptureSoundHint.Text = ShutterSound.Describe() + "。播放失败只记一条日志，不挡截图。";
    }

    private void CaptureSound_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        // 两个勾选框共用一个处理器：按发事件的控件分开写。
        if (ReferenceEquals(sender, CaptureShutterSoundBox))
            CaptureSoundSettings.SetShutter(CaptureShutterSoundBox.IsChecked == true);
        else if (ReferenceEquals(sender, CaptureScreenshotSoundBox))
            CaptureSoundSettings.SetScreenshot(CaptureScreenshotSoundBox.IsChecked == true);
    }
}
