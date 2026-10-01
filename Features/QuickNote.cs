using System.IO;
using System.Text.RegularExpressions;
using LTools.Config;
using LTools.Core;
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

    /// <summary>启动时初始化（算默认目录 + 建仓库 + 迁移根目录散落旧 .txt）。由 App 调用。</summary>
    public static void Initialize(string? iniPath)
    {
        _iniPath = iniPath;
        string root = ComputeDefaultDir(iniPath);
        try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
        catch (Exception ex) { CrashLog.Write("QuickNoteInit", ex); }

        _repo = new NoteRepository(root);
        _repo.MigrateRootScatteredNotes();
    }

    /// <summary>重新扫描分类（设置保存后调用；当前仓库即时扫描，此方法主要用于通知已打开窗口刷新）。</summary>
    public static void Refresh()
    {
        if (_form != null) _form.ReloadFromRepository();
    }

    /// <summary>CapsLock+N 切换：已打开则关闭，否则打开。</summary>
    public static void Toggle()
    {
        if (_form != null)
        {
            _form.Close();
            _form = null;
            return;
        }
        if (_repo == null) Initialize(_iniPath);
        _form = new QuickNoteWindow(_repo!);
        _form.Closed += (_, _) => _form = null;
        _form.Show();
    }

    private static string ComputeDefaultDir(string? iniPath)
    {
        string baseDir = !string.IsNullOrEmpty(iniPath) ? Path.GetDirectoryName(iniPath)! : AppContext.BaseDirectory;
        return Path.Combine(baseDir, "速记") + Path.DirectorySeparatorChar;
    }
}
