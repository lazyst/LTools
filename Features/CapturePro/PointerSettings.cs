using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 指针动作的五项设置（<c>configs/settings.json</c> 的 <c>Screenshot</c> / <c>PinToScreen</c> 两段）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot/double_click_action</c>、<c>screenshot/middle_mouse_button_action</c>、
/// <c>pin_to_screen/double_click_action</c>、<c>pin_to_screen/middle_mouse_button_action</c>、
/// <c>pin_to_screen/confirm_before_destroying_window</c>。前四个依次是覆盖层双击 / 中键、
/// 贴图双击 / 中键；最后一个是「删除贴图前先确认」（默认<b>开</b>，其余默认值见
/// <see cref="PointerActions"/>）。
/// </para>
/// <para>
/// **读盘只发生一次**：贴图的双击 / 中键在鼠标事件里问，每问一次读一遍 JSON 会把光标顶住
/// ——和 <see cref="PinToScreenStore"/> 同一套缓存口径。写盘只写自己这两个域，
/// 写完立刻更新缓存（不必重开贴图）。
/// </para>
/// </remarks>
internal static class PointerSettings
{
    private static readonly object Gate = new();

    private static OverlayPointerAction? _overlayDouble;
    private static OverlayPointerAction? _overlayMiddle;
    private static PinPointerAction? _pinDouble;
    private static PinPointerAction? _pinMiddle;
    private static bool? _confirmDestroy;

    /// <summary>覆盖层里「选择工具 + 双击」的动作（默认复制）。</summary>
    public static OverlayPointerAction OverlayDoubleClick
    {
        get { LoadAll(); lock (Gate) return _overlayDouble!.Value; }
    }

    /// <summary>覆盖层里「选择工具 + 中键」的动作（默认贴到屏幕）。</summary>
    public static OverlayPointerAction OverlayMiddleClick
    {
        get { LoadAll(); lock (Gate) return _overlayMiddle!.Value; }
    }

    /// <summary>贴图双击的动作（默认缩略图模式）。</summary>
    public static PinPointerAction PinDoubleClick
    {
        get { LoadAll(); lock (Gate) return _pinDouble!.Value; }
    }

    /// <summary>贴图中键的动作（默认重置缩放）。</summary>
    public static PinPointerAction PinMiddleClick
    {
        get { LoadAll(); lock (Gate) return _pinMiddle!.Value; }
    }

    /// <summary>删除贴图前是否先确认（默认是；关掉之后删了就找不回来）。</summary>
    public static bool ConfirmBeforeDestroyingWindow
    {
        get { LoadAll(); lock (Gate) return _confirmDestroy!.Value; }
    }

    public static void SetOverlayDoubleClick(string key)
    {
        var action = PointerActions.ParseOverlay(key);
        LoadAll();
        lock (Gate) _overlayDouble = action;
        Save(file => (file.Screenshot ??= new ScreenshotFile()).DoubleClickAction = PointerActions.Key(action));
    }

    public static void SetOverlayMiddleClick(string key)
    {
        var action = PointerActions.ParseOverlay(key);
        LoadAll();
        lock (Gate) _overlayMiddle = action;
        Save(file => (file.Screenshot ??= new ScreenshotFile()).MiddleButtonAction = PointerActions.Key(action));
    }

    public static void SetPinDoubleClick(string key)
    {
        var action = PointerActions.ParsePin(key);
        LoadAll();
        lock (Gate) _pinDouble = action;
        Save(file => (file.PinToScreen ??= new PinToScreenFile()).DoubleClickAction = PointerActions.Key(action));
    }

    public static void SetPinMiddleClick(string key)
    {
        var action = PointerActions.ParsePin(key);
        LoadAll();
        lock (Gate) _pinMiddle = action;
        Save(file => (file.PinToScreen ??= new PinToScreenFile()).MiddleButtonAction = PointerActions.Key(action));
    }

    public static void SetConfirmBeforeDestroyingWindow(bool on)
    {
        LoadAll();
        lock (Gate) _confirmDestroy = on;
        Save(file => (file.PinToScreen ??= new PinToScreenFile()).ConfirmBeforeDestroyingWindow = on);
    }

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _overlayDouble = null;
            _overlayMiddle = null;
            _pinDouble = null;
            _pinMiddle = null;
            _confirmDestroy = null;
        }
    }

    private static void LoadAll()
    {
        lock (Gate)
        {
            if (_overlayDouble != null && _overlayMiddle != null &&
                _pinDouble != null && _pinMiddle != null && _confirmDestroy != null)
                return;
        }

        OverlayPointerAction overlayDouble = PointerActions.ParseOverlay(PointerActions.DefaultOverlayDoubleClick);
        OverlayPointerAction overlayMiddle = PointerActions.ParseOverlay(PointerActions.DefaultOverlayMiddle);
        PinPointerAction pinDouble = PointerActions.ParsePin(PointerActions.DefaultPinDoubleClick);
        PinPointerAction pinMiddle = PointerActions.ParsePin(PointerActions.DefaultPinMiddle);
        bool confirmDestroy = true;

        try
        {
            var file = SettingsStore.Read(ConfigStore.Anchor);
            overlayDouble = PointerActions.ParseOverlay(
                file.Screenshot?.DoubleClickAction ?? PointerActions.DefaultOverlayDoubleClick);
            overlayMiddle = PointerActions.ParseOverlay(
                file.Screenshot?.MiddleButtonAction ?? PointerActions.DefaultOverlayMiddle);
            pinDouble = PointerActions.ParsePin(
                file.PinToScreen?.DoubleClickAction ?? PointerActions.DefaultPinDoubleClick);
            pinMiddle = PointerActions.ParsePin(
                file.PinToScreen?.MiddleButtonAction ?? PointerActions.DefaultPinMiddle);
            confirmDestroy = file.PinToScreen?.ConfirmBeforeDestroyingWindow ?? true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PointerSettings", "读取指针动作设置失败：" + ex.Message);
        }

        lock (Gate)
        {
            _overlayDouble ??= overlayDouble;
            _overlayMiddle ??= overlayMiddle;
            _pinDouble ??= pinDouble;
            _pinMiddle ??= pinMiddle;
            _confirmDestroy ??= confirmDestroy;
        }
    }

    private static void Save(Action<SettingsFile> mutate)
    {
        try
        {
            SettingsStore.Modify(ConfigStore.Anchor, mutate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PointerSettings", "保存指针动作设置失败：" + ex.Message);
        }
    }
}
