using System;
using System.Windows;
using System.Windows.Controls;
using LTools.Config;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Views.Controls;
using NAudio.CoreAudioApi;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「录屏选项」：声音 / 摄像头画中画 / 键鼠特效 / 编码 / 录完裁剪的默认值。
/// </summary>
/// <remarks>
/// <para>
/// 保存走 <c>SettingsStore.Modify</c>（只写 <c>configs/settings.json</c>），
/// 同时换掉 <see cref="AppState.Record"/> 指向的新实例——录制线程可能正在读旧实例，
/// 所以**不就地修改**它。
/// </para>
/// <para>
/// 麦克风设备名用 NAudio 现枚举（一次几十毫秒）；摄像头只给序号，
/// 不去逐个打开探名字（每开一个要两三百毫秒，而且会闪摄像头灯）。
/// </para>
/// </remarks>
public partial class RecordOptionsWindow : Window
{
    private bool _loading = true;

    /// <summary>「从面板打开」时的那份运行期选项：保存后要就地生效（composer 每帧读它）。</summary>
    private RecordOptions? _runtime;

    internal RecordOptionsWindow(RecordOptions options)
    {
        InitializeComponent();

        FillMics(options.MicrophoneDevice);
        FillCameras(options.CameraDevice);

        SystemAudioBox.IsChecked = options.SystemAudio;
        MicBox.IsChecked = options.Microphone;

        CameraSizeSlider.Value = Math.Clamp(options.CameraSize, 50, 500);
        SelectTag(CameraShapeBox, options.CameraShape == RecordCameraShape.circle ? "circle" : "square");
        MirrorBox.IsChecked = options.CameraMirrored;

        HighlightBox.IsChecked = options.MouseHighlight;
        CursorBox.IsChecked = options.ShowCursor;
        ClicksBox.IsChecked = options.RecordClicks;
        ScrollBox.IsChecked = options.RecordScroll;
        KeysBox.IsChecked = options.Keystroke;
        RadiusSlider.Value = Math.Clamp(options.MouseHighlightRadius, 8, 128);

        SelectTag(CodecBox, options.Codec switch
        {
            RecordCodec.h264 => "h264",
            RecordCodec.hevc => "hevc",
            _ => "auto",
        });

        TrimBox.IsChecked = options.TrimAfterRecord;
        AutoHideBox.IsChecked = options.AutoHideToolbar;

        SyncEnabled();
        _loading = false;
    }

    /// <summary>从工具条「更多 → 录屏选项…」打开（覆盖层在下面，对话框跟着它）。</summary>
    internal static void Show(Window? owner)
    {
        var window = new RecordOptionsWindow(AppState.Record)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? false,
        };

