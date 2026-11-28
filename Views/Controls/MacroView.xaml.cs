using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Views.Controls;

/// <summary>
/// 设置 → 键鼠宏：二级导航的两页（「录制的宏」/「设置」），由 <see cref="ShowPage"/> 切可见性。
/// </summary>
/// <remarks>
/// <para>
/// 一个宏就是一个「1 步 · 回放宏」的动作（<see cref="MacroAction"/>），所以「录制的宏」页只列宏文件，
/// 面板 / 快捷键 / 悬浮上的那一项由录制完成时自动挂出来；删除宏会连带删掉引用它的动作。
/// </para>
/// <para>
/// 「录制的宏」页是 Grid：页头固定、<c>MacroList</c> 占满 <c>*</c> 行自己滚，**不套外层 ScrollViewer**。
/// 原来整页一个 ScrollViewer + 被 StackPanel 撑开的 ListBox，内层 ScrollViewer 会把 MouseWheel 标
/// Handled 吞掉、页面滚不动（用户报过）；拆页后没有嵌套，滚轮由列表自己处理。设置页全是单行输入 /
/// 复选 / Expander，没有内层列表控件（ComboBox 另由 ComboBoxAssist 全局转发），正常滚动。
/// </para>
/// <para>
/// 窗口在宿主里（M3-5c-3b）：录制 / 试放 / 改名 / 删宏走 <see cref="MacroRecording"/> 与
/// <see cref="MacroWrites"/>（执行器那边要钩子、动作属主也在那边），列表与设置项是直接读写
/// <c>configs/macros.json</c>——宿主是这个文件的物理写者，写完执行器靠 <c>config.changed</c> 重读。
/// </para>
/// <para>
/// 录制一开始窗口就让开（<see cref="MacroRecording.Started"/> 是广播，热键发起的也算），
/// 停止后自动显示回来并刷新列表；试放同理，跑完由 <see cref="MacroWrites.Played"/> 回来。
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

        Loaded += (_, _) =>
        {
            // Loaded 可能来多次（窗口隐藏再显示）：事件只挂一次，否则一条录制会刷两遍列表。
            if (!_hooked)
            {
                _hooked = true;
                MacroRecording.Started += OnRecordingStarted;
                MacroRecording.Stopped += OnRecordingStopped;
                MacroRecording.Saved += OnMacroSaved;
                MacroWrites.Played += OnMacroPlayed;
            }
            if (_loaded) return;
            _loaded = true;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            _hooked = false;
            MacroRecording.Started -= OnRecordingStarted;
            MacroRecording.Stopped -= OnRecordingStopped;
            MacroRecording.Saved -= OnMacroSaved;
            MacroWrites.Played -= OnMacroPlayed;
        };
    }

    /// <summary>二级导航切子页：「录制的宏」(list) /「设置」(settings)，只切可见性，不重建数据。</summary>
    public void ShowPage(string page)
    {
        bool list = page != "settings";
        Page_List.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        Page_Settings.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 设置搜索用：两个子页各自的逻辑树根。**不按当前可见性过滤**——索引要能搜到没打开的那一页，
    /// 页面本身的 Visibility 由 <see cref="ShowPage"/> 管，跳转时再切过去。
    /// </summary>
    internal IEnumerable<(string Tag, FrameworkElement Root)> SearchRoots()
    {
        yield return ("macros-list", Page_List);
        yield return ("macros-settings", Page_Settings);
    }

    // ============ 载入 ============

    /// <summary>从配置与宏目录重建整页（切到本页 / 导入配置 / 录制结束后调用）。</summary>
    public void Reload()
    {
        _loaded = true;
        var m = ConfigStore.LoadConfig().Macros ?? new MacroConfig();

        _loading = true;
        try
        {
            EnabledCheck.IsChecked = m.Enabled;
            CountdownBox.Text = m.CountdownMs.ToString();
            SpeedBox.Text = m.DefaultSpeed.ToString("0.##");
            LoopsBox.Text = m.Loops.ToString();
            PlaybackContentBox.SelectedIndex = MacroPlaybackContents.Parse(m.PlaybackContent) switch
            {
                MacroPlaybackContent.Keyboard => 1,
                MacroPlaybackContent.Mouse => 2,
                _ => 0,
            };
            SampleMsBox.Text = m.MouseSampleMs.ToString();
            SamplePxBox.Text = m.MouseSamplePx.ToString();
            MaxEventsBox.Text = m.MaxEvents.ToString();
        }
        finally { _loading = false; }

        MacroStore.Invalidate();
        PopulateRows();
        UpdateRecordState();
    }

    /// <summary>
    /// 切到本页时调用：已建页且数据没变就不重建（首次建页时 Loaded 与 ApplyNavPage 只让
    /// 第一次真正 Reload）；数据真变时（导入 / 录制结束 / 删除）调用方仍直接调 <see cref="Reload"/>。
    /// </summary>
    public void RefreshIfNeeded()
    {
        if (!_loaded) Reload();
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
        bool recording = MacroRecording.IsRecording;
        RecordButton.Content = recording ? "■ 停止录制" : "● 录制新宏";
        RecordHint.Text = recording
            ? $"正在录制 · 按 {MacroSettings.RecordHotkeyDisplay} 停止"
            : $"按 {MacroSettings.RecordHotkeyDisplay} 也可以开始 / 停止；录完会自动变成面板上的动作。";
    }

    // ============ 录制 ============

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (MacroRecording.IsRecording)
        {
            MacroRecording.Stop();
            return;
        }

        HideForRecording();
        if (!MacroRecording.StartInto(null, null, out string? error))
        {
            // 被拒绝（没启用 / 正在回放 / 有动作在跑）时执行器已经弹过提示，这里只恢复窗口。
            ShowAfterRecording();
            if (!string.IsNullOrEmpty(error))
                ToastService.Show(error, "键鼠宏", ToastService.LongMs, ToastKind.error, key: "macro:record:hint");
        }
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
        // 录制也在执行器里做，宿主这份元数据缓存必须作废才读得到新值。
        OnUi(() => { MacroStore.Invalidate(); PopulateRows(); });
    }

    /// <summary>试放跑完（可能与失败原因一起来）：把藏起来的窗口显示回来。</summary>
    private void OnMacroPlayed(string? error) => OnUi(ShowAfterRecording);

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

        if (!MacroWrites.Rename(row.Id, name, out string? error))
        {
            box.Text = row.OriginalName;
            ToastService.Show("改名失败：" + error, "键鼠宏", ToastService.LongMs, ToastKind.error, key: "macro:rename");
            return;
        }

        row.OriginalName = name;
        row.Name = name;
        ToastService.Show($"宏已改名为「{name}」，引用它的动作也一起改了", "键鼠宏", 2500,
            ToastKind.success, key: "macro:rename");
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;

        // 从本页试放时窗口让开，否则第一下点击会落在自己身上。
        HideForRecording();
        if (MacroWrites.Play(row.Id, out string? error)) return;

        ShowAfterRecording();
        ToastService.Show(error ?? "没能开始试放", "键鼠宏", ToastService.LongMs, ToastKind.error,
            key: "macro:play:hint");
    }

    /// <summary>单个宏的回放设置（倒计时 / 速度 / 循环 / 回放内容）：只改这一个宏，留空 = 跟随默认。</summary>
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
        if (MacroRecording.IsRecording) return;

        HideForRecording();
        if (!MacroRecording.StartInto(row.Id, row.Name, out string? error))
        {
            ShowAfterRecording();
            if (!string.IsNullOrEmpty(error))
                ToastService.Show(error, "键鼠宏", ToastService.LongMs, ToastKind.error, key: "macro:record:hint");
        }
        UpdateRecordState();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroRow row) return;

        var window = Window.GetWindow(this);
        string message = $"删除宏「{row.Name}」？\n\n引用它的动作也会一起删掉，面板磁贴、快捷键绑定、触发规则与悬浮项中的引用会一并清除。";
        if (!ConfirmDialog.Confirm(window, "删除宏", message, danger: true, okText: "删除")) return;

        if (!MacroWrites.Delete(row.Id, out int removed, out string? error))
        {
            ToastService.Show("删除失败：" + error, "键鼠宏", ToastService.LongMs, ToastKind.error, key: "macro:delete");
            return;
        }

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
            ToastService.Show("打不开宏目录：" + ex.Message, "键鼠宏", ToastService.LongMs, ToastKind.error);
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

    /// <summary>「回放内容」下拉：选中即写盘（与其它设置同一处 <see cref="Save"/>，运行期立即生效）。</summary>
    private void PlaybackContent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string id = (PlaybackContentBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        Save(c => c.PlaybackContent = id);
    }

    /// <summary>只写 <c>configs/macros.json</c>，然后把运行期设置重新载入（钩子立即生效）。</summary>
    private static void Save(Action<MacroConfig> change)
    {
        try
        {
            MacroSettings.SaveAndReload(change);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroView.Save", ex);
            ToastService.Show("保存键鼠宏设置失败：" + ex.Message, "键鼠宏", ToastService.LongMs,
                ToastKind.error);
        }
    }

    /// <summary>去「热键 → 键鼠宏」改录制 / 停止运行热键（本页不再放录入框，避免两处各存一份）。</summary>
    private void OpenHotkeySettings_Click(object sender, RoutedEventArgs e)
        => ConfigHelper.OpenHotkeys("macro");

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
                : $"{meta.DurationText} · 宏文件已不存在（可能随动作一起删除）";
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
            if (!string.IsNullOrWhiteSpace(meta.Content))
            {
                var content = MacroPlaybackContents.Parse(meta.Content);
                if (content != MacroPlaybackContent.All) parts.Add(MacroPlaybackContents.Label(content));
            }
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }

        public string Id { get; }
        public string Name { get; set; }

        /// <summary>改名前的名字：失焦时用来判断「真的改了」。</summary>
        public string OriginalName { get; set; }

        public string Meta { get; }
    }
}
