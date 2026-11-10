using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 设置 →「日志」页：日志开关与级别、文件入口、最近日志预览、复制诊断信息。
/// </summary>
/// <remarks>
/// 从 <c>ConfigHelper.xaml.cs</c> 按职责拆出来（主文件已经过千行）。这里只管日志页，
/// 别的页面不碰；「哪些文件算日志、多大滚动、怎么读尾巴」统一走 <see cref="LogFiles"/>，
/// 别在 UI 里另写一套。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>预览最多显示多少行（读的时候多读一些，过滤完再截断）。</summary>
    private const int PreviewLines = 200;

    /// <summary>预览读取的上限：足够覆盖「过滤后还剩 200 行」的情况，又不至于整份 20 MB 都读进来。</summary>
    private const int PreviewScanLines = 5000;

    private bool _loadingLog;            // 日志页控件回填期间抑制保存事件

    // ============ 设置回填与保存 ============

    /// <summary>把配置里的日志设置回填到控件（回填期间不触发保存）。</summary>
    private void PopulateLogging()
    {
        var log = ConfigStore.LoadConfig().Logging ?? new LogConfig();

        _loadingLog = true;
        LogEnabledBox.IsChecked = log.Enabled;
        LogLevelCombo.SelectedIndex = log.Level switch
        {
            LogLevel.error => 0,
            LogLevel.debug => 2,
            _ => 1,
        };
        LogRetentionCombo.SelectedIndex = log.RetentionDays switch
        {
            3 => 0,
            7 => 1,
            14 => 2,
            30 => 3,
            0 => 4,
            _ => 2,
        };
        _loadingLog = false;
        RefreshLogPage(log);
        RefreshLogPreview();
    }

    private void LogOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLog) return;

        var cfg = new LogConfig
        {
            Enabled = LogEnabledBox.IsChecked == true,
            Level = LogLevelCombo.SelectedIndex switch
            {
                0 => LogLevel.error,
                2 => LogLevel.debug,
                _ => LogLevel.info,
            },
            RetentionDays = LogRetentionCombo.SelectedIndex switch
            {
                0 => 3,
                1 => 7,
                2 => 14,
                3 => 30,
                _ => 0,
            },
        };

        SettingsStore.Modify(ConfigPath, s => s.Logging = cfg);
        AppLog.Configure(cfg.Enabled, cfg.Level, cfg.RetentionDays);
        if (cfg.Enabled)
            AppLog.Info("Settings", $"日志已更新：级别={cfg.Level}，保留={cfg.RetentionDays} 天");
        RefreshLogPage(cfg);
        RefreshLogPreview();
        ShowStatus(cfg.Enabled ? "日志设置已保存" : "运行日志已关闭", ok: true);
    }

    private void RefreshLogPage(LogConfig log)
    {
        string dir = AppPaths.LogsDir;
        LogPathText.Text = "文件夹：" + dir
                           + "\n今天的日志：" + Path.Combine(dir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
        string level = log.Level switch
        {
            LogLevel.error => "仅错误",
            LogLevel.debug => "详细（含变量值）",
            _ => "常规",
        };
        string keep = log.RetentionDays > 0 ? $"{log.RetentionDays} 天" : "不自动清理";
        LogStatusText.Text = log.Enabled
            ? $"当前：已启用 · 级别 {level} · 保留 {keep}"
            : "当前：已关闭，不会写入新的日志（已有文件保留）。";
        LogLevelHint.Text = log.Level == LogLevel.debug
            ? "详细级别会记录每一步的执行细节与取值结果，可能包含剪贴板 / 选中文本等敏感内容；只在本机 logs 文件夹里。"
            : "常规级别记录动作与流程的执行、设置变更和异常，不记录文本正文；排查具体某一步时再临时改用「详细」。";
    }

    // ============ 最近日志预览 ============

    private void LogPreview_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLog || LogPreviewBox == null) return;
        RefreshLogPreview();
    }

    private void LogPreviewRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshLogPreview();
        ShowStatus("日志预览已刷新", ok: true);
    }

    /// <summary>把今天的运行日志按级别 / 关键字过滤后显示在预览框里。</summary>
    private void RefreshLogPreview()
    {
        string file = Path.Combine(AppPaths.LogsDir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
        string[] lines = LogFiles.ReadTail(file, PreviewScanLines);

        // 级别下拉：0 全部 / 1 错误 / 2 警告 / 3 信息 / 4 调试。日志行里的级别标签是定宽的。
        string tag = LogPreviewLevelCombo.SelectedIndex switch
        {
            1 => "[ERROR]",
            2 => "[WARN ]",
            3 => "[INFO ]",
            4 => "[DEBUG]",
            _ => "",
        };
        string search = LogPreviewSearchBox.Text.Trim();

        var kept = LogFiles.KeepEntries(lines, line =>
            (tag.Length == 0 || line.Contains(tag, StringComparison.Ordinal))
            && (search.Length == 0 || line.Contains(search, StringComparison.OrdinalIgnoreCase)));

        int total = kept.Count;
        if (kept.Count > PreviewLines) kept = kept.Skip(kept.Count - PreviewLines).ToList();

        LogPreviewBox.Text = kept.Count > 0
            ? string.Join(Environment.NewLine, kept)
            : lines.Length == 0
                ? "（今天还没有日志）"
                : "（没有匹配的日志行）";
        LogPreviewBox.ScrollToEnd();

        string scope = (tag.Length > 0 || search.Length > 0) ? $"匹配 {total} 行" : $"共 {lines.Length} 行";
        LogPreviewHint.Text = $"{scope}，显示最后 {kept.Count} 行（最多 {PreviewLines} 行）。"
                              + (File.Exists(FailureLog.LatestPath) ? "最近一次失败的完整报告在「打开失败报告」里。" : "");
    }

    // ============ 文件入口 ============

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogsDir) { UseShellExecute = true });
            ShowStatus("已打开日志文件夹", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "打开日志文件夹失败：" + ex.Message);
        }
    }

    private void OpenTodayLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string file = Path.Combine(AppPaths.LogsDir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(file)) File.WriteAllText(file, "");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "打开日志文件失败：" + ex.Message);
        }
    }

    private void CopyLogPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppPaths.LogsDir);
            ShowStatus("日志文件夹路径已复制", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "复制失败：" + ex.Message);
        }
    }

    /// <summary>打开最近一次失败的完整报告（轨迹 + 相关日志）；还没有就说明一句。</summary>
    private void OpenFailureReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = FailureLog.LatestPath;
            if (!File.Exists(report))
            {
                ConfirmDialog.Info(this, "运行日志", "还没有失败报告。动作失败一次后，这里会有含逐步轨迹的报告。");
                return;
            }
            Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "打开失败报告失败：" + ex.Message);
        }
    }

    // ============ 复制诊断信息 ============

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildDiagnostics());
            ShowStatus("诊断信息已复制到剪贴板", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "复制失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 一段可以直接贴给 AI 或提 issue 的 markdown：版本 / 系统 / 数据根 / 日志设置 /
    /// 配置规模 / 最近一次失败 / 最近的错误与警告。
    /// </summary>
    private string BuildDiagnostics()
    {
        var cfg = ConfigStore.LoadConfig();
        var log = cfg.Logging ?? new LogConfig();
        var sb = new StringBuilder();

        sb.AppendLine("## LTools 诊断信息");
        sb.AppendLine();
        sb.AppendLine($"- 版本：{typeof(ConfigHelperWindow).Assembly.GetName().Version?.ToString(3) ?? "?"}");
        sb.AppendLine($"- 系统：{RuntimeInformation.OSDescription} · {(Environment.Is64BitProcess ? "64 位" : "32 位")}");
        sb.AppendLine($"- 运行时：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- 数据根：{AppPaths.DataRoot}");
        sb.AppendLine($"- 日志：{(log.Enabled ? "开启" : "关闭")} · 级别 {log.Level} · " +
                      $"保留 {(log.RetentionDays > 0 ? log.RetentionDays + " 天" : "不清理")}");

        int bindings = cfg.Hotkeys.Groups.Sum(g => g.Bindings.Count);
        sb.AppendLine($"- 配置：动作 {cfg.Actions.Count} · 快捷键 {cfg.Hotkeys.Groups.Count} 组 / {bindings} 条 · " +
                      $"宏 {cfg.Macros.Items.Count} · 草稿 {DraftStore.List().Count}");
        sb.AppendLine();

        // 失败报告的头部就是「时间 / 运行 / 动作 / 位置 / 错误 / 步数」，取前几行足够。
        string[] report = LogFiles.ReadTail(FailureLog.LatestPath, 200);
        sb.AppendLine("## 最近一次失败");
        if (report.Length == 0)
        {
            sb.AppendLine("（没有失败报告）");
        }
        else
        {
            foreach (string line in report.Take(10))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                sb.AppendLine(line);
            }
        }
        sb.AppendLine();

        // 最近的错误 / 警告：比「整份日志」小得多，贴出来也看得完。多行记录一起带上。
        string today = Path.Combine(AppPaths.LogsDir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
        var recent = LogFiles.KeepEntries(LogFiles.ReadTail(today, PreviewScanLines),
                l => l.Contains("[ERROR]", StringComparison.Ordinal) || l.Contains("[WARN ]", StringComparison.Ordinal))
            .TakeLast(30)
            .ToArray();
        sb.AppendLine($"## 最近的错误与警告（{recent.Length} 行）");
        if (recent.Length == 0) sb.AppendLine("（没有）");
        else foreach (string line in recent) sb.AppendLine(line);

        return sb.ToString();
    }
}
