using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Config;
using LTools.Core;
using LTools.Features.Panel;
using LTools.Native;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 面板里「把文件 / 文件夹变成动作」的入口：右键菜单两项、<c>Ctrl+V</c>。
/// </summary>
/// <remarks>
/// 真「拖进面板」要等面板搬进普通权限进程（见 <c>docs/PROCESS_SPLIT.md</c>）：提权进程的窗口
/// 收不到资源管理器的拖放。这三个入口共用 <see cref="PanelDrop"/>，落位与磁盘写入也只有一份。
/// </remarks>
public partial class SuperPanelWindow
{
    /// <summary>从文件创建动作（可多选），落到 <paramref name="group"/> 末尾。</summary>
    private void AddFilesFor(PanelGroupDto group)
    {
        var dialog = new OpenFileDialog
        {
            Title = "从文件创建动作",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;
        AddPathsTo(group, dialog.FileNames);
    }

    /// <summary>从文件夹创建动作，落到 <paramref name="group"/> 末尾。</summary>
    private void AddFolderFor(PanelGroupDto group)
    {
        var dialog = new OpenFolderDialog { Title = "从文件夹创建动作" };

        if (dialog.ShowDialog(this) != true) return;
        AddPathsTo(group, new[] { dialog.FolderName });
    }

    /// <summary>
    /// <c>Ctrl+V</c>：剪贴板里的文件 / 文件夹 → 动作，落到光标下的分组（不在分组里就落到第一个）。
    /// </summary>
    /// <returns>剪贴板里没有文件时返回 false，让按键照常下传。</returns>
    private bool PasteFilesFromClipboard()
    {
        if (!NativeClipboard.TryGetFileList(out List<string> files) || files.Count == 0) return false;

        PanelGroupDto? group = GroupAt(Mouse.GetPosition(this)) ?? ZoneGroups(global: true).FirstOrDefault();
        if (group == null)
        {
            ToastService.Show("先新建一个分组，再往里放动作", "面板", 2600, ToastKind.warning,
                key: "panel:dropfiles");
            return true;
        }

        AddPathsTo(group, files);
        return true;
    }

    /// <summary>建动作 → 落进分组 → 重建面板 → 一条 toast（创建 / 已有 / 找不到各说一句）。</summary>
    private void AddPathsTo(PanelGroupDto group, IReadOnlyList<string> paths)
    {
        var known = PanelDrop.ExistingPaths();
        PanelDrop.Result result = PanelDrop.Build(paths, known.Contains);
        int added = PanelDrop.Insert(result.Actions, group, group.Actions.Count);

        if (added > 0) RebuildAll();

        var parts = new List<string>();
        if (added > 0) parts.Add($"已添加 {added} 个动作到「{group.Name}」");
        if (result.Duplicates.Count > 0) parts.Add($"{result.Duplicates.Count} 个已经在面板里");
        if (result.Missing.Count > 0) parts.Add($"{result.Missing.Count} 个找不到");

        string text = parts.Count > 0 ? string.Join(" · ", parts) : "没有可添加的文件";
        ToastService.Show(text, "面板", added > 0 ? 2600 : 3200,
            added > 0 ? ToastKind.success : ToastKind.warning, key: "panel:dropfiles");
    }
}
