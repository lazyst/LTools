using System.Text;
using CapsLockPro.Native;
using CapsLockPro.Views;

namespace CapsLockPro.Features;

/// <summary>
/// 帮助面板（对应原版 lib/ui/HelpPanel.ahk）。CapsLock+`` ` ``（扫描码 SC029）切换。
/// 无边框置顶工具窗口 + 只读滚动文本展示热键速查表（9 分类）；
/// Esc / 失焦 / 再次按 CapsLock+`` ` `` 关闭。
/// </summary>
internal static class HelpPanel
{
    private static HelpPanelWindow? _window;

    /// <summary>面板是否已打开。</summary>
    public static bool IsOpen => _window != null;

    /// <summary>
    /// 判断屏幕物理坐标点是否落在面板窗口矩形内（供鼠标钩子判定“点击外部”）。
    /// 鼠标钩子的坐标与 <see cref="Win32.GetWindowRect"/> 均为屏幕物理像素，同一坐标空间，无需 DIP 换算。
    /// </summary>
    public static bool PointInWindowRect(int x, int y)
    {
        var w = _window;
        if (w == null) return false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    /// <summary>切换显示/关闭（钩子在 CapsLock+SC029 时调用）。</summary>
    public static void Toggle()
    {
        if (_window != null) { Close(); return; }

        // 钩子回调（WH_KEYBOARD_LL）内直接 Show 时窗口拿不到前台——
        // 输入尚未处理完 / 前台权限受限，面板不是活动窗口，
        // 首次点击外部不触发 Deactivated（需先点面板激活再点外部才关）。
        // 延迟到下一 Dispatcher 周期执行（对应 AHK 热键在输入处理完后才触发的语义），
        // 并显式 Activate 确保成为前台窗口，使首次点击外部即触发 Deactivated 关闭。
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_window != null) { Close(); return; } // 期间可能已打开/关闭
            _window = new HelpPanelWindow(BuildHelpText());
            _window.Closed += (_, _) => _window = null;
            _window.Show();
            _window.Activate();
        }));
    }

    public static void Close()
    {
        if (_window != null)
        {
            try { _window.Close(); } catch { /* 静默 */ }
        }
        _window = null;
    }

    // —— 热键速查表（对应 BuildHelpText）——

    private static readonly (string Name, (string Key, string Desc)[] Items)[] Categories =
    {
        ("基本功能", new[]
        {
            ("CapsLock (单击, <0.3s)", "发送 Esc"),
            ("CapsLock (长按, >=0.3s)", "犹豫操作，无动作"),
            ("CapsLock + Esc", "禁用 / 启用 CapsLock++"),
            ("Ctrl + CapsLock", "手动切换大写锁定状态"),
            ("Ctrl + Alt + I", "显示调试信息"),
        }),
        ("光标移动", new[]
        {
            ("CapsLock + E", "上移一行"),
            ("CapsLock + D", "下移一行"),
            ("CapsLock + S", "左移一个字符"),
            ("CapsLock + F", "右移一个字符"),
            ("CapsLock + A", "左移一个单词"),
            ("CapsLock + G", "右移一个单词"),
            ("CapsLock + W", "移动到行首"),
            ("CapsLock + R", "移动到行尾"),
            ("CapsLock + Alt + A", "移动到文件开头"),
            ("CapsLock + Alt + G", "移动到文件末尾"),
        }),
        ("文本选择", new[]
        {
            ("CapsLock + H", "向左选择一个单词"),
            ("CapsLock + ;", "向右选择一个单词"),
            ("CapsLock + J", "向左选择一个字符"),
            ("CapsLock + L", "向右选择一个字符"),
            ("CapsLock + I", "向上选择一行"),
            ("CapsLock + K", "向下选择一行"),
            ("CapsLock + U", "选择到行首"),
            ("CapsLock + O", "选择到行尾"),
            ("CapsLock + Alt + H", "选择到文件开头"),
            ("CapsLock + Alt + ;", "选择到文件末尾"),
        }),
        ("删除操作", new[]
        {
            ("CapsLock + ,", "向左删除一个字符 (Backspace)"),
            ("CapsLock + .", "向右删除一个字符 (Delete)"),
            ("CapsLock + M", "删除到行首"),
            ("CapsLock + /", "删除到行尾"),
            ("CapsLock + Backspace", "删除整行"),
            ("CapsLock + Alt + M", "删除到文件开头"),
            ("CapsLock + Alt + /", "删除到文件末尾"),
        }),
        ("编辑操作", new[]
        {
            ("CapsLock + Z", "撤销"),
            ("CapsLock + Y", "重做"),
            ("CapsLock + X", "剪切 (独立剪切板)"),
            ("CapsLock + C", "复制 (独立剪切板)"),
            ("CapsLock + V", "粘贴 (独立剪切板)"),
            ("CapsLock + B", "任务视图 (Win+Tab)"),
            ("CapsLock + Enter", "行尾插入换行"),
            ("CapsLock + RShift", "行首上方插入空行"),
            ("CapsLock + [", "输入左花括号 { "),
            ("CapsLock + ]", "输入右花括号 } "),
            ("CapsLock + '", "输入双引号 \" "),
            ("CapsLock + 9 (空组)", "输入左圆括号 ( "),
            ("CapsLock + 0 (空组)", "输入右圆括号 ) "),
        }),
        ("窗口管理", new[]
        {
            ("CapsLock + 右键", "置顶 / 取消置顶窗口"),
            ("CapsLock + 左键", "文件重命名 (资源管理器)"),
            ("屏幕底部滚轮", "鼠标在底部5px → 调音量"),
        }),
        ("鼠标模式 (CapsLock+Space 进入)", new[]
        {
            ("E / D / S / F", "上 / 下 / 左 / 右 移动光标"),
            ("Q / A", "提高 / 降低移动速度"),
            ("W", "鼠标左键点击"),
            ("R", "鼠标右键点击"),
            ("J / K", "向下 / 向上 滚轮"),
            ("H / L", "向左 / 向右 水平滚轮"),
            ("Esc / CapsLock+Space", "退出鼠标模式"),
        }),
        ("快捷菜单", new[]
        {
            ("CapsLock + 1 ~ 0", "打开菜单组 1 ~ 10"),
            ("菜单内 1 ~ 0", "执行对应菜单项"),
            ("菜单内 Esc / 关闭按钮", "关闭菜单"),
            ("菜单外点击", "自动关闭菜单"),
        }),
        ("实用工具", new[]
        {
            ("CapsLock + Q", "搜索选中文本 / 打开URL / 打开路径"),
            ("CapsLock + Tab", "放大镜 开/关"),
            ("CapsLock + N", "打开速记窗口"),
            ("CapsLock + P", "符号跳转 (配对括号等)"),
            ("CapsLock + \\", "设置"),
            ("CapsLock + `", "帮助面板 (本窗口)"),
        }),
    };

    /// <summary>生成速查表文本（CJK 按 2 宽、ASCII 按 1 宽对齐）。</summary>
    private static string BuildHelpText()
    {
        const int keyWidth = 32;
        const string sep = "  ";
        var sb = new StringBuilder();

        foreach (var cat in Categories)
        {
            string title = "  " + cat.Name + "  ";
            int totalPad = 68 - DisplayWidth(title);
            int leftPad = totalPad / 2;
            int rightPad = totalPad - leftPad;
            sb.Append('\n');
            sb.Append(new string('━', leftPad)).Append(title).Append(new string('━', rightPad));
            sb.Append("\n\n");

            foreach (var item in cat.Items)
            {
                string paddedKey = item.Key;
                int need = keyWidth - DisplayWidth(paddedKey);
                while (need > 0) { paddedKey += " "; need--; }
                sb.Append("  ").Append(paddedKey).Append(sep).Append(item.Desc).Append('\n');
            }
        }

        sb.Append('\n').Append(new string('━', 68)).Append('\n');
        sb.Append("  提示: 按 Esc 或点击外部区域关闭  |  再次按 CapsLock + ` 关闭\n");
        return sb.ToString();
    }

    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var ch in s)
            w += (ch > 127) ? 2 : 1;
        return w;
    }
}
