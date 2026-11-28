using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「延迟建页（B 方案）」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 惰性页（B 方案） ============
    // XAML 里 Host_* 只是空 ContentControl；首次访问 Page_* 才 new 出真正的 UserControl 并塞进宿主。
    // 这样构造期不必一次建 6 棵可视树（「从超级面板打开设置」实测卡顿的大头），且只有切到的
    // 那一页被实例化 + 载入数据。可见性一律设在 Host_* 上（不触发创建），方法与数据走 Page_*
    // （触发创建）。跨页刷新用 RefreshCreatedPages()，别为没打开过的页强建视图。
    private HotkeysView Page_Hotkeys => _pageHotkeys ??= Mount(Host_Hotkeys, () => new HotkeysView());
    private MacroView Page_Macros => _pageMacros ??= Mount(Host_Macros, () => new MacroView());
    private SubActionLibraryView Page_SubActions => _pageSubActions ??= Mount(Host_SubActions, () => new SubActionLibraryView());
    private PanelManageView Page_SuperPanel => _pageSuperPanel ??= Mount(Host_SuperPanel, () => new PanelManageView());
    private TriggersView Page_Triggers => _pageTriggers ??= Mount(Host_Triggers, () => new TriggersView());
    private AiView Page_Ai => _pageAi ??= Mount(Host_Ai, () => new AiView());

    private static T Mount<T>(ContentControl host, Func<T> factory) where T : UIElement
    {
        var view = factory();
        host.Content = view;
        return view;
    }

    /// <summary>
    /// 配置 / 动作在别处变了（删动作 / 导入 / 重新加载 / 保存）：只刷已经建出来的页。
    /// 从没打开过的页下次切入时 ApplyNavPage 自然会 Reload，不必现在就为它建视图。
    /// </summary>
    private void RefreshCreatedPages()
    {
        // 动作管理是内联页（永远建着），也要跟着数据变：面板 / 别处删动作时表格与引用位置下拉即时刷新。
        PopulateActions();
        if (_pageHotkeys != null) _pageHotkeys.Reload();
        if (_pageSuperPanel != null) _pageSuperPanel.Reload();
        if (_pageAi != null) _pageAi.Reload();
        if (_pageMacros != null) _pageMacros.Reload();
        if (_pageSubActions != null) _pageSubActions.Reload();
        if (_pageTriggers != null) _pageTriggers.Reload();
        // 截图 Pro 的设置可能被覆盖层 / 录屏面板 / 颜色预设窗改过：正停在这一组就同步显示值
        if (NavFamilyOf(_currentNav) == FamilyCapture)
        {
            _initializing = true;
            PopulateToggles();
            _initializing = false;
        }

        // 热键页：正在「键鼠宏」子页时回填两个录入框（别处改了 macros.json / 导入配置）
        if (NavFamilyOf(_currentNav) == FamilyHotkeys && SubPageOf(FamilyHotkeys, _currentNav) == "macro")
            PopulateMacroHotkeys();
    }

    /// <summary>别处改了动作 / 宏：切回 UI 线程并合批刷新（事件可能来自录制 / AI 采纳的后台线程）。</summary>
    /// <remarks>只在数据真变时发，不在切页 / 导航记忆写盘时发，保住阶段 1 惰性建页的减负成果。</remarks>
    private void OnConfigChanged()
    {
        Ui.Post(() =>
        {
            if (!IsLoaded) return;   // 窗口还没显示 / 已关闭，下次开窗自然会重读新数据
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

}
