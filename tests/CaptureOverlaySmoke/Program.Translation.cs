using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图 Pro →「文字识别」页的在线翻译卡片（2026-11-20）：五项设置的回显与落盘
/// （源语言 / 目标语言 / 第二目标语言 / 检测门槛 + 完整设置入口），以及
/// 「原文已经是目标语言就改译第二目标语言」这条真行为。
/// </summary>
internal static partial class Program
{
    private static void TranslationSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var languagesType = asm.GetType("LTools.Features.CapturePro.TranslationLanguages")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.TranslationStore")!;

        string configRoot = Path.Combine(_outDir, "translation-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateTranslation", Inst)!
                .Invoke(settings, null);

            ComboBox Combo(string name) => (ComboBox)settings.FindName(name)!;
            TextBox Input(string name) => (TextBox)settings.FindName(name)!;
            TextBlock Text(string name) => (TextBlock)settings.FindName(name)!;

            var source = Combo("TranslationSourceCombo");
            var target = Combo("TranslationTargetCombo");
            var secondary = Combo("TranslationSecondaryCombo");
            var threshold = Input("TranslationThresholdBox");

            // ============ ① 回显（默认值照上游 configurationschema）============
            Check(source.Items.Count == 10, $"源语言 = 自动判断 + 9 门（实际 {source.Items.Count}）");
            Check(target.Items.Count == 9, $"目标语言 9 门（实际 {target.Items.Count}）");
            Check(secondary.Items.Count == 10, $"第二目标语言 = 不用 + 9 门（实际 {secondary.Items.Count}）");
            Check(Equals(TagOf(source), "auto"), "源语言默认自动判断");
            Check(Equals(TagOf(target), "zh-CN"), "目标语言默认简体中文");
            Check(Equals(TagOf(secondary), ""), "第二目标语言默认不用");
            Check(threshold.Text == "60", "检测门槛默认 60");
            Check(Text("TranslationSummaryText").Text.Contains("还没配"), "没配服务时摘要提示去配置");
            Check(!File.Exists(settingsFile), "只回显不写盘");

            // ============ ② 改下拉：立刻落盘到 settings.json 的 Translation 段 ============
            SelectTag(target, "en");
            Pump(settings);
            Check(TranslationField(settingsFile, "Target") == "en", "目标语言改成 English 立刻落盘");

            SelectTag(secondary, "ja");
            Pump(settings);
            Check(TranslationField(settingsFile, "SecondaryTarget") == "ja", "第二目标语言落盘");

            SelectTag(source, "auto");
            SelectTag(secondary, "");
            Pump(settings);
            Check(TranslationField(settingsFile, "SecondaryTarget") == "", "第二目标选「不用」→ 落空");

            // ============ ③ 门槛：失焦 / 回车落盘，越界夹取，填错回默认 ============
            threshold.Text = "45";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitThreshold", Inst)!
                .Invoke(settings, null);
            Check(TranslationField(settingsFile, "DetectionThreshold") == "45", "门槛 45 落盘");
            Check(threshold.Text == "45", "门槛写回控件");

            threshold.Text = "5";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitThreshold", Inst)!
                .Invoke(settings, null);
            Check(TranslationField(settingsFile, "DetectionThreshold") == "30", "低于 30 夹到 30");
            Check(threshold.Text == "30", "夹取后写回控件");

            threshold.Text = "abc";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitThreshold", Inst)!
                .Invoke(settings, null);
            Check(TranslationField(settingsFile, "DetectionThreshold") == "60", "填错回默认 60");

            // ============ ④ 真行为：原文已经是目标语言就改译第二目标语言 ============
            SelectTag(target, "zh-CN");
            SelectTag(secondary, "en");
            threshold.Text = "60";
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("CommitThreshold", Inst)!
                .Invoke(settings, null);
            Pump(settings);

            var resolve = storeType.GetMethod("ResolveTarget", BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)!;
            Check((string)resolve.Invoke(null, new object?[] { "zh-CN", "这是一段中文" })! == "en",
                "中文稿 + 目标中文 → 改译英文");
            Check((string)resolve.Invoke(null, new object?[] { "zh-CN", "Hello world" })! == "zh-CN",
                "英文稿 + 目标中文 → 照旧译中文");

            // ============ ⑤ 完整设置框也认这两项 ============
            var dialogType = asm.GetType("LTools.Views.CapturePro.TranslationSettingsWindow")!;
            var configType = asm.GetType("LTools.Features.CapturePro.TranslationConfig")!;
            var configEmpty = configType.GetField("Empty", BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)!.GetValue(null)!;
            var dialog = (Window)Activator.CreateInstance(dialogType, Inst, null,
                new[] { configEmpty }, null)!;
            try
            {
                dialog.Show();
                Pump(dialog);

                var dialogSecondary = (ComboBox)dialog.FindName("SecondaryBox")!;
                var dialogThreshold = (TextBox)dialog.FindName("ThresholdBox")!;
                Check(dialogSecondary.Items.Count == 10, "设置框的第二目标语言也是「不用 + 9 门」");
                Check(Equals((dialogSecondary.SelectedItem as ComboBoxItem)?.Tag, "en"),
                    "设置框回显当前第二目标语言");
                Check(dialogThreshold.Text == "60", "设置框回显门槛 60");
            }
            finally
            {
                dialog.Close();
            }
        }
        finally
        {
            settings?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }

    /// <summary>读 settings.json 里 Translation 段的某个字段（缺文件 / 缺字段 = 空串）。</summary>
    private static string TranslationField(string settingsFile, string name)
    {
        if (!File.Exists(settingsFile)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        if (!doc.RootElement.TryGetProperty("Translation", out var translation)) return "";
        if (!translation.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null => "",
            _ => value.GetRawText(),
        };
    }
}
