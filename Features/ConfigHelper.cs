using LTools.Core;
using LTools.Core.Link;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 设置（对应原版 lib/ConfigHelper.ahk）。CapsLock+\ 切换配置窗口；
/// UI 见 <see cref="Views.ConfigHelperWindow"/>。
/// </summary>
/// <remarks>
/// <para>
/// **窗口只在普通权限宿主里**（`docs/PROCESS_SPLIT.md` §15.4 第 5 步）。提权进程里的入口
/// （托盘 / CapsLock+\ / 托盘双击 / `--perf`）只发一条 <see cref="SettingsMethods.Show"/> 意图，
/// 由宿主按自己的身份开 / 切 / 定位那一页；因此下面每个公开方法两边都能调，调用点不必再判断身份。
/// </para>
/// <para>
/// 宿主里这份窗口用的是 <see cref="ConfigStore.InitializeReadOnly"/> 读进来的内存副本，
/// 写配置照旧走各自的 store（`configs/` 的物理写者就是宿主）。
/// </para>
/// </remarks>
internal static class ConfigHelper
{
    private static ConfigHelperWindow? _window;
    private static string? _configPath;

    /// <summary>启动时初始化（记录配置路径）。由 App / ShareHost 调用。</summary>
    public static void Initialize(string? configPath) => _configPath = configPath;

    /// <summary>CapsLock+\ 切换：已打开则关闭，否则打开。</summary>
    public static void Toggle()
    {
        if (Remote(SettingsActions.Toggle)) return;

        if (_window != null)
        {
            Close();
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
        if (Remote(SettingsActions.Open)) return;

        // 前台锁：只 Show()/Activate() 常被压回去（第一次开窗尤其明显），统一走 WindowFront。
        WindowFront.Bring(EnsureWindow());
    }

    /// <summary>关掉设置（没开就什么都不做）。</summary>
    public static void Close()
    {
        ConfigHelperWindow? window = _window;
        _window = null;
        window?.Close();
    }

    /// <summary>
    /// 打开设置并切到「面板管理」；<paramref name="sceneId"/> 非空时选中该场景
    /// （面板的「为此程序创建场景」用）。窗口已打开则直接激活并定位。
    /// </summary>
    public static void OpenPanelScene(string? sceneId = null)
    {
        if (Remote(SettingsActions.Open, "superpanel-scenes")) return;

        EnsureWindow().NavigateToPanelScene(sceneId);
    }

    /// <summary>打开设置并切到「AI 接入」（面板暂存区的「AI 接入设置」入口用）。</summary>
    public static void OpenAi()
    {
        if (Remote(SettingsActions.Open, "ai")) return;

        EnsureWindow().NavigateToAi();
    }

    /// <summary>打开设置并切到「键鼠宏」（托盘「键鼠宏…」与录制完的提示用）。</summary>
    public static void OpenMacros()
    {
        if (Remote(SettingsActions.Open, "macros")) return;

        EnsureWindow().NavigateToMacros();
    }

    /// <summary>
    /// 左键双击托盘图标：按 <see cref="AppState.TrayDoubleClick"/> 打开设置窗口并停到对应页。
    /// <c>settings</c> = 普通打开（停上次记住的页），其余跳到指定导航 tag；
    /// <c>superpanel</c> / <c>macros</c> 是二级分组的父项 tag，<see cref="ConfigHelperWindow.NavigateTo"/> 会落在该组上次停留的子页。
    /// 6 个行为与设置「通用」页下拉、<see cref="TrayDoubleClickAction"/> 的枚举值一一对应，改一处要三处同步。
    /// </summary>
    public static void OpenOnTrayDoubleClick()
    {
        string? tag = AppState.TrayDoubleClick switch
        {
            TrayDoubleClickAction.actions => "actions",
            TrayDoubleClickAction.hotkeys => "hotkeys",
            TrayDoubleClickAction.macros => "macros",
            TrayDoubleClickAction.triggers => "triggers",
            TrayDoubleClickAction.superpanel => "superpanel",
            _ => null,   // settings：不强制跳页，Open() 停在上次记住的导航页
        };

        if (tag == null) Open();
        else if (!Remote(SettingsActions.Open, tag)) EnsureWindow().NavigateTo(tag);
    }

    /// <summary>
    /// 宿主收到执行器的「开设置」意图（托盘 / CapsLock+\ / 托盘双击 / `--perf` 都在提权进程里）。
    /// </summary>
    internal static void OpenRemote(SettingsOpenRequest? request)
    {
        if (!AppRole.IsUiHost) return;

        switch (request?.Action ?? SettingsActions.Open)
        {
            case SettingsActions.Close:
                Close();
                break;

            case SettingsActions.Toggle:
                Toggle();
                break;

            default:
                if (string.IsNullOrEmpty(request?.Target))
                {
                    Open();
                }
                else
                {
                    ConfigHelperWindow window = EnsureWindow();
                    window.NavigateTo(request.Target);
                    WindowFront.Bring(window);
                }
                break;
        }
    }

    /// <summary>执行器里没有设置窗口：把意图交给宿主并返回 true；宿主里返回 false，照常本地开。</summary>
    private static bool Remote(string action, string? target = null)
    {
        if (AppRole.IsUiHost) return false;

        LinkWorker.RequestSettings(new SettingsOpenRequest(action, target));
        return true;
    }

    private static ConfigHelperWindow EnsureWindow()
    {
        if (_window != null) return _window;

        // 宿主里第一次建窗：后台那次只读初始化可能还没跑完（主程序刚启动的一两秒），
        // 就地补上——否则会看到空的动作 / 面板列表。执行器里已经 Initialize 过，直接返回。
        string configPath = _configPath ?? ConfigStore.ConfigPath;
        if (AppRole.IsUiHost) ConfigStore.EnsureReadOnly(configPath);

        // 诊断采样：把 new + Show() 整段算作「开窗」（阶段 0 量化 / 阶段 2 复测同一把尺子）
        using var perfSeg = PerfProbe.Measure("open:total");
        var window = new ConfigHelperWindow(configPath);
        window.Closed += (_, _) => _window = null;
        _window = window;
        WindowFront.Show(window);
        return window;
    }

    // —— 诊断采样入口（仅 --perf 使用；不参与产品路径） ——
    internal static void PerfOpen()
    {
        if (Remote(SettingsActions.Open)) return;
        EnsureWindow();
    }

    internal static void PerfNavigate(string tag)
    {
        if (Remote(SettingsActions.Open, tag)) return;
        EnsureWindow().NavigateTo(tag);
    }

    internal static void PerfClose()
    {
        if (Remote(SettingsActions.Close)) return;
        Close();
    }
}
