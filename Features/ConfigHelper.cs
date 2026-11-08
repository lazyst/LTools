using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 设置（对应原版 lib/ConfigHelper.ahk）。CapsLock+\ 切换配置窗口；
/// UI 见 <see cref="Views.ConfigHelperWindow"/>，逻辑委托 <see cref="MenuSystem"/> 的 CRUD。
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
        _window = new ConfigHelperWindow(_configPath ?? "");
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    /// <summary>
    /// 打开设置并切到「面板管理」；<paramref name="sceneId"/> 非空时选中该场景
    /// （面板的「为此程序创建场景」用）。窗口已打开则直接激活并定位。
    /// </summary>
    public static void OpenPanelScene(string? sceneId = null)
    {
        if (_window == null)
        {
            _window = new ConfigHelperWindow(_configPath ?? "");
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        _window.NavigateToPanelScene(sceneId);
    }

    /// <summary>打开设置并切到「AI 接入」（面板暂存区的「AI 接入设置」入口用）。</summary>
    public static void OpenAi()
    {
        if (_window == null)
        {
            _window = new ConfigHelperWindow(_configPath ?? "");
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        _window.NavigateToAi();
    }
}
