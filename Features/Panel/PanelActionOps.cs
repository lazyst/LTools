using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Storage;
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
        ConfigStore.NotifyChanged();   // 面板结构变了，已打开的设置页要跟着刷
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
        if (!ConfirmDialog.Confirm(owner, "删除动作", ActionRemoval.ConfirmMessage(id), danger: true))
            return false;

        ConfigStore.RemoveAction(id);
        return true;
    }

    /// <summary>
    /// 导出单个动作（<c>.ltact</c>）：打包 <c>actions/&lt;id&gt;/</c>（action.json + files/）。
    /// 导出在后台线程跑，完成后用 toast 报告路径（失败常驻并可点击开日志目录）。
    /// </summary>
    public static void Export(Window? owner, string id)
    {
        var action = ActionRegistry.FindById(id);
        if (action == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "LTools 动作 (*.ltact)|*.ltact",
            FileName = SafeFileName(action.Name) + BackupService.ActionExtension,
            DefaultExt = BackupService.ActionExtension,
            AddExtension = true,
            Title = "导出动作",
        };
        if (dlg.ShowDialog(owner) != true) return;

        string dest = dlg.FileName;
        string name = action.Name;
        Task.Run(() => BackupService.ExportAction(id, dest)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                string msg = t.Exception?.GetBaseException().Message ?? "未知错误";
                ToastService.Show($"导出失败：{msg}", name, ToastService.StickyMs, ToastKind.error,
                    onClick: () => { try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataRoot); } catch { } },
                    actionText: "打开数据目录");
            }
            else
            {
                ToastService.Show($"已导出到 {dest}", name, 3000, ToastKind.success);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 子动作 → 新动作：先转成动作草稿，再在动作编辑器里让用户确认名称 / 图标后保存。
    /// 变量方向一律归「本地」（子动作的输入 / 输出是它独有的概念，见 <see cref="SubActionTransfer"/>）。
    /// </summary>
    public static bool ToAction(Window? owner, SubActionDef def)
    {
        var dto = SubActionTransfer.ToAction(def);
        dto.Id = ActionRegistry.NextId();

        var saved = ActionEditor.Show(owner, "子动作另存为动作", dto);
        if (saved == null) return false;

        if (!ConfigStore.AddAction(saved)) return false;
        ConfigStore.Save();

        ToastService.Show($"已新建动作「{saved.Name}」，在动作管理里能看到它。",
            "另存为动作", 3000, ToastKind.success);
        return true;
    }

    /// <summary>
    /// 动作 → 新子动作：先转成子动作定义（变量归「本地」），再在子动作编辑器里让用户标输入 / 输出。
    /// 动作本体不动，两边之后各走各的。
    /// </summary>
    public static bool ToSubAction(Window? owner, string id)
    {
        var action = ActionRegistry.FindById(id);
        if (action == null) return false;

        var def = SubActionTransfer.FromAction(action, SubActionStore.NewId());
        var dlg = ActionEditorWindow.ForSubAction("动作另存为子动作", def);
        dlg.Owner = owner;
        if (dlg.ShowDialog() != true || dlg.SubResult == null) return false;

        try
        {
            SubActionStore.Save(dlg.SubResult);
        }
        catch (ActionException ex)
        {
            ConfirmDialog.Info(owner, "另存为子动作", ex.Message);
            return false;
        }

        ConfigStore.NotifyChanged();   // 子动作库已建页时跟着刷新
        ToastService.Show(
            $"已新建子动作「{dlg.SubResult.Name}」（{dlg.SubResult.Id}），在设置 → 子动作库里能看到它。",
            "另存为子动作", 3000, ToastKind.success);
        return true;
    }

    /// <summary>文件名里去掉 Windows 不允许的字符（动作名可能带 <c>/</c>、冒号等）。</summary>
    private static string SafeFileName(string name)
    {
        string cleaned = name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned.Length == 0 ? "action" : cleaned;
    }

    /// <summary>把动作从分组里移除（不删除动作本体）。</summary>
    public static void RemoveFromGroup(PanelGroupDto group, int index)
    {
        if (index < 0 || index >= group.Actions.Count) return;
        group.Actions.RemoveAt(index);
        PanelLibrary.Save();
    }
}
