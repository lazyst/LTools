using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Core.Storage;
using LTools.Features.Builtin;
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
        var dto = ActionEditor.Show(owner, "新建组合动作", null);
        if (dto == null) return null;

        if (!ActionWrites.Add(dto, out string? error))
        {
            ConfirmDialog.Info(owner, "新建组合动作", error);
            return null;
        }

        group.Actions.Add(dto.Id);
        PanelLibrary.Save();
        return dto;
    }

    /// <summary>新建一个简单动作并加入分组；取消返回 null。</summary>
    public static ActionDto? AddSimple(Window? owner, PanelGroupDto group, SimpleActions.Kind kind)
    {
        var edit = SimpleActionDialog.Show(owner, kind, null);
        return edit == null ? null : AddSimple(owner, group, kind, edit);
    }

    /// <summary>按表单结果建动作并放进分组（对话框之外的那一半，冒烟直接调）。</summary>
    internal static ActionDto? AddSimple(Window? owner, PanelGroupDto group, SimpleActions.Kind kind,
        SimpleActionEdit edit)
    {
        var dto = new ActionDto
        {
            Name = edit.Name,
            Icon = kind.Icon,
            SimpleKind = kind.Key,
            Program = SimpleActions.Build(kind, edit.Values),
        };
        dto.Program.Name = dto.Name;
        dto.Program.Icon = dto.Icon;

        if (!ActionWrites.Add(dto, out string? error))
        {
            ConfirmDialog.Info(owner, "新建简单动作", error ?? "动作没能保存");
            return null;
        }

        // 动作体里的 Id / 名称 / 图标跟外层对齐（与 PanelDrop.Insert 同一条约定）
        dto.Program.Id = dto.Id;
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

    /// <summary>复制引用：剪贴板里放「同一个动作再放一处」，粘到别的分组 / 场景后两边永远是同一个动作。</summary>
    public static void CopyReference(string reference)
    {
        var entry = ActionClipboard.CopyReference(reference);
        ToastService.Show("已复制引用；到目标分组右键「粘贴」或「粘贴到…」", entry.Name,
            2500, ToastKind.success, key: "panel:clipboard");
    }

    /// <summary>复制为副本：剪贴板里放「粘贴时现复制一份」，副本带新的 Id / 名字与它自己的 files/。</summary>
    public static void CopyCopy(string actionId)
    {
        var entry = ActionClipboard.CopyCopy(actionId);
        ToastService.Show("已复制为副本；粘贴时会新建一个动作", entry.Name,
            2500, ToastKind.success, key: "panel:clipboard");
    }

    /// <summary>
    /// 把剪贴板里的那一份粘进分组：引用 = 再加一颗磁贴（同一个动作）；
    /// 副本 = 现复制一份新动作（<c>files/</c> 一并带走）再加磁贴。
    /// </summary>
    /// <param name="group">目标分组。</param>
    /// <param name="index">插到第几颗前面；<c>-1</c> 或越界 = 追加到末尾。</param>
    /// <returns>是否真的粘了（剪贴板为空 / 源已被删返回 false）。</returns>
    public static bool Paste(PanelGroupDto group, int index = -1)
    {
        var entry = ActionClipboard.Entry;
        if (entry == null || group == null) return false;

        return PasteEntry(group, index, entry.Kind == ActionClipKind.Copy, entry.Reference, entry.Name);
    }

    /// <summary>
    /// 把一条剪贴板条目粘进分组（引用 = 再加一颗磁贴；副本 = 现复制一份新动作）。
    /// 源已经不在了（动作被删 / 内置功能下线）就清空剪贴板并提示——留着一个粘不动的东西只会误导。
    /// </summary>
    internal static bool PasteEntry(PanelGroupDto group, int index, bool copy, string reference, string name)
    {
        if (group == null || string.IsNullOrEmpty(reference)) return false;

        if (!copy)
        {
            if (!BindingRef.Exists(reference))
            {
                ActionClipboard.Clear();
                ToastService.Show($"「{name}」已经不存在了，剪贴板已清空", "粘贴", 4000, ToastKind.error);
                return false;
            }

            InsertAt(group, index, reference);
            PanelLibrary.Save();
            ConfigStore.NotifyChanged();   // 面板结构变了，已打开的设置页要跟着刷
            ToastService.Show($"已在「{group.Name}」里加了一份引用", name,
                2000, ToastKind.success, key: "panel:paste");
            return true;
        }

        var copyDto = ActionWrites.Duplicate(reference, out string? error);
        if (copyDto == null)
        {
            // 只有「源真的没了」才清；链路超时这类失败留着，重试还能粘上。
            if (!BindingRef.Exists(reference)) ActionClipboard.Clear();
            ToastService.Show(error ?? "原动作已经不存在了", "粘贴副本", 4000, ToastKind.error);
            return false;
        }

        InsertAt(group, index, copyDto.Id);
        PanelLibrary.Save();
        ConfigStore.NotifyChanged();
        ToastService.Show($"已复制为「{copyDto.Name}」并放进「{group.Name}」", "粘贴副本",
            3000, ToastKind.success, key: "panel:paste");
        return true;
    }

    /// <summary>把引用插进分组（越界 / -1 = 追加）。</summary>
    private static void InsertAt(PanelGroupDto group, int index, string reference)
    {
        if (index < 0 || index > group.Actions.Count) group.Actions.Add(reference);
        else group.Actions.Insert(index, reference);
    }

    /// <summary>编辑分组里的动作；返回是否发生了修改。简单动作开极简表单，组合动作开编辑器。</summary>
    public static bool Edit(Window? owner, string id)
    {
        var cur = ActionRegistry.FindById(id);
        if (cur == null) return false;

        if (SimpleActions.KindOf(cur) is { } kind) return EditSimple(owner, cur, kind);

        var dto = ActionEditor.Show(owner, "编辑动作", cur);
        if (dto == null) return false;
        dto.Id = cur.Id;

        if (!ActionWrites.Update(dto, out string? error))
        {
            ConfirmDialog.Info(owner, "保存动作", error);
            return false;
        }
        return true;
    }

    private static bool EditSimple(Window? owner, ActionDto cur, SimpleActions.Kind kind)
    {
        var edit = SimpleActionDialog.Show(owner, kind, cur);
        if (edit == null) return false;

        var dto = cur.Clone();
        dto.Name = edit.Name;
        // 拖进来的文件按扩展名挑过图标（.md → note…），编辑时别把它冲回通用「文件」
        dto.Icon = string.IsNullOrEmpty(dto.Icon) ? kind.Icon : dto.Icon;
        dto.Program = SimpleActions.Build(kind, edit.Values, cur.Program);
        dto.Program.Id = dto.Id;
        dto.Program.Name = dto.Name;
        dto.Program.Icon = dto.Icon;

        // 「转换为组合动作」：先按表单改好，再交给编辑器；用户在编辑器里取消就什么都不写。
        if (edit.Convert) return OpenComposite(owner, dto);

        if (!ActionWrites.Update(dto, out string? error))
        {
            ConfirmDialog.Info(owner, "保存简单动作", error ?? "动作没能保存");
            return false;
        }
        return true;
    }

    /// <summary>把简单动作转换为组合动作（清掉预设标记）并打开编辑器；取消返回 false。</summary>
    public static bool ConvertToComposite(Window? owner, string id)
    {
        var cur = ActionRegistry.FindById(id);
        if (cur == null || SimpleActions.KindOf(cur) == null) return false;

        if (!ConfirmDialog.Confirm(owner, "转换为组合动作",
                "转换后只能用组合动作编辑器修改，不能再回到简单动作。\n动作本身、以及它在面板 / 快捷键 / 悬浮里的引用都不变。",
                okText: "转换并编辑"))
            return false;

        return OpenComposite(owner, cur.Clone());
    }

    private static bool OpenComposite(Window? owner, ActionDto dto)
    {
        var edited = ActionEditor.Show(owner, "编辑动作", dto);
        if (edited == null) return false;

        edited.Id = dto.Id;
        edited.SimpleKind = null;
        if (!ActionWrites.Update(edited, out string? error))
        {
            ConfirmDialog.Info(owner, "保存动作", error ?? "动作没能保存");
            return false;
        }
        return true;
    }

    /// <summary>删除动作（确认框列出面板引用位置）；返回是否删除。</summary>
    public static bool Delete(Window? owner, string id)
    {
        if (!ConfirmDialog.Confirm(owner, "删除动作", ActionRemoval.ConfirmMessage(id), danger: true))
            return false;

        if (!ActionWrites.Delete(id, out string? error))
        {
            ConfirmDialog.Info(owner, "删除动作", error);
            return false;
        }
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
                ToastService.Show($"导出失败：{msg}", name, ToastService.LongMs, ToastKind.error,
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

        if (!ActionWrites.Add(saved, out string? addError))
        {
            ConfirmDialog.Info(owner, "另存为动作", addError);
            return false;
        }

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
            SubActionStore.Save(dlg.SubResult, createNew: true);
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
