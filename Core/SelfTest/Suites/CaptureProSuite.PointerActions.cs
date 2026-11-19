using System;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 指针动作（P7-b，照 Snow Shot 的 <c>MouseReleaseActionController</c> 与四个指针动作设置）：
/// 取值解析 / 默认值 / 实时贴图门禁 / 下拉选项，以及「按下武装、抬起才执行」的手势生命周期。
/// </summary>
/// <remarks>
/// 真窗口那一段（右键按下不关、抬起才关；贴图双击切缩略图；捕获被抢走就作废）在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>releaseAction</c> 段；这里只碰纯逻辑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PointerActionCases()
    {
        // 解析：认得出上游那一套英文键，大小写与空白都不计较
        Check.Equal(OverlayPointerAction.Copy, PointerActions.ParseOverlay("copy"), "覆盖层 copy");
        Check.Equal(OverlayPointerAction.Save, PointerActions.ParseOverlay(" save "), "覆盖层 save（带空白）");
        Check.Equal(OverlayPointerAction.QuickSave, PointerActions.ParseOverlay("QUICK_SAVE"), "覆盖层 quick_save（大写）");
        Check.Equal(OverlayPointerAction.Pin, PointerActions.ParseOverlay("pin"), "覆盖层 pin");
        Check.Equal(OverlayPointerAction.Ocr, PointerActions.ParseOverlay("ocr"), "覆盖层 ocr");
        Check.Equal(OverlayPointerAction.OcrCopy, PointerActions.ParseOverlay("ocr_copy"), "覆盖层 ocr_copy");
        Check.Equal(OverlayPointerAction.Cancel, PointerActions.ParseOverlay("cancel"), "覆盖层 cancel");
        Check.Equal(OverlayPointerAction.None, PointerActions.ParseOverlay("none"), "覆盖层 none");
        Check.Equal(OverlayPointerAction.None, PointerActions.ParseOverlay("不认识"), "认不出的键 = 不处理");
        Check.Equal(OverlayPointerAction.None, PointerActions.ParseOverlay(null), "空值 = 不处理");

        Check.Equal(PinPointerAction.ResetZoom, PointerActions.ParsePin("reset_zoom"), "贴图 reset_zoom");
        Check.Equal(PinPointerAction.ThumbnailMode, PointerActions.ParsePin("thumbnail_mode"), "贴图 thumbnail_mode");
        Check.Equal(PinPointerAction.HideToTop, PointerActions.ParsePin("hide_to_top"), "贴图 hide_to_top（实现前也认）");
        Check.Equal(PinPointerAction.TextRecognition,
            PointerActions.ParsePin("show_text_recognition_results"), "贴图文字识别结果（实现前也认）");
        Check.Equal(PinPointerAction.Close, PointerActions.ParsePin("close"), "贴图 close");
        Check.Equal(PinPointerAction.Destroy, PointerActions.ParsePin("destroy"), "贴图 destroy");
        Check.Equal(PinPointerAction.None, PointerActions.ParsePin(""), "空值 = 不处理");

        // 序列化往返：设置页选什么，写回磁盘的就是什么
        foreach (var action in (OverlayPointerAction[])Enum.GetValues(typeof(OverlayPointerAction)))
        {
            string key = PointerActions.Key(action);
            Check.Equal(action, PointerActions.ParseOverlay(key), $"覆盖层 {action} 往返");
            Check.True(PointerActions.Describe(action).Length > 0, $"覆盖层 {action} 有中文名");
        }

        foreach (var action in (PinPointerAction[])Enum.GetValues(typeof(PinPointerAction)))
        {
            string key = PointerActions.Key(action);
            Check.Equal(action, PointerActions.ParsePin(key), $"贴图 {action} 往返");
            Check.True(PointerActions.Describe(action).Length > 0, $"贴图 {action} 有中文名");
        }

        // 默认值（上游 configurationschema.cpp）：覆盖层双击复制、中键贴图；贴图双击缩略图、中键重置缩放
        Check.Equal(OverlayPointerAction.Copy,
            PointerActions.ParseOverlay(PointerActions.DefaultOverlayDoubleClick), "默认：框选后双击 = 复制");
        Check.Equal(OverlayPointerAction.Pin,
            PointerActions.ParseOverlay(PointerActions.DefaultOverlayMiddle), "默认：框选后中键 = 贴到屏幕");
        Check.Equal(PinPointerAction.ThumbnailMode,
            PointerActions.ParsePin(PointerActions.DefaultPinDoubleClick), "默认：贴图双击 = 缩略图模式");
        Check.Equal(PinPointerAction.ResetZoom,
            PointerActions.ParsePin(PointerActions.DefaultPinMiddle), "默认：贴图中键 = 重置缩放");

        // 下拉选项：键都能解析回来、标签不空；贴图那边**不列**还没实现的两项（不做假选项）
        Check.Equal(8, PointerActions.OverlayOptions.Count, "覆盖层下拉 8 项");
        foreach (var (key, label) in PointerActions.OverlayOptions)
        {
            Check.True(key == "none" || PointerActions.ParseOverlay(key) != OverlayPointerAction.None,
                $"覆盖层选项 {key} 能解析");
            Check.True(label.Length > 0, $"覆盖层选项 {key} 有中文名");
        }

        Check.Equal(6, PointerActions.PinOptions.Count, "贴图下拉 6 项（收顶部已实现，识别结果还没做）");
        foreach (var (key, label) in PointerActions.PinOptions)
        {
            Check.True(PointerActions.ParsePin(key) != PinPointerAction.None || key == "none",
                $"贴图选项 {key} 能解析");
            Check.True(label.Length > 0, $"贴图选项 {key} 有中文名");
        }

        Check.True(ContainsKey(PointerActions.PinOptions, "hide_to_top"),
            "下拉里有「收顶部」（P5-i 已实现，不是假选项）");
        Check.False(ContainsKey(PointerActions.PinOptions, "show_text_recognition_results"),
            "下拉里没有假的「识别结果」");

        // 实时贴图：只许「不处理 / 重置缩放 / 关闭」（照上游 liveCommandAllowed 的命令表）
        Check.True(PointerActions.AllowedInLive(PinPointerAction.None), "实时：不处理");
        Check.True(PointerActions.AllowedInLive(PinPointerAction.ResetZoom), "实时：重置缩放");
        Check.True(PointerActions.AllowedInLive(PinPointerAction.Close), "实时：关闭");
        Check.False(PointerActions.AllowedInLive(PinPointerAction.ThumbnailMode), "实时：缩略图不允许");
        Check.False(PointerActions.AllowedInLive(PinPointerAction.Destroy), "实时：删除不允许");
        Check.False(PointerActions.AllowedInLive(PinPointerAction.HideToTop), "实时：收顶部不允许");

        GestureCases();
    }

    private static bool ContainsKey(System.Collections.Generic.IReadOnlyList<(string Key, string Label)> options, string key)
    {
        foreach (var (k, _) in options)
            if (k == key) return true;
        return false;
    }

    /// <summary>「按下武装、抬起才执行」的纯逻辑：武装条件 / 只执行一次 / 取消后排队的那一拍作废。</summary>
    private static void GestureCases()
    {
        var gesture = new MouseReleaseGesture();
        int ran = 0;

        Check.False(gesture.Arm(PointerButton.Right, () => ran++, scopeVisible: false),
            "范围窗口不可见时不能武装");
        Check.False(gesture.Pending, "没武装成功就没有待执行");

        Check.True(gesture.Arm(PointerButton.Right, () => ran++, scopeVisible: true), "可见 + 有动作 = 武装");
        Check.True(gesture.Pending, "武装之后是待执行");
        Check.Equal((PointerButton?)PointerButton.Right, gesture.Button, "记下是哪个键");
        Check.False(gesture.Arm(PointerButton.Left, () => ran++, scopeVisible: true), "已经有待执行就不能再武装");

        Check.Equal(0L, gesture.Release(PointerButton.Left), "别的键抬起不算");
        long revision = gesture.Release(PointerButton.Right);
        Check.True(revision > 0, "右键抬起给出一轮 revision");
        Check.Equal(0L, gesture.Release(PointerButton.Right), "同一次抬起不重复触发");

        Check.True(gesture.Take(revision, scopeVisible: true, out Action? action), "抬起之后一拍取到动作");
        Check.True(action != null, "取到的动作不是空的");
        action?.Invoke();
        Check.Equal(1, ran, "动作只执行一次");
        Check.False(gesture.Pending, "执行完就清空");
        Check.False(gesture.Take(revision, scopeVisible: true, out _), "同一轮 revision 不能取第二次");

        // 取消（窗口藏了 / 捕获被抢走）：已经排队的那一拍什么都取不到
        Check.True(gesture.Arm(PointerButton.Left, () => ran++, scopeVisible: true), "再次武装");
        long second = gesture.Release(PointerButton.Left);
        gesture.Cancel();
        Check.False(gesture.Take(second, scopeVisible: true, out _), "取消之后排队的那一拍作废");
        Check.Equal(1, ran, "作废的动作不会被执行");

        // 取动作时范围窗口已经不可见 → 丢弃
        Check.True(gesture.Arm(PointerButton.Left, () => ran++, scopeVisible: true), "第三次武装");
        long third = gesture.Release(PointerButton.Left);
        Check.False(gesture.Take(third, scopeVisible: false, out _), "取的时候窗口已经藏了 → 不执行");
        Check.Equal(1, ran, "藏了也不执行");

        // 没武装：抬起与取都不成立
        Check.Equal(0L, gesture.Release(PointerButton.Right), "没武装时抬起不算");
        Check.False(gesture.Take(0, scopeVisible: true, out _), "没武装时取不到动作");
    }
}
