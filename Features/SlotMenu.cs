using System;
using System.Windows;
using System.Windows.Controls;
using CapsLockPro.Config;

namespace CapsLockPro.Features;

/// <summary>
/// 超级面板槽位「添加动作」共享菜单（计划 §5.5/§5.6 统一）：
/// 面板空格右键 / 设置页格子左键（弹出）与右键（ContextMenu）共用同一结构，
/// 保证两处「添加动作」的交互完全一致：
/// 新建动作… / 新建组合动作… / 快捷新建（5 类） / 从动作池选择… / 清除（可选，仅非空槽位）。
/// </summary>
public static class SlotMenu
{
    /// <param name="owner">菜单宿主窗口（用其 Dispatcher 把动作推迟到菜单关闭后执行）。</param>
    /// <param name="slot">槽位标识（宿主自行解释；设置页可闭包绑定页号）。</param>
    /// <param name="onNew">新建动作（type=null 表示弹出完整类型选择器；否则为快捷新建的预置类型）。</param>
    /// <param name="onPickExisting">从动作池选择已有动作。</param>
    /// <param name="onClear">清除槽位；null 表示不显示「清除」（如空格子）。</param>
    public static ContextMenu BuildAddMenu(Window owner, int slot,
        Action<int, ActionType?> onNew,
        Action<int> onPickExisting,
        Action<int>? onClear)
    {
        var menu = new ContextMenu();

        // 菜单项 Click 内同步打开模态对话框会与「菜单正在关闭」冲突（ShowDialog 可能不显示），
        // 故统一推迟到下一 Dispatcher 周期执行。
        void Add(string header, Action invoke)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => owner.Dispatcher.InvokeAsync(() => invoke());
            menu.Items.Add(mi);
        }

        Add("新建动作…", () => onNew(slot, null));
        Add("新建组合动作…", () => onNew(slot, ActionType.composite));
        menu.Items.Add(new Separator());
        Add("新建 · 启动软件", () => onNew(slot, ActionType.launchApp));
        Add("新建 · 打开文件", () => onNew(slot, ActionType.openFile));
        Add("新建 · 打开文件夹", () => onNew(slot, ActionType.openFolder));
        Add("新建 · 运行命令", () => onNew(slot, ActionType.runCommand));
        Add("新建 · 打开网址", () => onNew(slot, ActionType.openUrl));
        menu.Items.Add(new Separator());
        Add("从动作池选择…", () => onPickExisting(slot));
        if (onClear != null) Add("清除", () => onClear(slot));

        return menu;
    }
}
