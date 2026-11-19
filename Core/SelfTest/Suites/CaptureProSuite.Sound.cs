using System;
using System.IO;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 相机快门音（P7-g，照上游 <c>presentation/capture/camerashuttersound.cpp</c>）：
/// 音频路径、MCI 命令串、别名递增、两个开关的默认值，以及「文件不在也不挡截图」。
/// </summary>
/// <remarks>
/// 纯逻辑 + 文件存在性检查，**不真的播放**（自检跑一次就响一声会吵）；
/// 真播放与设置落盘由仓库外 <c>capture-overlay-smoke</c> 验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ShutterSoundCases()
    {
        // ============ 随包音频 ============
        Check.Equal(@"Assets\Audio\camera_shutter.mp3", ShutterSound.RelativePath, "音频在输出目录里的相对路径");
        Check.True(ShutterSound.FilePath.EndsWith(@"Assets\Audio\camera_shutter.mp3", StringComparison.Ordinal),
            "FilePath 指向 Assets\\Audio\\camera_shutter.mp3");
        Check.True(File.Exists(ShutterSound.FilePath), "构建后随包快门音真的被复制出来了");
        Check.True(ShutterSound.Available, "Available 与文件存在一致");
        Check.Contains(ShutterSound.Describe(), "就绪", "Describe 报可用状态");
        Check.Equal(5000, ShutterSound.ReleaseMs, "播放后 5 秒 close（照上游 QTimer::singleShot）");

        // ============ MCI 命令串 ============
        string alias = ShutterSound.AliasOf(1);
        Check.Equal("ltools_camera_shutter_1", alias, "别名带序号");
        Check.NotEqual(ShutterSound.AliasOf(1), ShutterSound.AliasOf(2), "每次播放一个独立别名");
        Check.Equal("open \"C:\\a b\\c.mp3\" type mpegvideo alias ltools_camera_shutter_1",
            ShutterSound.OpenCommand(@"C:\a b\c.mp3", alias), "open 命令带引号 + mpegvideo");
        Check.Equal("play ltools_camera_shutter_1 from 0", ShutterSound.PlayCommand(alias),
            "play 从 0 开始（重复播不接尾巴）");
        Check.Equal("close ltools_camera_shutter_1", ShutterSound.CloseCommand(alias), "close 命令");

        // ============ 缺文件只返回 false，不抛 ============
        string missing = Path.Combine(Path.GetTempPath(), "ltools-no-such-shutter-" + Guid.NewGuid().ToString("N") + ".mp3");
        bool played;
        try
        {
            played = ShutterSound.PlayFile(missing);
        }
        catch (Exception ex)
        {
            Check.Fail("缺文件时 PlayFile 不抛", ex.GetType().Name + "：" + ex.Message);
            return;
        }

        Check.False(played, "缺文件时 PlayFile 返回 false");

        // ============ MCI 入口点本身可用 ============
        // close 一个不存在的别名不会发声，但能证明 mciSendStringW 真的被解析到了
        // （P7-g 踩过：DllImport 少写 EntryPoint，.NET 去找 MciSendString 直接 EntryPointNotFound）。
        int closeProbe = Native.Win32.MciSendString("close ltools_selftest_probe", null, 0, IntPtr.Zero);
        Check.NotEqual(0, closeProbe, "mciSendStringW 入口点可用（close 不存在的别名返回错误码）");

        // ============ 两个开关的默认值（照上游 configurationschema）============
        Check.True(CaptureSoundSettings.DefaultShutter, "直接截图默认响（shutter_sound_notification = true）");
        Check.False(CaptureSoundSettings.DefaultScreenshot, "覆盖层默认不响（screenshot_sound_notification = false）");

        var defaults = new ScreenshotFile();
        Check.Equal(true, defaults.ShutterSound ?? true, "ScreenshotFile 默认 ShutterSound = true");
        Check.Equal(false, defaults.ScreenshotSound ?? false, "ScreenshotFile 默认 ScreenshotSound = false");
    }
}
