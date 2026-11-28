using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Native;

namespace LTools.Views;

/// <summary>编辑一条热键绑定：组合键录制 + 选动作 + 生效范围 / 名单 + 备注。</summary>
public partial class HotkeyEditDialog : Window
{
    private readonly HotkeyBinding _binding;

    private bool _capturing;
    private bool _capsPressed;
    private string _chord = "";
    private string _beforeCapture = "";
    private string _actionId = "";

    private HotkeyEditDialog(HotkeyBinding binding)
    {
        InitializeComponent();
        _binding = binding;
        _actionId = binding.ActionId;
        _chord = HotkeyChord.Normalize(binding.Chord);

        SetChord(_chord);
        ActionBox.Text = ActionName(_actionId);
        UpdateParamButton();
        NoteBox.Text = binding.Note;

        ScopeEditor.SetOptions("跟随组", "全部窗口（忽略默认名单）");
        ScopeEditor.Load(binding.Scope, binding.Filter);

        ChordBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ChordBox.Focus();
            BeginCapture();
            e.Handled = true;
        };
        ChordBox.GotKeyboardFocus += (_, _) => BeginCapture();
        ChordBox.LostKeyboardFocus += (_, _) => EndCapture(commit: true);
        ChordBox.PreviewKeyDown += OnChordKeyDown;
        ChordBox.PreviewKeyUp += (_, e) =>
        {
            if (!_capturing) return;
            // 松开 CapsLock 就不再把它算作前缀（避免「按住又松开之后再按 H」录进 CapsLock）
            Key up = e.Key == Key.System ? e.SystemKey : e.Key;
            if (up == Key.CapsLock) _capsPressed = false;
            e.Handled = true;
        };

