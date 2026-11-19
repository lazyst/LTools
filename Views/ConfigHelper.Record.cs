using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 录屏」那张卡片：清晰度 / 帧率 / 格式 / 质量 / 特效尺寸配色 /
/// 跟随鼠标 / 开录等待 / 六个行为开关 / 保存目录与文件名模板。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screen_recording/*</c> 的默认值（<c>configurationschema.cpp</c> 的
/// <c>kSchemaStrings*</c> / <c>kSchemaRange*</c>），读写与归一都在 <see cref="RecordOptions"/>，
/// 落盘 / 刷缓存走 <see cref="RecordSettings"/>。
/// </para>
/// <para>
/// 这里改的是「之后每次录的默认值」；录屏面板上仍可逐次改格式 / 延迟 / 声音 / 摄像头 / 特效开关
/// （见 <c>Views/CapturePro/CaptureRecordWindow</c>），「录屏选项…」窗口只改这一次录什么。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>取色按钮回填文本时挡住 <see cref="RecordColor_Changed"/>，避免一次点击写两遍盘。</summary>
    private bool _recordColorSync;

    /// <summary>把卡片上所有控件刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateRecord()
    {
        var options = RecordSettings.Current.Normalized();

        Fill(RecordClarityCombo, RecordOptions.ClarityOptions.Select(v => (ClarityLabel(v), (object)v)));
        Fill(RecordAnimClarityCombo,
            RecordOptions.AnimatedClarityOptions.Select(v => (v, (object)v)));
        Fill(RecordFormatCombo, RecordOptions.FormatOptions.Select(v => (FormatLabel(v), (object)v)));
        Fill(RecordPresetCombo, RecordOptions.PresetOptions.Select(v => (PresetLabel(v), (object)v)));

        Fill(RecordFpsCombo, new[] { 10, 15, 20, 24, 25, 30, 50, 60 }
            .Select(v => ($"{v} 帧/秒" + (v == RecordOptions.DefaultFrameRate ? "（默认）" : ""), (object)v)));
        Fill(RecordAnimFpsCombo, new[] { 5, 10, 12, 15, 20, 24, 30 }
            .Select(v => ($"{v} 帧/秒" + (v == RecordOptions.DefaultAnimatedFrameRate ? "（默认）" : ""), (object)v)));
        Fill(RecordQualityCombo, Enumerable.Range(0, 21)
            .Select(i => i * 5)
            .Select(v => ($"{v}" + (v == RecordOptions.DefaultVideoQuality ? "（默认）" : ""), (object)v)));
        Fill(RecordTrailCombo, Enumerable.Range(1, 20)
            .Select(i => i * 100)
            .Select(v => ($"{v} ms" + (v == RecordOptions.DefaultTrailDurationMs ? "（默认）" : ""), (object)v)));
        Fill(RecordKeySizeCombo, Enumerable.Range(4, 13)
            .Select(i => i * 8)
            .Select(v => ($"{v} px" + (v == RecordOptions.DefaultKeyboardSize ? "（默认）" : ""), (object)v)));
        Fill(RecordDelayCombo, Enumerable.Range(0, RecordOptions.MaxStartDelaySeconds + 1)
            .Select(v => (v == 0 ? "不等待" : $"{v} 秒", (object)v)));

        SelectTag(RecordClarityCombo, options.Clarity);
        SelectTag(RecordAnimClarityCombo, options.AnimatedClarity);
        SelectTag(RecordFormatCombo, options.OutputFormat);
        SelectTag(RecordPresetCombo, options.EncodingPreset);
        SelectTag(RecordFpsCombo, options.FrameRate);
        SelectTag(RecordAnimFpsCombo, options.AnimatedFrameRate);
        SelectTag(RecordQualityCombo, options.VideoQuality);
        SelectTag(RecordTrailCombo, options.TrailDurationMs);
        SelectTag(RecordKeySizeCombo, options.KeyboardSize);
        SelectTag(RecordDelayCombo, options.StartDelaySeconds);

        RecordLoopBox.IsChecked = options.LoopAnimated;
        RecordFollowBox.IsChecked = options.FollowMouse;
        RecordFollowSmoothBox.IsChecked = options.FollowMouseSmooth;
        RecordFollowJitterBox.IsChecked = options.FollowMouseReduceJitter;
        RecordToolbarBox.IsChecked = options.CaptureToolbarInRecording;
        RecordStatusBox.IsChecked = options.ShowStatusPrompts;
        RecordAutoExitBox.IsChecked = options.AutoExitAfterRecording;
        RecordNotifyBox.IsChecked = options.NotifyAfterExport;
        RecordDeleteBox.IsChecked = options.DeleteFilesOnClose;
        RecordSeparateAudioBox.IsChecked = options.SeparateAudioTracks;

        RecordKeyBgBox.Text = options.KeyboardBackgroundColor;
        RecordKeyFgBox.Text = options.KeyboardForegroundColor;
        RecordHighlightBox.Text = options.MouseHighlightColor;
        RecordProgressBox.Text = options.ProgressBarColor;

        RecordDirBox.Text = options.SaveDirectory;
        RecordNameBox.Text = options.FilenameFormat;
    }

    /// <summary>下拉 / 勾选共用：把整批控件读一遍，一次归一 + 一次落盘。</summary>
    private void RecordSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        RecordSettings.Modify(options =>
        {
            options.Clarity = TagText(RecordClarityCombo, options.Clarity);
            options.AnimatedClarity = TagText(RecordAnimClarityCombo, options.AnimatedClarity);
            options.OutputFormat = TagText(RecordFormatCombo, options.OutputFormat);
            options.EncodingPreset = TagText(RecordPresetCombo, options.EncodingPreset);
            options.FrameRate = TagNumber(RecordFpsCombo, options.FrameRate);
            options.AnimatedFrameRate = TagNumber(RecordAnimFpsCombo, options.AnimatedFrameRate);
            options.VideoQuality = TagNumber(RecordQualityCombo, options.VideoQuality);
            options.TrailDurationMs = TagNumber(RecordTrailCombo, options.TrailDurationMs);
            options.KeyboardSize = TagNumber(RecordKeySizeCombo, options.KeyboardSize);
            options.StartDelaySeconds = TagNumber(RecordDelayCombo, options.StartDelaySeconds);

            options.LoopAnimated = RecordLoopBox.IsChecked == true;
            options.FollowMouse = RecordFollowBox.IsChecked == true;
            options.FollowMouseSmooth = RecordFollowSmoothBox.IsChecked == true;
            options.FollowMouseReduceJitter = RecordFollowJitterBox.IsChecked == true;
            options.CaptureToolbarInRecording = RecordToolbarBox.IsChecked == true;
            options.ShowStatusPrompts = RecordStatusBox.IsChecked == true;
            options.AutoExitAfterRecording = RecordAutoExitBox.IsChecked == true;
            options.NotifyAfterExport = RecordNotifyBox.IsChecked == true;
            options.DeleteFilesOnClose = RecordDeleteBox.IsChecked == true;
            options.SeparateAudioTracks = RecordSeparateAudioBox.IsChecked == true;
        });
    }

    /// <summary>四个颜色输入框：打得出来的合法色值立刻落盘，没打完的半截（<c>#FF00</c>）不写。</summary>
    private void RecordColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _recordColorSync || sender is not TextBox box) return;

        string hex = RecordOptions.NormalizeColor(box.Text, "");
        if (hex.Length == 0) return;

        RecordSettings.Modify(options =>
        {
            if (ReferenceEquals(box, RecordKeyBgBox)) options.KeyboardBackgroundColor = hex;
            else if (ReferenceEquals(box, RecordKeyFgBox)) options.KeyboardForegroundColor = hex;
            else if (ReferenceEquals(box, RecordHighlightBox)) options.MouseHighlightColor = hex;
            else if (ReferenceEquals(box, RecordProgressBox)) options.ProgressBarColor = hex;
        });
    }

    private void RecordKeyBg_Click(object sender, RoutedEventArgs e) =>
        PickRecordColor(RecordKeyBgBox, o => o.KeyboardBackgroundColor, (o, v) => o.KeyboardBackgroundColor = v);

    private void RecordKeyFg_Click(object sender, RoutedEventArgs e) =>
        PickRecordColor(RecordKeyFgBox, o => o.KeyboardForegroundColor, (o, v) => o.KeyboardForegroundColor = v);

    private void RecordHighlight_Click(object sender, RoutedEventArgs e) =>
        PickRecordColor(RecordHighlightBox, o => o.MouseHighlightColor, (o, v) => o.MouseHighlightColor = v);

    private void RecordProgress_Click(object sender, RoutedEventArgs e) =>
        PickRecordColor(RecordProgressBox, o => o.ProgressBarColor, (o, v) => o.ProgressBarColor = v);

    /// <summary>弹一次取色框；确认后把色值写进输入框与设置（<c>#AARRGGBB</c>，与上游一致）。</summary>
    private void PickRecordColor(TextBox box, Func<RecordOptions, string> get, Action<RecordOptions, string> set)
    {
        if (!CaptureColorDialog.Show(this, MediaColor(get(RecordSettings.Current)), out var picked)) return;

        string hex = HexOf(picked);

        _recordColorSync = true;
        try { box.Text = hex; }
        finally { _recordColorSync = false; }

        RecordSettings.Modify(options => set(options, hex));
    }

    /// <summary>保存目录：留空 = 系统「视频」目录（<see cref="RecordOptions.SaveDirectory"/> 的语义）。</summary>
    private void RecordDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "录屏保存目录",
            InitialDirectory = System.IO.Directory.Exists(RecordDirBox.Text)
                ? RecordDirBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };

        if (dialog.ShowDialog(this) != true) return;

        RecordDirBox.Text = dialog.FolderName;
        RecordSettings.Modify(options => options.SaveDirectory = dialog.FolderName);
    }

    private void RecordDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        string text = (RecordDirBox.Text ?? "").Trim();
        RecordDirBox.Text = text;
        RecordSettings.Modify(options => options.SaveDirectory = text);
    }

    private void RecordNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        string text = (RecordNameBox.Text ?? "").Trim();
        string normalized = text.Length == 0
            ? new RecordOptions().FilenameFormat
            : text;
        RecordNameBox.Text = normalized;
        RecordSettings.Modify(options => options.FilenameFormat = normalized);
    }

    // ============ 小工具 ============

    private static string ClarityLabel(string key) => key switch
    {
        "4k" => "4K（2160p）",
        "2k" => "2K（1440p）",
        _ => key,
    };

    private static string FormatLabel(string key) => key switch
    {
        "mp4" => "MP4 视频",
        "gif" => "GIF 动图",
        "apng" => "APNG 动图",
        "webp" => "WebP 动图",
        _ => key,
    };

    private static string PresetLabel(string key) => key switch
    {
        "ultrafast" => "ultrafast（最快）",
        "veryfast" => "veryfast（默认）",
        "medium" => "medium（中等）",
        "veryslow" => "veryslow（最慢）",
        _ => key,
    };

    private static void Fill(ComboBox box, IEnumerable<(string Label, object Tag)> items)
    {
        if (box.Items.Count > 0) return;

        foreach (var (label, tag) in items) box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
    }

    /// <summary>按 Tag 选中；列表里没有这个值（MCP / 手改配置写进来的）就补一项，别静默落到第一项。</summary>
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

        box.Items.Add(new ComboBoxItem { Content = tag.ToString(), Tag = tag });
        box.SelectedIndex = box.Items.Count - 1;
    }

    private static string TagText(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: string text } ? text : fallback;

    private static int TagNumber(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: int value } ? value : fallback;

    /// <summary>设置里存的是 <c>#AARRGGBB</c>（照 Qt 的写法），取色框要 WPF 的 <see cref="Color"/>。</summary>
    private static Color MediaColor(string hex)
    {
        string text = RecordOptions.NormalizeColor(hex, "#FF000000");
        uint value = Convert.ToUInt32(text[1..], 16);
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static string HexOf(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
