using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图 Pro →「贴图窗口」页里的贴图历史策略（2026-11-20，照 Snow Shot 的 <c>pinned_history/*</c>）：
/// 五项回显（默认沿用 LTools 的 30 天 / 200 条）、永久保留时数字框置灰、越界夹取、落盘。
/// </summary>
internal static partial class Program
{
    private static void PinHistorySection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var storeType = asm.GetType("LTools.Features.CapturePro.PinnedHistoryStore")!;
        var invalidate = storeType.GetMethod("Invalidate", BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic)!;

        string configRoot = Path.Combine(_outDir, "pinnedhistory-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        invalidate.Invoke(null, null);

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulatePinHistory", Inst)!
                .Invoke(settings, null);

            CheckBox Box(string name) => (CheckBox)settings.FindName(name)!;
            TextBox Input(string name) => (TextBox)settings.FindName(name)!;

            var enabled = Box("PinHistoryEnabledBox");
            var keep = Box("PinHistoryKeepBox");
            var days = Input("PinHistoryDaysBox");
            var entries = Input("PinHistoryEntriesBox");
            var disk = Input("PinHistoryDiskBox");

            Check(enabled.IsChecked == true, "默认记录贴图历史");
            Check(keep.IsChecked == false, "默认不永久保留");
            Check(days.Text == "30", "保留天数默认 30（沿用 LTools 口径）");
            Check(entries.Text == "200", "条数默认 200（沿用 LTools 口径）");
            Check(disk.Text == "1024", "磁盘上限默认 1024");
            Check(!File.Exists(settingsFile), "只回显不写盘");

            keep.IsChecked = true;
            Pump(settings);
            Check(PinHistoryField(settingsFile, "KeepPermanently") == "true", "永久保留落盘");
            Check(!days.IsEnabled && !entries.IsEnabled && !disk.IsEnabled, "永久保留时数字框置灰");

            keep.IsChecked = false;
            Pump(settings);
            Check(days.IsEnabled, "取消永久保留后恢复可编辑");

            days.Text = "9999";
            entries.Text = "0";
            disk.Text = "abc";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitPinHistoryNumbers", Inst)!
                .Invoke(settings, null);
            Check(PinHistoryField(settingsFile, "RetentionDays") == "365", "天数 9999 夹到 365");
            Check(PinHistoryField(settingsFile, "MaxEntries") == "200", "条数 0 = 没配 → 默认 200");
            Check(PinHistoryField(settingsFile, "MaxDiskMiB") == "1024", "填错回默认 1024");

            enabled.IsChecked = false;
            Pump(settings);
            Check(PinHistoryField(settingsFile, "Enabled") == "false", "关掉记录落盘");
            var enabledProp = storeType.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)!;
            Check(!(bool)enabledProp.GetValue(null)!, "运行期缓存同步关掉");
        }
        finally
        {
            settings?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
            invalidate.Invoke(null, null);
        }
    }

    private static string PinHistoryField(string settingsFile, string name)
    {
        if (!File.Exists(settingsFile)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        if (!doc.RootElement.TryGetProperty("PinnedHistory", out var section)) return "";
        if (!section.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }
}