        window.ShowDialog();
    }

    /// <summary>
    /// 从录屏面板的「特效设置」打开：改的是**这一轮**的选项（保存时同时写成默认值），
    /// 返回 true = 保存了，调用方据此重画面板。
    /// </summary>
    internal static bool Show(Window? owner, RecordOptions runtime)
    {
        var window = new RecordOptionsWindow(runtime)
        {
            _runtime = runtime,
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? false,
        };

        return window.ShowDialog() == true;
    }

    private void FillMics(string? selected)
    {
        MicDeviceBox.Items.Add(new ComboBoxItem { Content = "系统默认输入设备", Tag = "" });

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string name = device.FriendlyName ?? "";
                if (name.Length == 0) continue;
                MicDeviceBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "枚举麦克风失败：" + ex.Message);
        }

        int index = 0;
        for (int i = 0; i < MicDeviceBox.Items.Count; i++)
        {
            if (MicDeviceBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, selected ?? "", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        MicDeviceBox.SelectedIndex = index;
    }

    private void FillCameras(int selected)
    {
        CameraBox.Items.Add(new ComboBoxItem { Content = "不录摄像头", Tag = -1 });
        for (int i = 0; i < 4; i++)
            CameraBox.Items.Add(new ComboBoxItem { Content = $"摄像头 {i}", Tag = i });

        CameraBox.SelectedIndex = selected is >= 0 and < 4 ? selected + 1 : 0;
    }

    private void Camera_Changed(object sender, SelectionChangedEventArgs e) => SyncEnabled();

    /// <summary>麦克风 / 光标高亮这类开关也会解锁下面的设备下拉、半径滑块（别等用户去动摄像头）。</summary>
    private void Toggle_Changed(object sender, RoutedEventArgs e) => SyncEnabled();

    private void CameraSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraSizeText == null) return;
        CameraSizeText.Text = ((int)Math.Round(e.NewValue)).ToString() + " px";
    }

    private void Radius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusText == null) return;
        RadiusText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    /// <summary>摄像头关着的时候，尺寸 / 形状 / 镜像不可用（免得以为设了就会录）。</summary>
    private void SyncEnabled()
    {
        bool camera = (TagOf(CameraBox) as int? ?? -1) >= 0;
        CameraSizeSlider.IsEnabled = camera;
        CameraShapeBox.IsEnabled = camera;
        MirrorBox.IsEnabled = camera;

        bool mic = MicBox.IsChecked == true;
        MicDeviceBox.IsEnabled = mic;

        bool highlight = HighlightBox.IsChecked == true;
        RadiusSlider.IsEnabled = highlight;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var options = new RecordOptions
        {
            SystemAudio = SystemAudioBox.IsChecked == true,
            Microphone = MicBox.IsChecked == true,
            MicrophoneDevice = TagOf(MicDeviceBox) as string,
            CameraDevice = TagOf(CameraBox) as int? ?? -1,
            CameraSize = (int)Math.Round(CameraSizeSlider.Value),
            CameraShape = string.Equals(TagOf(CameraShapeBox) as string, "circle", StringComparison.Ordinal)
                ? RecordCameraShape.circle
                : RecordCameraShape.square,
            CameraMirrored = MirrorBox.IsChecked == true,
            MouseHighlight = HighlightBox.IsChecked == true,
            ShowCursor = CursorBox.IsChecked == true,
            RecordClicks = ClicksBox.IsChecked == true,
            RecordScroll = ScrollBox.IsChecked == true,
            Keystroke = KeysBox.IsChecked == true,
            MouseHighlightRadius = (int)Math.Round(RadiusSlider.Value),
            Codec = (TagOf(CodecBox) as string) switch
            {
                "h264" => RecordCodec.h264,
                "hevc" => RecordCodec.hevc,
                _ => RecordCodec.auto,
            },
            TrimAfterRecord = TrimBox.IsChecked == true,
            AutoHideToolbar = AutoHideBox.IsChecked == true,
        }.Normalized();

        // 颜色 / 键过滤 / 后期处理 / 声音增益这些只在面板上改的项：窗口不管它们，原样带过来
        var previous = _runtime ?? AppState.Record;
        options.LaserColor = previous.LaserColor;
        options.ClickColor = previous.ClickColor;
        options.RecordNumberKeys = previous.RecordNumberKeys;
        options.RecordLetterKeys = previous.RecordLetterKeys;
        options.RecordControlKeys = previous.RecordControlKeys;
        options.PreserveShortcuts = previous.PreserveShortcuts;
        options.PostProcessing = previous.PostProcessing;
        options.PostProcessingProgressBar = previous.PostProcessingProgressBar;
        options.CameraOffsetLeft = previous.CameraOffsetLeft;
        options.CameraOffsetBottom = previous.CameraOffsetBottom;
        options.MicrophoneGainDb = previous.MicrophoneGainDb;
        options.SystemAudioGainDb = previous.SystemAudioGainDb;

        try
        {
            SettingsStore.Modify(file => file.Record = options);
            AppState.Record = options;
            _runtime?.CopyFrom(options);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "保存录屏选项失败", ex.Message);
            return;
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static object? TagOf(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag;

    private static void SelectTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        box.SelectedIndex = 0;
    }
}
