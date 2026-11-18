using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>智能选区的三档（照上游 <c>SmartSelectionMode</c>）。</summary>
internal enum SmartSelectionMode
{
    /// <summary>窗口 + 子控件（滚轮在两级之间切换）。</summary>
    WindowSubElement,

    /// <summary>只认整扇窗口。</summary>
    Window,

    /// <summary>不做智能识别，只能手拖框选。</summary>
    Disabled,
}

/// <summary>切换元素层级时的过渡动画（照上游 <c>SelectionTransitionAnimation</c>）。</summary>
internal enum SelectionTransition
{
    /// <summary>柔和：101ms。</summary>
    Soft,

    /// <summary>快速：67ms。</summary>
    Fast,

    /// <summary>不做动画，直接落位。</summary>
    None,
}

/// <summary>一个选区快速集：屏幕物理像素几何 + 提交时的外观 + 当时锁没锁比例。</summary>
internal sealed record SelectionPreset(string Name, Int32Rect Area, SelectionStyle Style,
    bool LockAspect = false)
{
    public bool IsUsable => Area.Width >= 2 && Area.Height >= 2;
}

/// <summary>
/// 选区设置的读写：进程内缓存 + 300ms 防抖，只写 <c>configs/ui.json</c> 的 <c>CaptureSelection</c>。
/// </summary>
/// <remarks>
/// 和 <see cref="CaptureStyleStore"/> 同一套节奏：改外观是滚轮一下一下改的，每下都走
/// 「读整份聚合 + 原子写」会卡住与鼠标钩子同线程的 UI。关窗 / 退出前调 <see cref="Flush"/>。
/// </remarks>
internal static class SelectionStore
{
    private const int SaveDelayMs = 300;

    private static readonly object Gate = new();
    private static CaptureSelectionFile? _cached;
    private static System.Threading.Timer? _timer;
    private static bool _dirty;

    /// <summary>当前外观（首次访问读盘）。</summary>
    public static SelectionStyle Style
    {
        get
        {
            lock (Gate) return ToStyle(Load());
        }
    }

    /// <summary>比例是否锁着。</summary>
    public static bool AspectLocked
    {
        get
        {
            lock (Gate)
            {
                var file = Load();
                return SelectionAspects.FromId(file.AspectPreset) != SelectionAspect.Free ||
                       file.LockAspect is true;
            }
        }
    }

    /// <summary>上次选的比例预设（自由 = 没配预设）。</summary>
    public static SelectionAspect Aspect
    {
        get
        {
            lock (Gate) return SelectionAspects.FromId(Load().AspectPreset);
        }
    }

    public static SmartSelectionMode SmartSelection
    {
        get
        {
            lock (Gate) return ParseSmart(Load().SmartSelection);
        }
    }

    public static SelectionTransition Transition
    {
        get
        {
            lock (Gate) return ParseTransition(Load().SelectionTransition);
        }
    }

    /// <summary>「上次选区」快速集（没有 = null）。</summary>
    public static SelectionPreset? Previous
    {
        get
        {
            lock (Gate)
            {
                var preset = Load().Previous;
                return preset == null ? null : ToPreset(preset);
            }
        }
    }

    /// <summary>用户存的快速集（只读副本）。</summary>
    public static IReadOnlyList<SelectionPreset> Presets
    {
        get
        {
            lock (Gate)
            {
                var list = new List<SelectionPreset>();
                foreach (var preset in Load().Presets)
                {
                    if (preset == null || string.IsNullOrWhiteSpace(preset.Name)) continue;
                    list.Add(ToPreset(preset));
                }
                return list;
            }
        }
    }

    /// <summary>换外观（防抖落盘）。</summary>
    public static void SetStyle(SelectionStyle style)
    {
        var clamped = style.Clamp();
        lock (Gate)
        {
            var file = Load();
            file.CornerRadius = clamped.CornerRadius;
            file.ShadowWidth = clamped.ShadowWidth;
            file.ShadowColor = ToolStyleFormat.Hex(clamped.ShadowColor);
            file.BorderWidth = clamped.BorderWidth;
            file.BorderColor = ToolStyleFormat.Hex(clamped.BorderColor);
            file.BorderType = clamped.BorderType == SelectionBorderType.Border ? "border" : "shadow";
            MarkDirty();
        }
    }

    /// <summary>记比例锁 + 预设。</summary>
    public static void SetAspect(bool locked, SelectionAspect aspect)
    {
        lock (Gate)
        {
            var file = Load();
            file.LockAspect = locked;
            file.AspectPreset = locked ? SelectionAspects.Id(aspect) : "free";
            MarkDirty();
        }
    }

    public static void SetSmartSelection(SmartSelectionMode mode)
    {
        lock (Gate)
        {
            Load().SmartSelection = mode switch
            {
                SmartSelectionMode.Window => "window",
                SmartSelectionMode.Disabled => "disabled",
                _ => "window_sub_element",
            };
            MarkDirty();
        }
    }

    public static void SetTransition(SelectionTransition transition)
    {
        lock (Gate)
        {
            Load().SelectionTransition = transition switch
            {
                SelectionTransition.Fast => "fast",
                SelectionTransition.None => "none",
                _ => "soft",
            };
            MarkDirty();
        }
    }

