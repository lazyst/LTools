using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 设置（对应原版 lib/ConfigHelper.ahk）。CapsLock+\ 切换配置窗口；
/// UI 见 <see cref="Views.ConfigHelperWindow"/>，逻辑委托 <see cref="ActionLibrary"/> / <see cref="HotkeyRegistry"/>。
/// </summary>
internal static class ConfigHelper
{
    private static ConfigHelperWindow? _window;
    private static string? _configPath;

    /// <summary>启动时初始化（记录配置路径）。由 App 调用。</summary>
    public static void Initialize(string? configPath) => _configPath = configPath;

    /// <summary>CapsLock+\ 切换：已打开则关闭，否则打开。</summary>
    public static void Toggle()
    {
        if (_window != null)
        {
            _window.Close();
            _window = null;
            return;
        }
        EnsureWindow();
    }

    /// <summary>
    /// 打开设置，停在上次记住的导航页；已经打开就激活并提到最前。
    /// 面板齿轮 / 托盘 / 功能的普通「设置」入口走这里——要强制跳页才用
    /// <see cref="OpenPanelScene"/> / <see cref="OpenAi"/>。
    /// </summary>
    public static void Open()
    {
        var window = EnsureWindow();
        window.Activate();
        // 面板 / 悬浮都是 Topmost，被它们盖住时 Activate 不会自己浮上来，借 Topmost 抬一次
        window.Topmost = true;
        window.Topmost = false;
    }

    private static ConfigHelperWindow EnsureWindow()
    {
        if (_window != null) return _window;

        var window = new ConfigHelperWindow(_configPath ?? "");
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        return window;
    }

    /// <summary>
    /// 打开设置并切到「面板管理」；<paramref name="sceneId"/> 非空时选中该场景
    /// （面板的「为此程序创建场景」用）。窗口已打开则直接激活并定位。
    /// </summary>
    public static void OpenPanelScene(string? sceneId = null)
    {
        EnsureWindow().NavigateToPanelScene(sceneId);
    }

    /// <summary>打开设置并切到「AI 接入」（面板暂存区的「AI 接入设置」入口用）。</summary>
    public static void OpenAi()
    {
        EnsureWindow().NavigateToAi();
    }
}
