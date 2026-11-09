using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Views.Controls;

/// <summary>
/// 设置 → 键鼠宏：录制入口 + 已录制的宏 + 回放默认值。
/// </summary>
/// <remarks>
/// <para>
/// 一个宏就是一个「1 步 · 回放宏」的动作（<see cref="MacroAction"/>），所以这里只列宏文件，
/// 面板 / 快捷键 / 悬浮上的那一项由录制完成时自动挂出来；删除宏会连带删掉引用它的动作。
/// </para>
/// <para>
/// 录制从本页发起时窗口会先藏起来（录制期间钩子透明，窗口上的点击也会被录进去），
/// 停止后自动显示回来并刷新列表。改设置立即落 <c>configs/macros.json</c> 并重载
/// <see cref="MacroSettings"/>，钩子下一次按键就是新值。
/// </para>
/// </remarks>
public partial class MacroView : UserControl
{
    private readonly ObservableCollection<MacroRow> _rows = new();

    private bool _loading;
    private bool _loaded;
    private bool _hooked;
    private bool _hiddenForRecording;

    public MacroView()
    {
        InitializeComponent();
        MacroList.ItemsSource = _rows;

        HotkeyCapture.Attach(RecordHotkeyBox, raw => Save(m => m.RecordHotkey = raw));
        HotkeyCapture.Attach(PlayHotkeyBox, raw => Save(m => m.PlaybackStopHotkey = raw));

        Loaded += (_, _) =>
        {
            // Loaded 可能来多次（窗口隐藏再显示）：事件只挂一次，否则一条录制会刷两遍列表。
            if (!_hooked)
            {
                _hooked = true;
                MacroRecorder.RecordingStarted += OnRecordingStarted;
                MacroRecorder.RecordingStopped += OnRecordingStopped;
                MacroRecorder.MacroSaved += OnMacroSaved;
            }
            if (_loaded) return;
            _loaded = true;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            _hooked = false;
            MacroRecorder.RecordingStarted -= OnRecordingStarted;
            MacroRecorder.RecordingStopped -= OnRecordingStopped;
            MacroRecorder.MacroSaved -= OnMacroSaved;
        };
    }

    // ============ 载入 ============

    /// <summary>从配置与宏目录重建整页（切到本页 / 导入配置 / 录制结束后调用）。</summary>
    public void Reload()
    {
        var m = ConfigStore.LoadConfig().Macros ?? new MacroConfig();

        _loading = true;
        try
        {
            EnabledCheck.IsChecked = m.Enabled;
            HotkeyCapture.SetHotkey(RecordHotkeyBox, m.RecordHotkey);
            HotkeyCapture.SetHotkey(PlayHotkeyBox, m.PlaybackStopHotkey);
            CountdownBox.Text = m.CountdownMs.ToString();
            SpeedBox.Text = m.DefaultSpeed.ToString("0.##");
            LoopsBox.Text = m.Loops.ToString();
            SampleMsBox.Text = m.MouseSampleMs.ToString();
            SamplePxBox.Text = m.MouseSamplePx.ToString();
            MaxEventsBox.Text = m.MaxEvents.ToString();
        }
        finally { _loading = false; }

        MacroStore.Invalidate();
        PopulateRows();
        UpdateRecordState();
    }

    private void PopulateRows()
    {
        _rows.Clear();
        foreach (var meta in MacroStore.List())
            _rows.Add(new MacroRow(meta));

        bool empty = _rows.Count == 0;
        MacroList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRecordState()
    {
        bool recording = MacroRecorder.IsRecording;
        RecordButton.Content = recording ? "■ 停止录制" : "● 录制新宏";
        RecordHint.Text = recording
            ? $"正在录制 · {MacroRecorder.EventCount} 个事件 · 按 {MacroSettings.RecordHotkeyDisplay} 停止"
            : $"按 {MacroSettings.RecordHotkeyDisplay} 也可以开始 / 停止；录完会自动变成面板上的动作。";
    }

    // ============ 录制 ============

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (MacroRecorder.IsRecording)
        {
            MacroRecorder.Stop();
            return;
        }

        HideForRecording();
        MacroRecorder.Start();
        if (!MacroRecorder.IsRecording) ShowAfterRecording();   // 被拒绝（没启用 / 正在回放）就别把窗口藏起来
        UpdateRecordState();
    }

