using System.Collections.Generic;
using LTools.Config;

namespace LTools.Core.SelfTest;

/// <summary>
/// 文本窗口（动作单元 <c>ui.textWindow</c>）的窗口偏好：**默认不置顶**，置顶走标题栏图钉并记进
/// <c>configs/ui.json</c> 的 <c>TextWindowTopmost</c>。见 <c>Views/TextViewWindow</c> 的类注释。
/// </summary>
/// <remarks>
/// 只断言纯数据（默认值 / 写盘形状）；「最小化进任务栏、不在左下角留一条小标题栏」是 Win32 / DWM 行为，
/// 靠 <c>tests/TextWindowSmoke</c>（真窗口 + 真最小化）守。
/// </remarks>
internal static class TextWindowSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("textWindow", "文本窗口默认不置顶", DefaultNotTopmost));
        cases.Add(new("textWindow", "置顶偏好写进 ui.json 并与速记互不影响", RoundTrip));
    }

    private static void DefaultNotTopmost()
    {
        Check.False(new AppConfig().TextWindowTopmost, "整份配置的默认值是「不置顶」");
        Check.False(new UiStateFile().TextWindowTopmost, "ui.json 缺字段时也是「不置顶」");
    }

    private static void RoundTrip()
    {
        var cfg = new AppConfig { TextWindowTopmost = true, QuickNoteTopmost = false };
        UiStateFile ui = ConfigBundle.ToUi(cfg);

        Check.True(ui.TextWindowTopmost, "ToUi 带上文本窗口置顶偏好");
        Check.False(ui.QuickNoteTopmost, "速记的置顶不被带过来（两个偏好各自独立）");

        var pinnedNote = new AppConfig { QuickNoteTopmost = true };
        Check.False(pinnedNote.TextWindowTopmost, "速记钉住不等于文本窗口钉住");
    }
}
