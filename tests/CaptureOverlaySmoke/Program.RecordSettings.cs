using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 录屏设置（P9-g，照上游 <c>screen_recording/*</c>）：设置页卡片的回显 / 改动 / 归一 / 落盘，
/// 以及「全局默认值」与「录屏选项窗口」的分工（后者保存时不能把前者冲回默认）。
/// </summary>
/// <remarks>
/// 配置根指向冒烟自己的临时目录，段尾搬回原来的 <c>ConfigStore.ConfigPath</c> 并丢掉缓存
/// （与 <c>Program.ColorPresets.cs</c> 同一套写法）。断言全部落在「看得见的行为」上：
/// 控件状态、运行期缓存 <c>AppState.Record</c>、<c>configs/settings.json</c> 的字节。
/// </remarks>
internal static partial class Program
{
    private static void RecordSettingsSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var appStateType = asm.GetType("LTools.Core.AppState")!;
        var settingsType = asm.GetType("LTools.Features.CapturePro.RecordSettings")!;
        var settingsStoreType = asm.GetType("LTools.Config.SettingsStore")!;

        var recordField = appStateType.GetField("Record", Stat)!;
        var oldRecord = recordField.GetValue(null);
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "recordsettings-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        recordField.SetValue(null, new RecordOptions());

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateRecord", Inst)!
                .Invoke(settings, null);

            ComboBox Combo(string name) => (ComboBox)settings.FindName(name)!;
            CheckBox Box(string name) => (CheckBox)settings.FindName(name)!;
            TextBox Input(string name) => (TextBox)settings.FindName(name)!;

            // ============ ① 回显：控件就是当前设置（默认值照上游 configurationschema）============
            Check(Equals(TagOf(Combo("RecordClarityCombo")), "1080p"), "清晰度回显 1080p");
            Check(Equals(TagOf(Combo("RecordFpsCombo")), 30), "帧率回显 30");
            Check(Equals(TagOf(Combo("RecordFormatCombo")), "mp4"), "默认格式回显 mp4");
            Check(Equals(TagOf(Combo("RecordQualityCombo")), 80), "质量回显 80");
            Check(Equals(TagOf(Combo("RecordPresetCombo")), "veryfast"), "速度档回显 veryfast");
            Check(Equals(TagOf(Combo("RecordAnimClarityCombo")), "720p"), "动图清晰度回显 720p");
            Check(Equals(TagOf(Combo("RecordAnimFpsCombo")), 10), "动图帧率回显 10");
            Check(Equals(TagOf(Combo("RecordTrailCombo")), 500), "拖尾回显 500ms");
            Check(Equals(TagOf(Combo("RecordKeySizeCombo")), 64), "按键浮层回显 64");
            Check(Equals(TagOf(Combo("RecordDelayCombo")), 0), "等待秒数回显 0");

            Check(Box("RecordLoopBox").IsChecked == true, "动图默认无限循环");
            Check(Box("RecordFollowBox").IsChecked == false, "默认不跟随鼠标");
            Check(Box("RecordFollowSmoothBox").IsChecked == true, "跟随默认平滑");
            Check(Box("RecordFollowJitterBox").IsChecked == true, "跟随默认抑抖");
            Check(Box("RecordToolbarBox").IsChecked == true, "工具条默认录进画面");
            Check(Box("RecordStatusBox").IsChecked == true, "默认显示状态提示");
            Check(Box("RecordAutoExitBox").IsChecked == false, "默认不自动收起");
            Check(Box("RecordNotifyBox").IsChecked == false, "默认不额外提示导出");
            Check(Box("RecordDeleteBox").IsChecked == false, "默认关窗不删文件");
            Check(Box("RecordSeparateAudioBox").IsChecked == false, "默认混成一条音轨");

            Check(Input("RecordKeyBgBox").Text == "#000000CC", "按键底色回显 #000000CC");
            Check(Input("RecordKeyFgBox").Text == "#FFFFFFFF", "按键字色回显白");
            Check(Input("RecordHighlightBox").Text == "#FFFF0080", "光标高亮回显玫红");
            Check(Input("RecordProgressBox").Text == "#1677FFFF", "进度条回显蓝");
            Check(Input("RecordDirBox").Text.Length == 0, "保存目录默认留空");
            Check(Input("RecordNameBox").Text == "SnowShot_Video_{YYYY-MM-DD_HH-mm-ss}", "文件名模板回显默认");

            Check(!File.Exists(settingsFile), "只回显不写盘（还没人改过设置）");

            // ============ ② 改控件：运行期缓存与 settings.json 立刻跟着变 ============
            SelectTag(Combo("RecordClarityCombo"), "720p");
            SelectTag(Combo("RecordFpsCombo"), 24);
            SelectTag(Combo("RecordFormatCombo"), "gif");
            SelectTag(Combo("RecordQualityCombo"), 60);
            SelectTag(Combo("RecordPresetCombo"), "medium");
            SelectTag(Combo("RecordAnimClarityCombo"), "480p");
            SelectTag(Combo("RecordAnimFpsCombo"), 20);
            SelectTag(Combo("RecordTrailCombo"), 800);
            SelectTag(Combo("RecordKeySizeCombo"), 96);
            SelectTag(Combo("RecordDelayCombo"), 3);

            Box("RecordLoopBox").IsChecked = false;
            Box("RecordFollowBox").IsChecked = true;
            Box("RecordFollowJitterBox").IsChecked = false;
            Box("RecordToolbarBox").IsChecked = false;
            Box("RecordStatusBox").IsChecked = false;
            Box("RecordAutoExitBox").IsChecked = true;
            Box("RecordNotifyBox").IsChecked = true;
            Box("RecordDeleteBox").IsChecked = true;
            Box("RecordSeparateAudioBox").IsChecked = true;

            Input("RecordKeyBgBox").Text = "#80112233";
            Input("RecordKeyFgBox").Text = "#FF00FF00";
            Input("RecordHighlightBox").Text = "#40FF0000";
            Input("RecordProgressBox").Text = "#8011FF22";

            string saveDir = Path.Combine(configRoot, "videos");
            Directory.CreateDirectory(saveDir);
            Input("RecordDirBox").Text = saveDir;
            Input("RecordNameBox").Text = "clip_{YYYY-MM-DD}";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("RecordDirBox_LostFocus", Inst)!
                .Invoke(settings, new object[] { Input("RecordDirBox"), new RoutedEventArgs() });
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("RecordNameBox_LostFocus", Inst)!
                .Invoke(settings, new object[] { Input("RecordNameBox"), new RoutedEventArgs() });
            Pump(settings);

            var live = (RecordOptions)recordField.GetValue(null)!;
            Check(live.Clarity == "720p", "清晰度写进运行期缓存：" + live.Clarity);
            Check(live.FrameRate == 24, "帧率写进运行期缓存：" + live.FrameRate);
            Check(live.OutputFormat == "gif", "格式写进运行期缓存：" + live.OutputFormat);
            Check(live.VideoQuality == 60, "质量写进运行期缓存：" + live.VideoQuality);
            Check(live.EncodingPreset == "medium", "速度档写进运行期缓存：" + live.EncodingPreset);
            Check(live.AnimatedClarity == "480p", "动图清晰度写进运行期缓存：" + live.AnimatedClarity);
            Check(live.AnimatedFrameRate == 20, "动图帧率写进运行期缓存：" + live.AnimatedFrameRate);
            Check(live.TrailDurationMs == 800, "拖尾写进运行期缓存：" + live.TrailDurationMs);
            Check(live.KeyboardSize == 96, "按键浮层写进运行期缓存：" + live.KeyboardSize);
            Check(live.StartDelaySeconds == 3, "等待秒数写进运行期缓存：" + live.StartDelaySeconds);
            Check(!live.LoopAnimated, "循环开关跟着关");
            Check(live.FollowMouse && live.FollowMouseSmooth && !live.FollowMouseReduceJitter,
                "跟随鼠标三项各自独立");
            Check(!live.CaptureToolbarInRecording && !live.ShowStatusPrompts, "入镜 / 状态提示两个开关");
            Check(live.AutoExitAfterRecording && live.NotifyAfterExport && live.DeleteFilesOnClose,
                "三个收尾开关");
            Check(live.SeparateAudioTracks, "分离音轨开关");
            Check(live.KeyboardBackgroundColor == "#80112233", "按键底色：" + live.KeyboardBackgroundColor);
            Check(live.KeyboardForegroundColor == "#FF00FF00", "按键字色：" + live.KeyboardForegroundColor);
            Check(live.MouseHighlightColor == "#40FF0000", "光标高亮：" + live.MouseHighlightColor);
            Check(live.ProgressBarColor == "#8011FF22", "进度条色：" + live.ProgressBarColor);
            Check(live.SaveDirectory == saveDir, "保存目录：" + live.SaveDirectory);
            Check(live.FilenameFormat == "clip_{YYYY-MM-DD}", "文件名模板：" + live.FilenameFormat);

            // 没打完的色值不写（打一半的 #FF00 不该把已存颜色冲掉）
            Input("RecordHighlightBox").Text = "#FF00";
            Pump(settings);
            Check(((RecordOptions)recordField.GetValue(null)!).MouseHighlightColor == "#40FF0000",
                "半截色值不落盘");

            // ============ ③ 落盘：settings.json 里就是这些值，读回来一致 ============
            string text = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
            Check(text.Contains("\"Record\""), "settings.json 里有 Record 段");
            Check(text.Contains("\"Clarity\": \"720p\""), "清晰度落到 settings.json");
            Check(text.Contains("\"VideoQuality\": 60"), "质量落到 settings.json");
            Check(text.Contains("\"TrailDurationMs\": 800"), "拖尾落到 settings.json");
            Check(text.Contains("\"KeyboardSize\": 96"), "按键浮层落到 settings.json");
            Check(text.Contains("\"StartDelaySeconds\": 3"), "等待秒数落到 settings.json");
            Check(text.Contains("\"OutputFormat\": \"gif\""), "格式落到 settings.json");
            Check(text.Contains("\"MouseHighlightColor\": \"#40FF0000\""), "颜色落到 settings.json");

            object file = settingsStoreType.GetMethod("Read", Stat)!.Invoke(null, new object[] { configPath })!;
            object stored = file.GetType().GetProperty("Record")!.GetValue(file)!;
            string Stored(string name) =>
                stored.GetType().GetProperty(name)!.GetValue(stored)?.ToString() ?? "";
            Check(Stored("Clarity") == "720p" && Stored("EncodingPreset") == "medium",
                "重新读盘拿到清晰度 / 速度档");
            Check(Stored("SaveDirectory") == saveDir, "重新读盘拿到保存目录");
            Check(Stored("FilenameFormat") == "clip_{YYYY-MM-DD}", "重新读盘拿到文件名模板");
            Check(Stored("AnimatedFrameRate") == "20", "重新读盘拿到动图帧率");

            // ============ ④ 归一：写入侧也要夹（MCP / 手改配置写进来的怪值）============
            var modify = settingsType.GetMethod("Modify", Stat)!;
            modify.Invoke(null, new object[] { (Action<RecordOptions>)(o => o.TrailDurationMs = 260) });
            Check(((RecordOptions)recordField.GetValue(null)!).TrailDurationMs == 300,
                "拖尾按 100ms 取整后才落缓存");

            modify.Invoke(null, new object[] { (Action<RecordOptions>)(o => o.KeyboardSize = 200) });
            Check(((RecordOptions)recordField.GetValue(null)!).KeyboardSize == 128,
                "按键浮层夹到 128");

            modify.Invoke(null, new object[] { (Action<RecordOptions>)(o => o.VideoQuality = 500) });
            Check(((RecordOptions)recordField.GetValue(null)!).VideoQuality == 100, "质量夹到 100");

            modify.Invoke(null, new object[] { (Action<RecordOptions>)(o => o.Clarity = "8k") });
            Check(((RecordOptions)recordField.GetValue(null)!).Clarity == "1080p",
                "认不出的清晰度回默认 1080p");

            // 归一后重新回显：控件跟着夹过的值
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateRecord", Inst)!
                .Invoke(settings, null);
            Check(Equals(TagOf(Combo("RecordTrailCombo")), 300), "回显跟着归一后的拖尾");
            Check(Equals(TagOf(Combo("RecordClarityCombo")), "1080p"), "回显跟着归一后的清晰度");

            // ============ ⑤ 全局默认值进计划（覆盖层 / 内置功能入口）============
            var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
            object plan = planType.GetMethod("FromSettings", Stat)!
                .Invoke(null, new[] { recordField.GetValue(null), (object)false })!;
            string Plan(string name) =>
                plan.GetType().GetProperty(name)!.GetValue(plan)?.ToString() ?? "";
            Check(Plan("Format") == "Gif", "开录计划用设置里的默认格式：" + Plan("Format"));
            Check(Plan("Fps") == "24", "开录计划用设置里的默认帧率：" + Plan("Fps"));
            Check(Plan("Countdown") == "3", "开录计划用设置里的等待秒数：" + Plan("Countdown"));
            Check((bool)plan.GetType().GetProperty("FollowMouse")!.GetValue(plan)!, "开录计划用设置里的跟随鼠标");

            Save(Render(settings), Path.Combine(_outDir, "record-settings.png"));
        }
        finally
        {
            try { settings?.Close(); } catch { /* 已经关了 */ }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            recordField.SetValue(null, oldRecord);
        }
    }

    /// <summary>取下拉里选中项的 Tag（回显断言用）。</summary>
    private static object? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>按 Tag 选中一项（找不到就报错：设置页必须把当前值列出来）。</summary>
    private static void SelectTag(ComboBox box, object tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && Equals(item.Tag, tag))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        throw new InvalidOperationException($"下拉里没有 {tag}");
    }
}
