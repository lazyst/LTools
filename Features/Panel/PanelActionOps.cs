using System;
using System.Windows;
using LTools.Config;
using LTools.Views;

namespace LTools.Features.Panel;

/// <summary>
/// 面板入口的增删改（动作本体仍由 <see cref="ConfigStore"/> 管理）。
/// 面板窗口与设置「面板管理」页共用，保证两处的「新建 / 选择 / 编辑 / 删除」行为一致。
/// </summary>
/// <remarks>
/// 面板窗口是「用完即收」的：调用方在打开对话框前应先关面板，并把 <c>owner</c> 传 null；
/// 设置页则传自己的窗口，让对话框居中在设置窗口上。
/// </remarks>
internal static class PanelActionOps
{
    /// <summary>新建动作并加入分组；返回新动作（取消返回 null）。</summary>
    public static ActionDto? AddNew(Window? owner, PanelGroupDto group)
    {
        var dto = ActionEditor.Show(owner, "新建动作", null);

        if (dto == null) return null;
        ConfigStore.AddAction(dto);
        ConfigStore.Save();
        group.Actions.Add(dto.Id);
        PanelLibrary.Save();
        return dto;
    }

    /// <summary>从动作池选一个已有动作加入分组。</summary>
    public static bool AddExisting(Window? owner, PanelGroupDto group)
    {
        var picker = new ActionPoolPicker(owner, "面板分组 — 选择动作", null);
        picker.ShowDialog();
        if (picker.Result == null) return false;
        group.Actions.Add(picker.Result);
        PanelLibrary.Save();
        return true;
    }

    /// <summary>编辑分组里的动作；返回是否发生了修改。</summary>
    public static bool Edit(Window? owner, string id)
    {
        var cur = ActionRegistry.FindById(id);
        if (cur == null) return false;

        var dto = ActionEditor.Show(owner, "编辑动作", cur);
        if (dto == null) return false;
        dto.Id = cur.Id;
        ConfigStore.UpdateAction(dto);
        ConfigStore.Save();
        return true;
    }

    /// <summary>删除动作（确认框列出面板引用位置）；返回是否删除。</summary>
    public static bool Delete(Window? owner, string id)
    {
        string name = ActionRegistry.DisplayName(id);
        string refs = PanelLibrary.DescribeReferences(id);
        if (!ConfirmDialog.Confirm(owner, "删除动作",
            $"确定删除「{name}」吗？\n\n面板引用：{refs}\n菜单项 / 组合步骤中的引用也会一并清除。",
            danger: true))
            return false;

        ConfigStore.RemoveAction(id);
        return true;
    }

    /// <summary>把动作从分组里移除（不删除动作本体）。</summary>
    public static void RemoveFromGroup(PanelGroupDto group, int index)
    {
        if (index < 0 || index >= group.Actions.Count) return;
        group.Actions.RemoveAt(index);
        PanelLibrary.Save();
    }
}
