using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 相机快门音（P7-g，照上游 <c>presentation/capture/camerashuttersound.cpp</c>）：
/// 两个默认值 / 随包音频 / 真发 MCI 播放 / 缺文件不抛 / 设置页两个勾选框真落 settings.json。
/// </summary>
/// <remarks>
/// 配置根指向冒烟自己的临时目录（不碰仓库里真正的 <c>configs/settings.json</c>），
/// 段尾搬回原来的 <c>ConfigStore.ConfigPath</c> 并丢掉缓存。
/// </remarks>
internal static partial class Program
{
    private static void SoundSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var soundType = asm.GetType("LTools.Features.CapturePro.CaptureSoundSettings")!;
        var shutterType = asm.GetType("LTools.Features.CapturePro.ShutterSound")!;

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "sound-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });

        void Invalidate() => soundType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
        bool Shutter() => (bool)soundType.GetProperty("Shutter", Stat)!.GetValue(null)!;
        bool Screenshot() => (bool)soundType.GetProperty("Screenshot", Stat)!.GetValue(null)!;
        string SettingsText() => File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";

        Window? window = null;
        try
        {
            // ============ 默认值（照上游 configurationschema）============
            Invalidate();
            Check(Shutter(), "空配置下直接截图默认响（shutter_sound_notification = true）");
            Check(!Screenshot(), "空配置下覆盖层默认不响（screenshot_sound_notification = false）");
            Check(!File.Exists(settingsFile), "只读缓存不会平白落一份 settings.json");

            // ============ 随包音频与真播放 ============
            string filePath = (string)shutterType.GetProperty("FilePath", Stat)!.GetValue(null)!;
            Check(File.Exists(filePath), "随包快门音在输出目录里：" + filePath);
            bool played = (bool)shutterType.GetMethod("Play", Stat)!.Invoke(null, null)!;
            Check(played, "ShutterSound.Play() 真的发出了 MCI 播放命令");

            bool missing = (bool)shutterType.GetMethod("PlayFile", Stat)!
                .Invoke(null, new object[] { Path.Combine(_outDir, "no-such-shutter.mp3") })!;
            Check(!missing, "缺文件时 PlayFile 只返回 false（不抛、不挡截图）");

            // ============ 设置页：两个勾选框真落盘 ============
            window = new LTools.Views.ConfigHelperWindow(configPath);
            window.Show();
            Pump(window);
            Pump(window);

            // Loaded 里那个异步自启查询会把 _initializing 保持为 true，直接勾选会被吞；
            // 这里把它和真实「查询结束」后一样置回 false，再走用户的那条点击路径。
            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(window, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateCaptureSound", Inst)!
                .Invoke(window, null);

            var shutterBox = window.FindName("CaptureShutterSoundBox") as CheckBox;
            var screenshotBox = window.FindName("CaptureScreenshotSoundBox") as CheckBox;
            Check(shutterBox != null && shutterBox.IsChecked == true, "设置页「直接截图响」默认勾上");
            Check(screenshotBox != null && screenshotBox.IsChecked == false, "设置页「框选开始也响」默认不勾");

            shutterBox!.IsChecked = false;
            screenshotBox!.IsChecked = true;
            Pump(window);

            string text = SettingsText();
            Check(text.Contains("\"ShutterSound\": false"), "取消勾选落 settings.json：ShutterSound = false");
            Check(text.Contains("\"ScreenshotSound\": true"), "勾上后落 settings.json：ScreenshotSound = true");

            Invalidate();
            Check(!Shutter(), "重新读盘拿到 ShutterSound = false");
            Check(Screenshot(), "重新读盘拿到 ScreenshotSound = true");

            var hint = window.FindName("CaptureSoundHint") as TextBlock;
            Check(hint != null && hint.Text.Contains("camera_shutter.mp3"),
                "设置页说明写明音频文件：" + hint?.Text);

            window.Close();
            window = null;
        }
        finally
        {
            if (window != null)
            {
                try { window.Close(); } catch { /* 已经关了 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
        }
    }
}
