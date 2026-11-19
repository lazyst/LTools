using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图 Pro →「采集历史」页（2026-11-20，照 Snow Shot 的 <c>capture_history/*</c>）：
/// 五项默认值回显、永久保留时三个数字框置灰、数字越界夹取、落盘到 settings.json。
/// </summary>
internal static partial class Program
{
    private static void HistoryPolicySection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var storeType = asm.GetType("LTools.Features.CapturePro.CaptureHistoryStore")!;

        string configRoot = Path.Combine(_outDir, "historypolicy-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        storeType.GetMethod("Invalidate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, null);

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateCaptureHistory", Inst)!
                .Invoke(settings, null);

            CheckBox Box(string name) => (CheckBox)settings.FindName(name)!;
            TextBox Input(string name) => (TextBox)settings.FindName(name)!;
            TextBlock Text(string name) => (TextBlock)settings.FindName(name)!;

            var enabled = Box("CaptureHistoryEnabledBox");
            var keep = Box("CaptureHistoryKeepBox");
            var days = Input("CaptureHistoryDaysBox");
            var entries = Input("CaptureHistoryEntriesBox");
            var disk = Input("CaptureHistoryDiskBox");

            // ============ ① 默认值（逐字照上游 configurationschema） ============
            Check(enabled.IsChecked == true, "默认记录新采集");
            Check(keep.IsChecked == false, "默认不永久保留");
            Check(days.Text == "7", "保留天数默认 7");
            Check(entries.Text == "100", "条数默认 100");
            Check(disk.Text == "1024", "磁盘上限默认 1024");
            Check(days.IsEnabled && entries.IsEnabled && disk.IsEnabled, "默认三个数字框可编辑");
            Check(Text("CaptureHistorySummaryText").Text.Contains("当前"), "摘要有当前条数 / 占用");
            Check(!File.Exists(settingsFile), "只回显不写盘");

            // ============ ② 永久保留：落盘 + 三个数字框置灰 ============
            keep.IsChecked = true;
            Pump(settings);
            Check(HistoryField(settingsFile, "KeepPermanently") == "true", "永久保留落盘");
            Check(!days.IsEnabled && !entries.IsEnabled && !disk.IsEnabled, "永久保留时三个数字框置灰");

            keep.IsChecked = false;
            Pump(settings);
            Check(days.IsEnabled, "取消永久保留后数字框恢复可编辑");

            // ============ ③ 数字：越界夹取 / 填错回原值 ============
            days.Text = "9999";
            entries.Text = "0";
            disk.Text = "abc";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitHistoryNumbers", Inst)!
                .Invoke(settings, null);
            Check(HistoryField(settingsFile, "RetentionDays") == "365", "天数 9999 夹到 365");
            Check(days.Text == "365", "夹取后写回控件");
            Check(HistoryField(settingsFile, "MaxEntries") == "100", "条数 0 = 没配 → 默认 100");
            Check(HistoryField(settingsFile, "MaxDiskMiB") == "1024", "填错回默认 1024");

            // ============ ④ 关掉记录：落盘（CaptureCatalog 按它决定入不入库） ============
            enabled.IsChecked = false;
            Pump(settings);
            Check(HistoryField(settingsFile, "Enabled") == "false", "关掉记录落盘");
            var enabledProp = storeType.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)!;
            Check(!(bool)enabledProp.GetValue(null)!, "运行期缓存同步关掉");

            enabled.IsChecked = true;
            Pump(settings);
            Check((bool)enabledProp.GetValue(null)!, "再打开立刻生效");
        }
        finally
        {
            settings?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
            storeType.GetMethod("Invalidate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, null);
        }
    }

    /// <summary>读 settings.json 里 CaptureHistory 段的某个字段（缺文件 / 缺字段 = 空串）。</summary>
    private static string HistoryField(string settingsFile, string name)
    {
        if (!File.Exists(settingsFile)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        if (!doc.RootElement.TryGetProperty("CaptureHistory", out var section)) return "";
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
