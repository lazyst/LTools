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

/// <summary>ConfigHelperWindow 的「备份 / 恢复」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 备份 / 恢复 ============
    private async void BackupExport_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        bool res = BackupIncludeResourcesBox.IsChecked == true;
        bool notes = BackupIncludeNotesBox.IsChecked == true;

        var dlg = new SaveFileDialog
        {
            Filter = "LTools 备份 (*.ltbak)|*.ltbak",
            FileName = $"LTools-backup-{DateTime.Now:yyyyMMdd-HHmmss}",
            Title = "导出备份",
        };
        if (dlg.ShowDialog(this) != true) return;

        long size;
        try { size = await Task.Run(() => BackupService.EstimateSize(res, notes)); }
        catch (Exception ex)
        {
            ShowStatus("导出失败", ok: false);
            ConfirmDialog.Info(this, "备份", "导出失败: " + ex.Message);
            return;
        }
        if (size > BackupService.ExportWarnBytes
            && !ConfirmDialog.Confirm(this, "导出", $"备份约 {size / 1024.0 / 1024.0:F1} MB，确定导出吗？", okText: "确认导出"))
            return;

        _backupBusy = true;
        BackupStatusText.Text = "正在导出…";
        try
        {
            await Task.Run(() => BackupService.Export(dlg.FileName, res, notes));
            ShowStatus($"已导出到 {dlg.FileName}", ok: true);
            BackupStatusText.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            ShowStatus("导出失败", ok: false);
            BackupStatusText.Text = "导出失败";
            ConfirmDialog.Info(this, "备份", "导出失败: " + ex.Message);
        }
        finally { _backupBusy = false; }
    }

    private async void BackupImport_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        var ofd = new OpenFileDialog
        {
            Filter = "LTools 备份 (*.ltbak)|*.ltbak",
            Title = "导入备份",
        };
        if (ofd.ShowDialog(this) != true) return;

        _backupBusy = true;
        BackupStatusText.Text = "正在读取备份…";
        BackupService.ImportPreview? preview = null;
        try
        {
            preview = await Task.Run(() => BackupService.Inspect(ofd.FileName));

            var msg = new StringBuilder();
            msg.AppendLine($"备份包含 {preview.ExecutableCount} 个可执行文件，并将替换当前配置。");
            if (preview.ExecutableCount > 0)
            {
                msg.AppendLine("导入后以下动作可能以管理员权限执行其中脚本：");
                if (preview.ResourceActionRefs.Count == 0)
                    msg.AppendLine("  （未发现引用 resources 的动作）");
                else
                    foreach (var r in preview.ResourceActionRefs) msg.AppendLine("  • " + r);
            }
            msg.Append("仅导入来源可信的备份。继续？");
            if (!ConfirmDialog.Confirm(this, "导入备份", msg.ToString(), okText: "确认导入"))
                return;

            BackupStatusText.Text = "正在导入…";
            await Task.Run(() => BackupService.Import(preview));
            // 运行期状态（动作 / 快捷键 / AppState / 宏 / 触发 / 面板）整份重读，别在这里逐项再抄一遍
            ConfigStore.Reload();
            RefreshCreatedPages();
            // 日志是进程级状态：Reload 里的 ApplyLogging 只在执行器跑，宿主这份自己配
            // （AppLog.Configure 不传 fileTag = 沿用当前的 host 标签，别写回共用的那份日志文件）
            var logCfg = SettingsStore.Read(ConfigPath).Logging ?? new LogConfig();
            AppLog.Configure(logCfg.Enabled, logCfg.Level, logCfg.RetentionDays);
            PopulateLogging();
            _initializing = true;
            PopulateToggles();
            _initializing = false;
            ShowStatus("导入完成", ok: true);
            BackupStatusText.Text = "导入完成（旧数据已备份为 .bak-时间戳）";
        }
        catch (Exception ex)
        {
            ShowStatus("导入失败", ok: false);
            BackupStatusText.Text = "导入失败";
            ConfirmDialog.Info(this, "备份", "导入失败: " + ex.Message);
        }
        finally
        {
            BackupService.Cleanup(preview);
            _backupBusy = false;
        }
    }

}