        Closed += (_, _) =>
        {
            if (_capturing) EndCapture(commit: true);
        };
    }

    /// <summary>录入结果（仅 <see cref="Window.DialogResult"/> 为 true 时有效）。</summary>
    public HotkeyBinding Result { get; private set; } = new();

    /// <summary>编辑一条已有绑定（<paramref name="binding"/> 会被克隆，取消时不影响原对象）。</summary>
    public static HotkeyBinding? Show(Window? owner, HotkeyBinding binding)
    {
        var dlg = new HotkeyEditDialog(binding);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ============ 组合键录制 ============

    private void BeginCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _capsPressed = false;
        _beforeCapture = _chord;
        // 录入期间别让刚录的组合键触发动作。设置窗口在宿主里、钩子在提权进程，
        // PushSuspend 会把它推过去（同一个让路通道，见 HotkeyCapture.Publish）。
        HotkeyCapture.PushSuspend();
        ChordBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        ChordBox.Text = "请按下组合键…";
    }

    private void EndCapture(bool commit)
    {
        if (!_capturing) return;
        _capturing = false;
        HotkeyCapture.PopSuspend();
        ChordBox.ClearValue(Control.BorderBrushProperty);
        SetChord(commit ? _chord : _beforeCapture);
    }

    private void OnChordKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            e.Handled = true;
            EndCapture(commit: false);
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None && !CapsDown)
        {
            e.Handled = true;
            SetChord("");
            EndCapture(commit: true);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            UpdatePreview();
            return;
        }

        if (key == Key.CapsLock)
        {
            e.Handled = true;
            _capsPressed = true;
            UpdatePreview();
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        e.Handled = true;
        bool caps = CapsDown;
        string chord = BuildChord(caps, Keyboard.Modifiers, (ushort)vk);
        if (!HotkeyChord.TryParse(chord, out var parsed, out string error))
        {
            ShowChordError(error);
            return;
        }

        ShowChordError(null);
        SetChord(parsed.ToText());
        EndCapture(commit: true);
    }

    private void UpdatePreview()
    {
        var parts = new List<string>();
        if (CapsDown) parts.Add("CapsLock");
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        ChordBox.Text = parts.Count > 0 ? string.Join("+", parts) + "+…" : "请按下组合键…";
    }

    /// <summary>
    /// 现在 CapsLock 算不算按着：录入框收到过它的按下（<see cref="_capsPressed"/>）、
    /// 或者按下发生在开始录入之前（物理状态还在）。执行器里另有 <see cref="AppState.CapsLockIsDown"/>，
    /// 但设置窗口在宿主进程，那边永远是 false，所以要以物理状态为准。
    /// </summary>
    private bool CapsDown => _capsPressed || AppState.CapsLockIsDown || KeyState.IsDown(Win32.VkCapital);

    private static string BuildChord(bool caps, ModifierKeys mods, ushort vk)
    {
        var parts = new List<string>();
        if (caps) parts.Add("capslock");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("win");
        parts.Add(KeyStroke.Format(ModifierKeys.None, vk));
        return string.Join("+", parts);
    }

    private void SetChord(string chord)
    {
        _chord = HotkeyChord.Normalize(chord);
        ChordBox.Text = _chord.Length == 0 ? "" : HotkeyChord.Format(_chord);
    }

    private void ShowChordError(string? error)
    {
        ChordError.Text = error ?? "";
        ChordError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============ 其它字段 ============

    private void PickAction_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ActionPoolPicker(this, "选择动作 / 功能", _actionId);
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.Result)) return;
        _actionId = picker.Result!;
        ActionBox.Text = ActionName(_actionId);
        UpdateParamButton();
    }

    /// <summary>内置功能带参数时显示「参数…」：就地改这一条绑定的参数。</summary>
    private void EditParams_Click(object sender, RoutedEventArgs e)
    {
        if (!FeatureCatalog.IsFeatureRef(_actionId)) return;
        string? updated = FeatureParamsDialog.Show(this, _actionId);
        if (string.IsNullOrEmpty(updated)) return;
        _actionId = updated;
        ActionBox.Text = ActionName(_actionId);
        UpdateParamButton();
    }

    private void UpdateParamButton()
    {
        var def = FeatureCatalog.Find(FeatureRef.IdOf(_actionId));
        ParamBtn.Visibility = def is { HasParams: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>动作名 / 内置功能名（功能再附上当前参数摘要）。</summary>
    private static string ActionName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        if (!BindingRef.Exists(id)) return $"({id}) 已不存在";
        return BindingRef.Describe(id);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture(commit: true);

        if (_chord.Length == 0)
        {
            ShowChordError("请先录入一个组合键（点输入框后直接按）");
            return;
        }
        if (!HotkeyChord.TryParse(_chord, out var chord, out string error))
        {
            ShowChordError(error);
            return;
        }
        if (string.IsNullOrWhiteSpace(_actionId) || !BindingRef.Exists(_actionId))
        {
            ConfirmDialog.Info(this, "热键", "请选择一个存在的动作或功能。");
            return;
        }

        // 单键：允许，但先说清代价——命中就吞键，等于把这个按键从所有程序里抢走。
        if (chord.IsBare)
        {
            bool ok = ConfirmDialog.Confirm(this, "单键热键",
                $"「{chord.Display()}」没有修饰键：\n\n" +
                "· 它会在所有程序里生效，并且把这个按键吞掉——在记事本、聊天框里打这个字符，" +
                "字符进不去，反而会触发动作；\n" +
                "· 只想在某个程序里用，就把下面的「生效范围」设成白名单；\n" +
                "· F1–F24、Pause 这类不产生字符的键最安全。\n\n" +
                "确定保存这条单键热键？",
                danger: true, okText: "保存单键");
            if (!ok) return;
        }

        var scope = ScopeEditor.SelectedScope;
        Result = new HotkeyBinding
        {
            Id = _binding.Id,
            Enabled = _binding.Enabled,
            Chord = chord.ToText(),
            ActionId = _actionId,
            Note = NoteBox.Text.Trim(),
            Scope = scope,
            Filter = ScopeEditor.SelectedFilter,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture(commit: false);
        DialogResult = false;
    }
}