    /// <summary>提交一次选区：写「上次选区」，可选再存成具名快速集。</summary>
    public static void Commit(Int32Rect area, SelectionStyle style, bool lockAspect,
        string? presetName = null)
    {
        if (area.Width < 2 || area.Height < 2) return;

        lock (Gate)
        {
            var file = Load();
            var dto = ToDto("上次选区", area, style, lockAspect);
            file.Previous = dto;

            if (!string.IsNullOrWhiteSpace(presetName))
            {
                file.Presets.RemoveAll(p => string.Equals(p.Name, presetName, StringComparison.Ordinal));
                var named = ToDto(presetName!.Trim(), area, style, lockAspect);
                file.Presets.Add(named);
                if (file.Presets.Count > 30) file.Presets.RemoveAt(0);
            }

            MarkDirty();
        }
    }

    /// <summary>存一个具名快速集（参数框里的「添加」）。</summary>
    public static void AddPreset(string name, Int32Rect area, SelectionStyle style, bool lockAspect)
    {
        if (string.IsNullOrWhiteSpace(name) || area.Width < 2 || area.Height < 2) return;
        Commit(area, style, lockAspect, name);
    }

    /// <summary>删一个具名快速集。</summary>
    public static void RemovePreset(string name)
    {
        lock (Gate)
        {
            var file = Load();
            if (file.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal)) > 0)
                MarkDirty();
        }
    }

    /// <summary>立刻落盘。</summary>
    public static void Flush()
    {
        CaptureSelectionFile? file;
        lock (Gate)
        {
            if (!_dirty) return;
            _dirty = false;
            file = _cached;
        }
        if (file != null) Write(file);
    }

    /// <summary>丢掉缓存（导入备份 / 自检夹具改了 ui.json 之后调）。</summary>
    public static void Forget()
    {
        Flush();
        lock (Gate) _cached = null;
    }

    private static void MarkDirty()
    {
        _dirty = true;
        _timer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(SaveDelayMs, Timeout.Infinite);
    }

    private static CaptureSelectionFile Load()
    {
        if (_cached != null) return _cached;
        try
        {
            string path = ConfigStore.ConfigPath;
            _cached = string.IsNullOrEmpty(path)
                ? new CaptureSelectionFile()
                : UiStore.Read(path).CaptureSelection ?? new CaptureSelectionFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "读取选区设置失败：" + ex.Message);
            _cached = new CaptureSelectionFile();
        }
        return _cached;
    }

    private static void Write(CaptureSelectionFile file)
    {
        try
        {
            void Change(UiStateFile ui) => ui.CaptureSelection = file;

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) UiStore.Modify(Change);
            else UiStore.Modify(ConfigStore.ConfigPath, Change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "保存选区设置失败：" + ex.Message);
        }
    }

    private static SelectionStyle ToStyle(CaptureSelectionFile file)
    {
        if (!ToolStyleFormat.TryParseColor(file.ShadowColor, out var shadow))
            shadow = SelectionStyle.DefaultShadowColor;
        if (!ToolStyleFormat.TryParseColor(file.BorderColor, out var border))
            border = SelectionStyle.DefaultBorderColor;

        return new SelectionStyle
        {
            CornerRadius = file.CornerRadius,
            ShadowWidth = file.ShadowWidth,
            ShadowColor = shadow,
            BorderWidth = file.BorderWidth,
            BorderColor = border,
            BorderType = string.Equals(file.BorderType, "border", StringComparison.OrdinalIgnoreCase)
                ? SelectionBorderType.Border
                : SelectionBorderType.Shadow,
        }.Clamp();
    }

    private static SelectionPreset ToPreset(CaptureSelectionPreset dto)
    {
        if (!ToolStyleFormat.TryParseColor(dto.ShadowColor, out var shadow))
            shadow = SelectionStyle.DefaultShadowColor;
        if (!ToolStyleFormat.TryParseColor(dto.BorderColor, out var border))
            border = SelectionStyle.DefaultBorderColor;

        var style = new SelectionStyle
        {
            CornerRadius = dto.CornerRadius,
            ShadowWidth = dto.ShadowWidth,
            ShadowColor = shadow,
            BorderWidth = dto.BorderWidth,
            BorderColor = border,
            BorderType = string.Equals(dto.BorderType, "border", StringComparison.OrdinalIgnoreCase)
                ? SelectionBorderType.Border
                : SelectionBorderType.Shadow,
        }.Clamp();

        return new SelectionPreset(dto.Name, new Int32Rect(dto.X, dto.Y, dto.Width, dto.Height), style,
            dto.LockAspect is true);
    }

    private static CaptureSelectionPreset ToDto(string name, Int32Rect area, SelectionStyle style,
        bool lockAspect) => new()
    {
        Name = name,
        X = area.X,
        Y = area.Y,
        Width = area.Width,
        Height = area.Height,
        LockAspect = lockAspect,
        CornerRadius = style.CornerRadius,
        ShadowWidth = style.ShadowWidth,
        ShadowColor = ToolStyleFormat.Hex(style.ShadowColor),
        BorderWidth = style.BorderWidth,
        BorderColor = ToolStyleFormat.Hex(style.BorderColor),
        BorderType = style.BorderType == SelectionBorderType.Border ? "border" : "shadow",
    };

    private static SmartSelectionMode ParseSmart(string? value) => value switch
    {
        "window" => SmartSelectionMode.Window,
        "disabled" => SmartSelectionMode.Disabled,
        _ => SmartSelectionMode.WindowSubElement,
    };

    private static SelectionTransition ParseTransition(string? value) => value switch
    {
        "fast" => SelectionTransition.Fast,
        "none" => SelectionTransition.None,
        _ => SelectionTransition.Soft,
    };
}
