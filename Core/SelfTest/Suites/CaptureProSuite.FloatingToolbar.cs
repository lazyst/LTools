using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 悬浮工具条（照 Snow Shot 的 <c>floating_toolbar/*</c>）：默认布局 / 归一 /
/// 落盘往返 / 设置默认值与显示名。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void FloatingToolbarCases()
    {
        // ============ 设置默认值（逐字照 configurationschema.cpp） ============
        var settings = FloatingToolbarSettings.Default;
        Check.False(settings.Enabled, "默认不显示悬浮工具条");
        Check.Equal(FloatingToolbarSettings.ModeIcon, settings.Mode, "默认图标模式");
        Check.Equal(50, settings.Opacity, "默认不透明度 50");
        Check.Equal(FloatingToolbarSettings.DropPin, settings.DropBehavior, "默认拖放 = 贴到屏幕");
        Check.True(settings.HideInFullscreen, "默认全屏时隐藏");
        Check.True(settings.HideDuringCapture, "默认截图时隐藏");
        Check.True(settings.IsIconMode, "IsIconMode 认图标模式");

        Check.Equal(10, FloatingToolbarStore.NormalizeOpacity(5), "不透明度下限 10");
        Check.Equal(100, FloatingToolbarStore.NormalizeOpacity(500), "不透明度上限 100");
        Check.Equal(50, FloatingToolbarStore.NormalizeOpacity(0), "0 = 没配 → 默认 50");
        Check.Equal("toolbar", FloatingToolbarStore.ModeOf("TOOLBAR"), "模式大小写不敏感");
        Check.Equal("icon", FloatingToolbarStore.ModeOf("bogus"), "认不出的模式回图标");
        Check.Equal("image_splitting", FloatingToolbarStore.DropBehaviorOf("image_splitting"),
            "拖放行为原样");
        Check.Equal("pin_to_screen", FloatingToolbarStore.DropBehaviorOf("bogus"),
            "认不出的拖放行为回贴到屏幕");
        Check.Equal("一直展开", FloatingToolbarStore.ModeLabel("toolbar"), "模式显示名");
        Check.Equal("拆分图片", FloatingToolbarStore.DropLabel("image_splitting"), "拖放行为显示名");

        // ============ 默认布局（照上游 defaultFloatingToolbarLayout） ============
        var layout = FloatingToolbarLayout.Default();
        Check.Equal(4, layout.Positions.Count, "默认 4 叠");
        Check.Equal(7, layout.VisibleCount, "默认 7 个按钮在工具条上");
        Check.Equal(7, layout.Hidden.Count, "默认收起 7 个");
        Check.Equal("ltools.captureScreen", layout.Positions[0].Single(), "第一叠 = 截图");
        Check.Equal("common.pinClipboard", layout.Positions[1].Single(), "第二叠 = 贴剪贴板");
        Check.Equal("ltools.captureDelay", layout.Positions[2].Single(), "第三叠 = 延时截图");
        Check.Equal("ltools.captureRecognize,ltools.captureTranslate,ltools.captureScrolling,common.record",
            string.Join(",", layout.Positions[3]), "第四叠 = 识别 / 翻译 / 长截图 / 录屏");
        Check.Equal(14, FloatingToolbarLayout.Catalog.Count, "功能目录 14 个");

        // ============ 落盘往返 ============
        var positions = layout.ToPositions();
        var hidden = layout.ToHidden();
        Check.Equal(4, positions.Count, "一行一叠");
        Check.Equal("ltools.captureScreen", positions[0], "第一行就是第一叠");
        var roundtrip = FloatingToolbarLayout.Parse(positions, hidden);
        Check.Equal(layout.Describe(), roundtrip.Describe(), "写出去再读回来完全一致");

        // ============ 归一 ============
        var normalized = FloatingToolbarLayout.Normalize(
            new[] { "ltools.captureScreen,bogus,ltools.captureScreen", "", "common.record" },
            new[] { "common.record", "nope", "ltools.captureSave" }, appendMissing: false);
        Check.Equal(2, normalized.Positions.Count, "空叠与认不出的引用丢掉");
        Check.Equal(1, normalized.Positions[0].Count, "同一个引用只算一次");
        Check.Equal(1, normalized.Hidden.Count, "既在位置里又在隐藏里的按位置算");
        Check.Equal("ltools.captureSave", normalized.Hidden.Single(), "隐藏里只留有效的");

        var appended = FloatingToolbarLayout.Normalize(
            new[] { "ltools.captureScreen" }, new string[0], appendMissing: true);
        Check.Equal(14, appended.VisibleCount, "没提到的功能补进最后一叠（1 + 13）");
        Check.Equal(1, appended.Positions.Count, "补进来的并进最后一叠");

        var empty = FloatingToolbarLayout.Parse(null, null);
        Check.Equal(4, empty.Positions.Count, "两个字段都空 → 默认布局");

        // ============ 摘要 ============
        Check.True(layout.Describe().Contains("│"), "摘要用 │ 分隔叠");
        Check.True(layout.Describe().Contains("截图"), "摘要里是中文名");
    }
}
