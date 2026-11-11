using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;

namespace LTools.Views;

/// <summary>编辑一条快捷键绑定：组合键录制 + 选动作 + 生效范围 / 名单 + 备注。</summary>
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

        SelectScope(binding.Scope);
        var filter = binding.Filter ?? new AppFilter();
        ProcessBox.Text = string.Join(Environment.NewLine, filter.Processes);
        TitleBox.Text = string.Join(Environment.NewLine, filter.Titles);
        ClassBox.Text = string.Join(Environment.NewLine, filter.Classes);
        UpdateFilterVisibility();

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
            if (_capturing) e.Handled = true;
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
        HotkeyRegistry.Suspended = true;      // 录入期间别让刚录的组合键触发动作
        ChordBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        ChordBox.Text = "请按下组合键…";
    }

    private void EndCapture(bool commit)
    {
        if (!_capturing) return;
        _capturing = false;
        HotkeyRegistry.Suspended = false;
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

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None && !AppState.CapsLockIsDown)
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
        bool caps = _capsPressed || AppState.CapsLockIsDown;
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
        if (_capsPressed || AppState.CapsLockIsDown) parts.Add("CapsLock");
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        ChordBox.Text = parts.Count > 0 ? string.Join("+", parts) + "+…" : "请按下组合键…";
    }

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

    private void SelectScope(HotkeyScope scope)
    {
        string tag = scope.ToString();
        foreach (ComboBoxItem item in ScopeBox.Items)
        {
            if ((item.Tag as string) == tag) { ScopeBox.SelectedItem = item; return; }
        }
        ScopeBox.SelectedIndex = 0;
    }

    private void Scope_Changed(object sender, SelectionChangedEventArgs e) => UpdateFilterVisibility();

    // 名单三行都能从系统里点选（与触发规则「作用范围」共用同一套），不用手抄进程名 / 标题 / 类名。
    private void ScopeProcess_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ProcessBox, SystemPickerDialog.PickProcess(this));

    private void ScopeTitle_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(TitleBox, SystemPickerDialog.PickWindow(this)?.Title);

    private void ScopeClass_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ClassBox, SystemPickerDialog.PickWindow(this)?.ClassName);

    private void UpdateFilterVisibility()
    {
        string tag = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "inherit";
        FilterPanel.Visibility = tag is "whitelist" or "blacklist" ? Visibility.Visible : Visibility.Collapsed;
    }

    private HotkeyScope SelectedScope()
    {
        string tag = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "inherit";
        return Enum.TryParse<HotkeyScope>(tag, out var scope) ? scope : HotkeyScope.inherit;
    }

    private static List<string> Lines(TextBox box) => box.Text
        .Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();

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
            MessageBox.Show(this, "请选择一个存在的动作或功能。", "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 单键：允许，但先说清代价——命中就吞键，等于把这个按键从所有程序里抢走。
        if (chord.IsBare)
        {
            bool ok = ConfirmDialog.Confirm(this, "单键快捷键",
                $"「{chord.Display()}」没有修饰键：\n\n" +
                "· 它会在所有程序里生效，并且把这个按键吞掉——在记事本、聊天框里打这个字符，" +
                "字符进不去，反而会触发动作；\n" +
                "· 只想在某个程序里用，就把下面的「生效范围」设成白名单；\n" +
                "· F1–F24、Pause 这类不产生字符的键最安全。\n\n" +
                "确定保存这条单键快捷键？",
                danger: true, okText: "保存单键");
            if (!ok) return;
        }

        var scope = SelectedScope();
        Result = new HotkeyBinding
        {
            Id = _binding.Id,
            Enabled = _binding.Enabled,
            Chord = chord.ToText(),
            ActionId = _actionId,
            Note = NoteBox.Text.Trim(),
            Scope = scope,
            Filter = scope is HotkeyScope.whitelist or HotkeyScope.blacklist
                ? new AppFilter
                {
                    Processes = Lines(ProcessBox),
                    Titles = Lines(TitleBox),
                    Classes = Lines(ClassBox),
                }
                : null,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing) EndCapture(commit: false);
        DialogResult = false;
    }
}
