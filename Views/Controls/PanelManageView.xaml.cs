using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>
/// 设置「面板管理」页。左侧主导航把这里拆成三个二级页（常规 / 场景 / 禁止弹出）：
/// <list type="bullet">
///   <item>常规：开关、长按阈值、视图选项；</item>
///   <item>场景：左边场景列表，右边详情——匹配条件**默认折叠成一行**，动作管理（分组 + 磁贴）常驻；</item>
///   <item>禁止弹出：禁止列表。</item>
/// </list>
/// 设计见 <c>docs/PANEL_REDESIGN.md</c> §5.1。
/// </summary>
public partial class PanelManageView : UserControl
{
    /// <summary>上下文「通用」区的键（面板跳转设置时也用它定位）。</summary>
    public const string CommonKey = "common";

    private readonly DispatcherTimer _thresholdTimer;
    private string _selectedKey = CommonKey;
    private bool _loading = true;      // 构造期间 XAML 设 Minimum/Maximum 也会触发 ValueChanged
    private bool _rulesExpanded;       // 匹配条件默认折叠（方案 3：规则折叠、动作管理常驻）
    private TileDragSort? _tileDrag;
    private TextBox? _sceneNameBox;    // 右侧场景名输入框（右键「重命名场景」用）
    private readonly Dictionary<PanelGroupDto, TextBox> _groupNameBoxes = new();

    public PanelManageView()
    {
        InitializeComponent();

        _thresholdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _thresholdTimer.Tick += (_, _) =>
        {
            _thresholdTimer.Stop();
            SaveThreshold();
        };

        // 构造时可能还没挂到窗口上（Window.GetWindow 为 null），那时拖动挂不上。
        // 首次 Loaded 只补建拖动实例，不重建详情区（切页 / 导入由 Reload 重建，避免首次建页刷两遍）。
        Loaded += (_, _) =>
        {
            if (_tileDrag == null) EnsureTileDrag();
        };

        // 空白处右键（照 Quicker）：列表空白新建场景，详情空白新建分组 / 添加动作
        SceneList.ContextMenu = BuildSceneListBlankMenu();
        DetailHost.ContextMenu = BuildDetailBlankMenu();
    }

}