    private void OnRecordingStarted()
    {
        OnUi(UpdateRecordState);
        // 从热键 / 托盘开始时窗口还开着：同样让开，免得录到自己。
        OnUi(HideForRecording);
    }

    private void OnRecordingStopped()
    {
        OnUi(() =>
        {
            ShowAfterRecording();
            UpdateRecordState();
        });
    }

    private void OnMacroSaved(string id)
    {
        // 保存完成在后台线程：等它落盘后再刷新列表（此时元数据里已经有这条宏）。
        OnUi(() => { MacroStore.Invalidate(); PopulateRows(); });
    }

    private void HideForRecording()
    {
        if (_hiddenForRecording) return;
        var window = Window.GetWindow(this);
        if (window == null || !window.IsVisible) return;
        _hiddenForRecording = true;
        try { window.Hide(); } catch { /* 隐藏失败不影响录制 */ }
    }

    private void ShowAfterRecording()
    {
        if (!_hiddenForRecording) return;
        _hiddenForRecording = false;
        var window = Window.GetWindow(this);
        if (window == null) return;
        try
        {
            window.Show();
            window.Activate();
        }
        catch { /* 恢复失败不影响设置 */ }
    }

    // ============ 宏列表操作 ============

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box || box.DataContext is not MacroRow row) return;
        string name = box.Text.Trim();
        if (name.Length == 0 || name == row.OriginalName) return;

        MacroStore.Rename(row.Id, name);
        row.OriginalName = name;
        ToastService.Show($"宏已改名为「{name}」，引用它的动作也一起改了", "键鼠宏", 2500,
            ToastKind.success, key: "macro:rename");
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;

        if (MacroPlayer.IsPlaying)
        {
            ToastService.Show("已有宏正在回放", "键鼠宏", 2500, ToastKind.warning, key: "macro:play:hint");
            return;
        }
        if (MacroRecorder.IsRecording)
        {
            ToastService.Show("正在录制，先停止录制再试放", "键鼠宏", 2500, ToastKind.warning, key: "macro:play:hint");
            return;
        }

        var macro = MacroStore.Load(row.Id);
        if (macro == null)
        {
            ToastService.Show("宏文件不存在（可能被手工删掉了），请重新录制", "键鼠宏",
                ToastService.StickyMs, ToastKind.error, key: "macro:play:hint");
            return;
        }

        // 从本页试放时窗口让开，否则第一下点击会落在自己身上。
        HideForRecording();
        Task.Run(() =>
        {
            try
            {
                MacroPlayer.PlaySync(macro, MacroStore.PlaybackOf(row.Id));
            }
            catch (Exception ex)
            {
                AppLog.Error("Macro", $"试放失败「{macro.Name}」", ex);
                ToastService.Show("试放失败：" + ex.Message, "键鼠宏", ToastService.StickyMs,
                    ToastKind.error, key: "macro:play:hint");
            }
            finally
            {
                OnUi(ShowAfterRecording);
            }
        });
    }

    /// <summary>单个宏的回放设置（倒计时 / 速度 / 循环）：只改这一个宏，留空 = 跟随默认。</summary>
    private void PlaybackSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;

        var window = Window.GetWindow(this);
        var meta = MacroStore.MetaOf(row.Id);
        var dialog = new MacroPlaybackDialog(row.Id, row.Name, meta)
        {
            Owner = window,
            Topmost = window?.Topmost ?? false,
        };
        dialog.ShowDialog();

        if (dialog.Result == null) return;   // 取消
        MacroStore.Invalidate();
        PopulateRows();
        ToastService.Show(dialog.Result.Value.Any
                ? $"「{row.Name}」的回放参数已单独设置"
                : $"「{row.Name}」已改回跟随默认",
            "键鼠宏", 2500, ToastKind.success, key: "macro:playback");
    }

    private void Rerecord_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;
        if (MacroRecorder.IsRecording) return;

        HideForRecording();
        MacroRecorder.StartInto(row.Id, row.Name);
        if (!MacroRecorder.IsRecording) ShowAfterRecording();
        UpdateRecordState();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;

        var window = Window.GetWindow(this);
        string message = $"删除宏「{row.Name}」？\n\n引用它的动作（面板 / 快捷键上的那一项）也会一起删掉。";
        if (!ConfirmDialog.Confirm(window, "删除宏", message, danger: true, okText: "删除")) return;

        int removed = MacroStore.Delete(row.Id);
        MacroStore.Invalidate();
        PopulateRows();
        ToastService.Show(removed > 0
            ? $"已删除宏「{row.Name}」与 {removed} 个动作"
            : $"已删除宏「{row.Name}」", "键鼠宏", 3000, ToastKind.success, key: "macro:delete");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MacroStore.Ensure();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = MacroStore.Dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show("打不开宏目录：" + ex.Message, "键鼠宏", ToastService.StickyMs, ToastKind.error);
        }
    }

    // ============ 设置 ============

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool enabled = EnabledCheck.IsChecked == true;
        Save(m => m.Enabled = enabled);
        UpdateRecordState();
    }

    /// <summary>数字框：失焦即按上下限夹回并写盘，填错就还原成当前值。</summary>
    private void Number_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;
        var m = ConfigStore.LoadConfig().Macros ?? new MacroConfig();

        if (ReferenceEquals(box, CountdownBox))
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) v = m.CountdownMs;
            v = Math.Clamp(v, 0, 10_000);
            box.Text = v.ToString();
            Save(c => c.CountdownMs = v);
        }
        else if (ReferenceEquals(box, SpeedBox))
        {
            if (!double.TryParse(box.Text.Trim(), out double v)) v = m.DefaultSpeed;
            v = Math.Clamp(v, 0.1, 5.0);
            box.Text = v.ToString("0.##");
            Save(c => c.DefaultSpeed = v);
        }
        else if (ReferenceEquals(box, LoopsBox))
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) v = m.Loops;
            v = Math.Clamp(v, 1, 99);
            box.Text = v.ToString();
            Save(c => c.Loops = v);
        }
        else if (ReferenceEquals(box, SampleMsBox))
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) v = m.MouseSampleMs;
            v = Math.Clamp(v, 0, 100);
            box.Text = v.ToString();
            Save(c => c.MouseSampleMs = v);
        }
        else if (ReferenceEquals(box, SamplePxBox))
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) v = m.MouseSamplePx;
            v = Math.Clamp(v, 0, 20);
            box.Text = v.ToString();
            Save(c => c.MouseSamplePx = v);
        }
        else if (ReferenceEquals(box, MaxEventsBox))
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) v = m.MaxEvents;
            v = Math.Clamp(v, 1_000, 2_000_000);
            box.Text = v.ToString();
            Save(c => c.MaxEvents = v);
        }
    }

    /// <summary>只写 <c>configs/macros.json</c>，然后把运行期设置重新载入（钩子立即生效）。</summary>
    private static void Save(Action<MacroConfig> change)
    {
        try
        {
            MacroFileStore.Modify(file => change(file.Macros));
            MacroSettings.Load(ConfigStore.LoadConfig());
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroView.Save", ex);
            ToastService.Show("保存键鼠宏设置失败：" + ex.Message, "键鼠宏", ToastService.StickyMs,
                ToastKind.error);
        }
    }

    private void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    /// <summary>列表一行（宏元数据 + 界面显示文本）。</summary>
    public sealed class MacroRow
    {
        public MacroRow(MacroMeta meta)
        {
            Id = meta.Id;
            Name = meta.Name;
            OriginalName = meta.Name;
            Meta = MacroStore.Exists(meta.Id)
                ? $"{meta.DurationText} · {meta.EventCount} 事件 · {meta.UpdatedAt}{PlaybackSuffix(meta)}"
                : $"{meta.DurationText} · 宏文件缺失（重新录制或删除）";
        }

        /// <summary>
        /// 单独设过的回放参数（没有就什么都不加）：让「这个宏和别的宏不一样」在列表里直接看得见，
        /// 不用逐个点开「回放设置…」才知道。
        /// </summary>
        private static string PlaybackSuffix(MacroMeta meta)
        {
            var parts = new List<string>();
            if (meta.Speed > 0) parts.Add($"{meta.Speed:0.##}×");
            if (meta.Loops is int loops && loops < 0) parts.Add("无限循环");
            else if (meta.Loops is int n and > 0) parts.Add($"{n} 次");
            if (meta.CountdownMs is int c and > 0) parts.Add($"倒计时 {c} ms");
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }

        public string Id { get; }
        public string Name { get; set; }

        /// <summary>改名前的名字：失焦时用来判断「真的改了」。</summary>
        public string OriginalName { get; set; }

        public string Meta { get; }
    }
}
