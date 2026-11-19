using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图「显示文字识别结果」（P4-j，照上游 <c>show_text_recognition_results</c> / 
/// <c>screenshotOcrImageWithinPixelLimit</c>）：尺寸门槛、阶段推导、菜单与状态条文案。
/// </summary>
/// <remarks>
/// 真窗口那一段（识别 → 铺文字层 → 拖动选择 → 复制、OCR 模式里禁用拖动 / 滚轮）在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>pinocr</c> 段；这里只碰纯逻辑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinOcrCases()
    {
        // 尺寸门槛：上游 kScreenshotOcrMaximumPixels = 3840 × 2160
        Check.Equal(3840L * 2160L, PinOcr.MaxPixels, "识别上限就是 3840×2160");
        Check.True(PinOcr.WithinPixelLimit(1, 1), "1×1 能识别");
        Check.True(PinOcr.WithinPixelLimit(1920, 1080), "1080p 能识别");
        Check.True(PinOcr.WithinPixelLimit(3840, 2160), "正好 4K 能识别（含等号）");
        Check.False(PinOcr.WithinPixelLimit(3841, 2160), "超一个像素就不行");
        Check.False(PinOcr.WithinPixelLimit(2160, 3841), "竖过来超了也不行");
        Check.False(PinOcr.WithinPixelLimit(0, 100), "宽 0 不行");
        Check.False(PinOcr.WithinPixelLimit(100, 0), "高 0 不行");
        Check.False(PinOcr.WithinPixelLimit(-1, 10), "负宽不行");

        // 阶段：尺寸优先，然后识别中，然后结果
        Check.Equal(PinOcrPhase.TooLarge, PinOcr.PhaseOf(false, false, false), "超限 = 尺寸过大");
        Check.Equal(PinOcrPhase.TooLarge, PinOcr.PhaseOf(false, true, true), "超限压过识别中 / 结果");
        Check.Equal(PinOcrPhase.Busy, PinOcr.PhaseOf(true, true, false), "识别中");
        Check.Equal(PinOcrPhase.Busy, PinOcr.PhaseOf(true, true, true), "识别中压过旧结果");
        Check.Equal(PinOcrPhase.Ready, PinOcr.PhaseOf(true, false, true), "有结果");
        Check.Equal(PinOcrPhase.Idle, PinOcr.PhaseOf(true, false, false), "还没识别");

        // 菜单：只有「尺寸过大 / 识别中」换文案且不可点；勾只在真有结果时
        Check.Equal(PinOcr.ShowLabel, PinOcr.MenuLabel(PinOcrPhase.Idle), "空闲写「显示文字」");
        Check.Equal(PinOcr.ShowLabel, PinOcr.MenuLabel(PinOcrPhase.Ready), "有结果仍写「显示文字」");
        Check.Equal(PinOcr.BusyLabel, PinOcr.MenuLabel(PinOcrPhase.Busy), "识别中写「正在识别…」");
        Check.Equal(PinOcr.TooLargeLabel, PinOcr.MenuLabel(PinOcrPhase.TooLarge), "超限写「图片尺寸过大」");

        Check.True(PinOcr.MenuEnabled(PinOcrPhase.Idle), "空闲可点（开始识别）");
        Check.True(PinOcr.MenuEnabled(PinOcrPhase.Ready), "有结果可点（退出文字层）");
        Check.True(PinOcr.MenuEnabled(PinOcrPhase.Empty), "没找到也可点（重试）");
        Check.False(PinOcr.MenuEnabled(PinOcrPhase.Busy), "识别中不可点");
        Check.False(PinOcr.MenuEnabled(PinOcrPhase.TooLarge), "超限不可点");

        Check.True(PinOcr.MenuChecked(PinOcrPhase.Ready), "铺着文字层时才勾");
        Check.False(PinOcr.MenuChecked(PinOcrPhase.Idle), "没铺不勾");
        Check.False(PinOcr.MenuChecked(PinOcrPhase.Empty), "没结果不勾");
        Check.False(PinOcr.MenuChecked(PinOcrPhase.TooLarge), "超限不勾");

        // 状态条：识别中 / 行数 / 已选字数 / 没找到 / 尺寸过大
        Check.Equal(PinOcr.BusyLabel, PinOcr.BarText(PinOcrPhase.Busy, 0, 0), "状态条：识别中");
        Check.Equal(PinOcr.EmptyHint, PinOcr.BarText(PinOcrPhase.Empty, 0, 0), "状态条：没找到文字");
        Check.Equal(PinOcr.TooLargeLabel + "（上限 3840×2160）",
            PinOcr.BarText(PinOcrPhase.TooLarge, 0, 0), "状态条：尺寸过大带上限");
        Check.Equal("", PinOcr.BarText(PinOcrPhase.Idle, 0, 0), "状态条：空闲为空");
        Check.Equal("12 行 · 拖动选择文字", PinOcr.BarText(PinOcrPhase.Ready, 12, 0),
            "状态条：有结果时报行数");
        Check.Equal("已选 5 字 · 点「复制所选」或右键复制", PinOcr.BarText(PinOcrPhase.Ready, 12, 5),
            "状态条：有选区时报字数");
        Check.Equal("0 行 · 拖动选择文字", PinOcr.BarText(PinOcrPhase.Ready, -3, 0),
            "状态条：行数不会为负");
    }
}
