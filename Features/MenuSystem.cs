using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPro.Config;
using CapsLockPro.Core;
using CapsLockPro.Native;
using CapsLockPro.Views;

namespace CapsLockPro.Features;

/// <summary>
/// 快捷菜单系统（对应原版 lib/MenuSystem.ahk + lib/ui/MenuUI.ahk）。
/// CapsLock+1~8 显示对应菜单组；CapsLock+9/0 在第 9/10 组为空时发送左右圆括号，
/// 否则显示菜单（见 CHANGELOG v1.1）。菜单内按 1~0 执行对应项，Esc 或点击外部关闭。
/// </summary>
/// <remarks>
/// 菜单项为<strong>动作 Id 引用</strong>（计划 §6），执行统一走 <see cref="ActionExecutor"/>。
/// 名称显示时从 <see cref="ActionRegistry"/> 按 Id 解析。空组仍发圆括号（保留旧行为）。
/// 钩子回调运行在 UI 线程，故 <see cref="Show"/> 可直接创建 WPF 窗口（modeless，非阻塞）。
/// 动作执行 spawn 到后台线程（不阻塞 UI / 钩子回调）。
/// </remarks>
internal static class MenuSystem
{
    private const int MaxGroups = 10;
    private static readonly MenuGroup?[] _groups = new MenuGroup?[MaxGroups + 1]; // 1-indexed
    private static MenuPopupWindow? _current;
    private static int _currentGroup; // 当前弹出菜单的组索引（供钩子路由选择用）
    private static string? _loadedConfigPath; // 缓存已加载的配置路径，避免重复 IO

    /// <summary>菜单组槽位总数（1..N）。</summary>
    public static int GroupCount => MaxGroups;

    /// <summary>当前是否有菜单弹出。</summary>
    public static bool IsMenuOpen => _current != null;

    /// <summary>指定窗口是否是当前活跃的菜单窗口（用于旧窗口淡出期间忽略其 Deactivated 事件）。</summary>
    public static bool IsCurrentWindow(System.Windows.Window w) => _current == w;

