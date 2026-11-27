using LTools.Config;
using LTools.Features.Panel;
using LTools.Native;

namespace LTools.Core.SelfTest;

/// <summary>
/// 面板底部那条「尚未为“X”添加专用设置」提示：什么时候该出现、什么时候不该出现。
/// </summary>
/// <remarks>
/// 纯函数部分是 <see cref="PanelSceneResolver.Resolve"/>；「面板显示成什么样」在仓库内
/// <c>tests/PanelDragSmoke</c> 的 ⑪ 段（真窗口 + 宿主身份，回归过「_preview 恒真 → 提示永不显示」）。
/// </remarks>
internal static class PanelHintSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("panelHint", "未配置提示：无场景 / 关掉过 / 命中场景 / 取不到前台", Cases));
    }

    private static void Cases()
    {
        var panel = new PanelConfig();
        var fg = new ForegroundApp(IntPtr.Zero, @"C:\smoke\noscene.exe", "noscene.exe", "冒烟窗口", "SmokeWnd");

        PanelResolveResult none = PanelSceneResolver.Resolve(panel, fg);
        Check.True(none.Scene == null, "没有上下文场景时不命中");
        Check.True(none.ShowHint, "没有上下文场景时显示提示");

        panel.HintDismissed = true;
        Check.False(PanelSceneResolver.Resolve(panel, fg).ShowHint, "手动关掉过就不再显示");
        panel.HintDismissed = false;

        Check.False(PanelSceneResolver.Resolve(panel, null).ShowHint, "取不到前台程序时不显示");

        // 进程名命中根场景：不显示提示
        var scene = new PanelSceneDto { Id = "s1", Name = "noscene" };
        scene.Match.Exe = "noscene.exe";
        panel.Context.Scenes.Add(scene);
        PanelResolveResult hit = PanelSceneResolver.Resolve(panel, fg);
        Check.Equal("s1", hit.Scene?.Id, "进程名命中上下文场景");
        Check.False(hit.ShowHint, "命中了就不显示提示");

        // 只带窗口条件的根场景（进程对不上、标题对上）
        var byTitle = new PanelSceneDto { Id = "s2", Name = "按标题" };
        byTitle.Match.TitleContains = "冒烟";
        panel.Context.Scenes.Clear();
        panel.Context.Scenes.Add(byTitle);
        Check.Equal("s2", PanelSceneResolver.Resolve(panel, fg).Scene?.Id, "窗口标题条件也能命中");

        // 禁用了的场景不算
        byTitle.Enabled = false;
        Check.True(PanelSceneResolver.Resolve(panel, fg).ShowHint, "场景停用后回到「未配置」");
    }
}
