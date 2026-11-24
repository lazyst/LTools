using System.IO;
using System.Text.RegularExpressions;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 速记协调器（对应原版 lib/QuickNote.ahk，重构后数据模型：每条速记=独立文件，分类=子目录）。
/// 数据层见 <see cref="NoteRepository"/>；UI 见 <see cref="Views.QuickNoteWindow"/>。
/// CapsLock+N 调 <see cref="Toggle"/>；设置保存后调 <see cref="Refresh"/>。
/// </summary>
internal static class QuickNote
{
    private static QuickNoteWindow? _form;
    private static string? _iniPath;
    private static NoteRepository? _repo;

    /// <summary>启动时初始化（算默认目录 + 建仓库 + 迁移根目录散落旧 .txt）。**只有宿主调**。</summary>
    public static void Initialize(string? iniPath)
    {
        _iniPath = iniPath;
        string root = ComputeDefaultDir(iniPath);
        try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
        catch (Exception ex) { CrashLog.Write("QuickNoteInit", ex); }

        _repo = new NoteRepository(root);
        _repo.MigrateRootScatteredNotes();
        _repo.MigrateTxtToMd();
        _repo.CleanDraftAttachments(TimeSpan.FromDays(7));
        _repo.CollectAttachmentGarbage(); // 程序外删 / 改名留下的附件夹、正文里已没引用的图片 → trash（30 天后清）
    }

    /// <summary>重新扫描分类（设置保存后调用；当前仓库即时扫描，此方法主要用于通知已打开窗口刷新）。</summary>
    public static void Refresh()
    {
        if (_form != null) _form.ReloadFromRepository();
    }

    /// <summary>CapsLock+N 切换：已打开则关闭，否则打开。</summary>
    /// <remarks>
    /// 窗口归**普通权限宿主**（<c>docs/PROCESS_SPLIT.md</c> §15.4 第 1 步）：提权进程只把「开关速记」转过去，
    /// 宿主还没连上就先记着（连上补发）——不在提权进程里开第二份，否则 <c>速记/</c> 会有两个写者。
    /// </remarks>
    public static void Toggle()
    {
        if (!ConfigStore.ReadOnly)
        {
            LinkWorker.RequestNote();
            return;
        }

        if (_form != null)
        {
            _form.Close();
            _form = null;
            return;
        }
        if (_repo == null) Initialize(_iniPath);
        _form = new QuickNoteWindow(_repo!);
        _form.ApplyTopmost(ReadTopmost());
        _form.TopmostChanged += (_, on) => SaveTopmost(on);
        _form.Closed += (_, _) => _form = null;
        Views.WindowFront.Show(_form);
    }

    /// <summary>上次的置顶状态（<c>configs/ui.json</c>）；读不到 / 还没初始化就当不置顶。</summary>
    private static bool ReadTopmost()
    {
        if (string.IsNullOrEmpty(_iniPath)) return false;
        try { return UiStore.Read(_iniPath).QuickNoteTopmost; }
        catch (Exception ex) { CrashLog.Write("速记置顶状态读取失败", ex); return false; }
    }

    private static void SaveTopmost(bool value)
    {
        if (string.IsNullOrEmpty(_iniPath)) return;
        try { UiStore.Modify(_iniPath, ui => ui.QuickNoteTopmost = value); }
        catch (Exception ex) { CrashLog.Write("速记置顶状态保存失败", ex); }
    }

    private static string ComputeDefaultDir(string? iniPath)
    {
        string baseDir = !string.IsNullOrEmpty(iniPath) ? Path.GetDirectoryName(iniPath)! : AppContext.BaseDirectory;
        return Path.Combine(baseDir, "速记") + Path.DirectorySeparatorChar;
    }
}