    /// <summary>判断屏幕坐标点是否落在当前菜单窗口矩形内（供鼠标钩子判定"点击外部"）。</summary>
    public static bool PointInMenuRect(int x, int y)
    {
        var w = _current;
        if (w == null) return false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>从 JSON 加载全部 10 个菜单组 + 填充全局动作清单。启动时调用一次；相同路径重复调用直接跳过。</summary>
    public static void Load(string? configPath)
    {
        if (configPath == _loadedConfigPath) return; // 已加载，跳过重复 IO
        _loadedConfigPath = configPath;
        var cfg = AppConfig.Load(configPath ?? "");
        TerminalLauncher.LoadFromConfig(cfg);
        // 填充全局动作清单（菜单项 / 超级面板 / 组合步骤都引用这些 Id）
        ActionRegistry.Clear();
        ActionRegistry.RegisterAll(cfg.Actions);
        for (int i = 1; i <= MaxGroups; i++)
            _groups[i] = FromDto(cfg.MenuGroups.Count >= i ? cfg.MenuGroups[i - 1] : null);
    }

    private static MenuGroup? FromDto(MenuGroupDto? d)
    {
        if (d == null) return null;
        var items = d.Items.ToList();
        return items.Count == 0 ? null : new MenuGroup(d.Name, items);
    }

    /// <summary>第 groupIndex 组是否为空（未启用或无项目）。</summary>
    public static bool IsEmpty(int groupIndex) => _groups[groupIndex] == null;

    /// <summary>CapsLock+数字键 派发入口（钩子调用）。</summary>
    public static void Dispatch(ushort vk)
    {
        int group = (vk == '0') ? 10 : (vk - '0'); // '1'..'9' → 1..9, '0' → 10
        // 空组圆括号回退（CHANGELOG v1.1）
        if (group == 9 && IsEmpty(9)) { InputHelper.SendText("("); return; }
        if (group == 10 && IsEmpty(10)) { InputHelper.SendText(")"); return; }
        Show(group);
    }

    /// <summary>显示指定菜单组（已打开的菜单先关闭）。空组为 no-op。</summary>
    public static void Show(int groupIndex)
    {
        CloseCurrent();
        var g = _groups[groupIndex];
        if (g == null) return;
        _currentGroup = groupIndex;
        // 延迟到下一 Dispatcher 周期再创建并显示窗口：
        // 让上一组菜单的淡出/失焦先完成，并显式 Activate 使其成为前台窗口，
        // 避免切换组后新菜单因前台权限/时序竞争未能激活，导致点击外部不触发
        // Deactivated 而关不掉（与 HelpPanel 同一类激活竞态，见 Features/HelpPanel.cs）。
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_currentGroup != groupIndex) return; // 期间已切换到别的组，丢弃本次
            var menu = new MenuPopupWindow(g.Name, groupIndex, g.Items.Select(ActionRegistry.DisplayName).ToList());
            // 仅当关闭的仍是当前活跃菜单时才清引用，避免旧窗口淡出动画完成时误清新窗口
            menu.Closed += (_, _) => { if (_current == menu) _current = null; };
            _current = menu;
            _current.Show();
            _current.Activate();
        }));
    }

    /// <summary>菜单内按键路由（由全局钩子调用，不依赖窗口焦点）：
    /// 数字 1~9/0 → 选第 N 项（超范围→关菜单）；Esc → 关菜单。对齐 AHK #HotIf WinActive(menu) 的数字热键。</summary>
    public static bool HandleMenuKey(ushort vk, bool isDown)
    {
        if (!IsMenuOpen || !isDown) return false;
        if (vk >= '0' && vk <= '9')
        {
            int idx = (vk == '0') ? 10 : (vk - '0');
            var g = _groups[_currentGroup];
            if (g != null && idx >= 1 && idx <= g.Items.Count)
                SelectItem(_currentGroup, idx);
            else
                CloseCurrent();
            return true;
        }
        if (vk == Win32.VkEscape)
        {
            CloseCurrent();
            return true;
        }
        return false;
    }

    /// <summary>关闭当前菜单（若存在）。</summary>
    public static void CloseCurrent()
    {
        if (_current != null)
        {
            try { _current.Close(); } catch { /* 关闭失败静默 */ }
        }
        _current = null;
    }

    /// <summary>执行菜单项（按动作 Id 引用解析后交 ActionExecutor）并关闭菜单。</summary>
    public static void SelectItem(int groupIndex, int itemIndex)
    {
        var g = _groups[groupIndex];
        if (g == null || itemIndex < 1 || itemIndex > g.Items.Count) return;
        string actionId = g.Items[itemIndex - 1];
        CloseCurrent();
        var action = ActionRegistry.FindById(actionId);
        if (action == null)
        {
            CrashLog.Write("MenuSystem", new InvalidOperationException($"菜单项引用的动作不存在: {actionId}"));
            return;
        }
        ActionExecutor.Run(action);
    }

    // —— 设置 CRUD（由 Views.ConfigHelperWindow 调用）——

    /// <summary>取得 1..N 的菜单组（可能为 null）。</summary>
    public static MenuGroup? GetGroup(int groupIndex) =>
        (groupIndex >= 1 && groupIndex <= MaxGroups) ? _groups[groupIndex] : null;

    /// <summary>在首个空槽添加菜单组，返回槽位索引；无空槽返回 -1。</summary>
    public static int AddGroup(string name)
    {
        for (int i = 1; i <= MaxGroups; i++)
            if (_groups[i] == null)
            {
                _groups[i] = new MenuGroup(name, new List<string>());
                return i;
            }
        return -1;
    }

    /// <summary>修改组名（保留原项目）。</summary>
    public static void EditGroup(int groupIndex, string name)
    {
        var g = GetGroup(groupIndex);
        if (g == null) return;
        _groups[groupIndex] = new MenuGroup(name, g.Items);
    }

    /// <summary>删除菜单组（置空槽位）。</summary>
    public static void DeleteGroup(int groupIndex)
    {
        if (groupIndex >= 1 && groupIndex <= MaxGroups) _groups[groupIndex] = null;
    }

    /// <summary>在组末尾添加菜单项（动作 Id 引用）。</summary>
    public static void AddItem(int groupIndex, string actionId)
    {
        var g = GetGroup(groupIndex);
        if (g == null) return;
        g.Items.Add(actionId);
    }

    /// <summary>修改指定菜单项的动作 Id 引用。</summary>
    public static void EditItem(int groupIndex, int itemIndex, string actionId)
    {
        var g = GetGroup(groupIndex);
        if (g == null || itemIndex < 0 || itemIndex >= g.Items.Count) return;
        g.Items[itemIndex] = actionId;
    }

    /// <summary>删除指定菜单项。</summary>
    public static void DeleteItem(int groupIndex, int itemIndex)
    {
        var g = GetGroup(groupIndex);
        if (g == null || itemIndex < 0 || itemIndex >= g.Items.Count) return;
        g.Items.RemoveAt(itemIndex);
        if (g.Items.Count == 0) _groups[groupIndex] = null; // 空组视为不存在
    }

    /// <summary>移动菜单项；delta=-1 上移 / +1 下移；返回是否实际移动。</summary>
    public static bool MoveMenuItem(int groupIndex, int itemIndex, int delta)
    {
        var g = GetGroup(groupIndex);
        if (g == null) return false;
        int ni = itemIndex + delta;
        if (ni < 0 || ni >= g.Items.Count) return false;
        (g.Items[itemIndex], g.Items[ni]) = (g.Items[ni], g.Items[itemIndex]);
        return true;
    }

    /// <summary>从 JSON 重新加载全部组 + 动作清单（关闭已开菜单）。</summary>
    public static void ReloadFromConfig(string? configPath)
    {
        _loadedConfigPath = null; // 清除缓存，强制重新加载
        CloseCurrent();
        Load(configPath);
    }

    /// <summary>将当前 10 个组写回 JSON（读现有配置→替换菜单部分→整体写回，保留动作/终端等其它配置）。</summary>
    public static void SaveToConfig(string configPath)
    {
        var cfg = AppConfig.Load(configPath);
        cfg.MenuGroups = new List<MenuGroupDto?>();
        for (int i = 1; i <= MaxGroups; i++)
            cfg.MenuGroups.Add(ToDto(_groups[i]));
        cfg.Save(configPath);
    }

    private static MenuGroupDto? ToDto(MenuGroup? g)
    {
        if (g == null) return null;
        return new MenuGroupDto
        {
            Enabled = true,
            Name = g.Name,
            Items = g.Items.ToList(),
        };
    }

    // —— 数据模型 ——
    internal sealed class MenuGroup
    {
        public string Name { get; }
        public List<string> Items { get; }  // 动作 Id 引用列表
        public MenuGroup(string name, List<string> items) { Name = name; Items = items; }
    }
}
