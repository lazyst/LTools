using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.Builtin;

/// <summary>
/// 内置功能目录：**常用功能**（作用于系统 / 编辑） + **LTools 功能**（作用于 LTools 自身）。
/// </summary>
/// <remarks>
/// <para>
/// 这些不是步骤单元，也不进动作编辑器左栏：它们只能被放到面板 / 绑到快捷键 / 悬浮。
/// 动作里想用同样的效果，请用对应的步骤单元（截图 / 等待 / 模拟按键…）自己拼。
/// </para>
/// <para>
/// 常用功能里除了系统类入口，还有一组「光标移动 / 文本选择 / 删除与撤销」的预设按键
/// （<c>common.key.*</c> / <c>common.select.*</c> / <c>common.edit.*</c>）：它们就是一次
/// 模拟按键；因为是功能，可以直接放进面板 / 悬浮 / 换键。
/// </para>
/// <para>
/// 新增一条只需在 <see cref="Defs"/> 里加一行；参数声明写在 <see cref="FeatureParam"/> 上，
/// 参数表单、引用编码与摘要都按声明自动生成。
/// </para>
/// </remarks>
internal static class FeatureCatalog
{
    private static readonly FeatureDef[] Defs =
    {
        // ============ LTools 功能 ============
        L("ltools.panel", "打开超级面板", "window",
            "打开 / 收起 LTools 的超级面板（动作面板与暂存区）",
            () => Ui(() => SuperPanel.Toggle())),
        L("ltools.stash", "打开暂存区", "note",
            "打开超级面板并切到暂存区（AI 草稿所在页）",
            () => Ui(() => SuperPanel.ShowStash())),
        L("ltools.note", "打开速记", "note",
            "打开速记窗口，随手记一段文字",
            () => Ui(() => QuickNote.Toggle())),
        L("ltools.settings", "打开设置", "settings",
            "打开 LTools 设置窗口",
            () => Ui(() => ConfigHelper.Toggle())),
        L("ltools.record", "录制键鼠宏", "record",
            "开始 / 结束录制键鼠宏；录完自动生成一个「键鼠宏」动作",
            () => Ui(() => MacroRecorder.Toggle())),
        L("ltools.stop", "停止当前动作", "stop",
            "中止正在运行的自动化动作（与停止热键等效）",
            () => AutomationRunner.Stop()),
        L("ltools.toggle", "启用 / 停用 LTools", "power",
            "切换 CapsLock 增强总开关；停用后 CapsLock 恢复原生大小写",
            () => Ui(() => Settings.ToggleCapsLock())),
        L("ltools.logs", "打开日志目录", "folder",
            "在资源管理器里打开 logs 目录，排查动作失败时用",
            () => OpenFolder(AppPaths.LogsDir)),
        L("ltools.exit", "退出 LTools", "power",
            "退出 LTools（等同托盘菜单的「退出」）",
            () => Ui(App.RequestExit)),

        // ============ 常用功能 · 工具 ============
        C("common.magnifier", "工具", "放大镜", "magnifier",
            "切换屏幕放大镜（Ctrl+Alt+滚轮可缩放）",
            () => Magnifier.Toggle()),
        C("common.quickSearch", "工具", "快速搜索", "search",
            "搜索当前选中的文本：网址直接打开、路径直接打开、其余走 Bing",
            () => QuickSearch.Start()),
        C("common.windowPin", "工具", "窗口置顶", "pin",
            "把鼠标位置所在的窗口切换置顶",
            ctx => WindowPin.ToggleAtCursor(Cursor(ctx).X, Cursor(ctx).Y)),

        // ============ 常用功能 · 光标移动 ============
        C("common.key.left", "光标移动", "左移一字符", "keyboard",
            "光标左移一个字符（←）", () => Keys("left")),
        C("common.key.down", "光标移动", "下移一行", "keyboard",
            "光标下移一行（↓）", () => Keys("down")),
        C("common.key.up", "光标移动", "上移一行", "keyboard",
            "光标上移一行（↑）", () => Keys("up")),
        C("common.key.right", "光标移动", "右移一字符", "keyboard",
            "光标右移一个字符（→）", () => Keys("right")),
        C("common.key.home", "光标移动", "移到行首", "keyboard",
            "移到当前行行首（Home）", () => Keys("home")),
        C("common.key.end", "光标移动", "移到行尾", "keyboard",
            "移到当前行行尾（End）", () => Keys("end")),

        // ============ 常用功能 · 文本选择 ============
        C("common.select.left", "文本选择", "选中左一字符", "keyboard",
            "向左选中一个字符（Shift+←）", () => Keys("shift+left")),
        C("common.select.down", "文本选择", "选中下一行", "keyboard",
            "向下选中一行（Shift+↓）", () => Keys("shift+down")),
        C("common.select.up", "文本选择", "选中上一行", "keyboard",
            "向上选中一行（Shift+↑）", () => Keys("shift+up")),
        C("common.select.right", "文本选择", "选中右一字符", "keyboard",
            "向右选中一个字符（Shift+→）", () => Keys("shift+right")),

        // ============ 常用功能 · 删除与撤销 ============
        C("common.edit.backspace", "删除与撤销", "退格", "keyboard",
            "删除光标前一个字符（Backspace）", () => Keys("backspace")),
        C("common.edit.delete", "删除与撤销", "删除", "keyboard",
            "删除光标后一个字符（Delete）", () => Keys("delete")),
        C("common.edit.deleteLine", "删除与撤销", "删除整行", "keyboard",
            "从行首选中到行尾再删除（Home → Shift+End → Delete）",
            () => Keys("home", "shift+end", "delete")),
        C("common.edit.undo", "删除与撤销", "撤销", "keyboard",
            "撤销上一次编辑（Ctrl+Z）", () => Keys("ctrl+z")),
        C("common.edit.redo", "删除与撤销", "重做", "keyboard",
            "重做被撤销的编辑（Ctrl+Y）", () => Keys("ctrl+y")),

        // ============ 常用功能 · 剪贴板 ============
        C("common.clipboard.history", "剪贴板", "剪贴板历史", "paste",
            "打开系统剪贴板历史（Win+V）",
            () => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkLwin, 0x56)),
        C("common.clipboard.clear", "剪贴板", "清空剪贴板", "delete",
            "清空当前剪贴板内容",
            () =>
            {
                NativeClipboard.Clear();
                ToastService.Show("剪贴板已清空", null, 1600, ToastKind.success);
            }),

        // ============ 常用功能 · 系统 ============
        C("common.system.desktop", "系统", "显示桌面", "window",
            "显示桌面（Win+D）",
            () => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkLwin, 0x44)),
        C("common.system.lock", "系统", "锁定电脑", "power",
            "锁定当前 Windows 会话（Win+L）",
            () => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkLwin, 0x4C)),
        C("common.system.taskmgr", "系统", "任务管理器", "app",
            "打开任务管理器（Ctrl+Shift+Esc）",
            () => InputHelper.ComboReleasingHeldModifiers(
                (ushort)Win32.VkControl, (ushort)Win32.VkShift, (ushort)Win32.VkEscape)),
        C("common.system.explorer", "系统", "打开资源管理器", "folder",
            "打开资源管理器（Win+E）",
            () => InputHelper.ComboReleasingHeldModifiers((ushort)Win32.VkLwin, 0x45)),

        // ============ 常用功能 · 媒体 ============
        C("common.volume.up", "媒体", "音量 +", "music",
            "调高系统音量（每格约 2%）",
            ctx => VolumeSteps(ctx, up: true),
            Number("steps", "格数", "3", "每格约 2%，默认 3 格（约 6%）", "格")),
        C("common.volume.down", "媒体", "音量 −", "music",
            "调低系统音量（每格约 2%）",
            ctx => VolumeSteps(ctx, up: false),
            Number("steps", "格数", "3", "每格约 2%，默认 3 格（约 6%）", "格")),
        C("common.volume.mute", "媒体", "静音", "music",
            "切换系统静音",
            () => InputHelper.Tap((ushort)Win32.VkVolumeMute)),

        // ============ 常用功能 · 屏幕 ============
        C("common.capture", "屏幕", "截图", "camera",
            "截一张图并复制到剪贴板（范围可选）",
            Capture,
            new FeatureParam("source", "范围", FeatureParamKind.Choice, "pick",
                Options: new[]
                {
                    new FeatureOption("pick", "框选区域"),
                    new FeatureOption("screen", "整个屏幕"),
                    new FeatureOption("window", "前台窗口"),
                    new FeatureOption("region", "固定区域"),
                },
                Hint: "框选时按 Esc 取消"),
            new FeatureParam("region", "区域", FeatureParamKind.Region, "",
                Hint: "x,y,w,h；也可以点「框选…」，只在「固定区域」时生效",
                VisibleWhenKey: "source", VisibleWhenValue: "region")),
    };

    private static readonly Dictionary<string, FeatureDef> ById =
        Defs.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static IReadOnlyList<FeatureDef> All => Defs;

    /// <summary>按 Id 取功能定义；没有这个功能返回 null。</summary>
    public static FeatureDef? Find(string? id) =>
        !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out var d) ? d : null;

    /// <summary>是不是一个功能引用（动作 Id 永远不带 <c>#</c>、也不在目录里）。</summary>
    public static bool IsFeatureRef(string? reference) =>
        Find(FeatureRef.IdOf(reference)) != null;

    public static string GroupName(FeatureGroup group) =>
        group == FeatureGroup.Ltools ? "LTools 功能" : "常用功能";

    public static IEnumerable<FeatureDef> InGroup(FeatureGroup group) =>
        Defs.Where(d => d.Group == group);

    /// <summary>显示名（参数不显示在名字里）；未知引用返回占位文本。</summary>
    public static string Name(string reference)
    {
        var def = Find(FeatureRef.IdOf(reference));
        return def != null ? def.Name : "（未知功能）" + FeatureRef.IdOf(reference);
    }

    /// <summary>参数摘要，例如「3 格」「框选区域」；没有参数返回空串。</summary>
    public static string ParamSummary(string reference)
    {
        var def = Find(FeatureRef.IdOf(reference));
        if (def == null || !def.HasParams) return "";

        var values = FeatureRef.Parse(reference);
        var parts = new List<string>();
        foreach (var p in def.Params)
        {
            if (!p.IsVisible(values)) continue;
            string value = values.TryGetValue(p.Key, out var v) ? v : p.Default;
            if (p.Kind == FeatureParamKind.Choice)
            {
                value = p.Options?.FirstOrDefault(o => o.Value == value)?.Label ?? value;
            }
            if (value.Length > 0)
            {
                parts.Add(p.Kind == FeatureParamKind.Number
                    ? value + " " + (p.Unit is { Length: > 0 } unit ? unit : p.Label)
                    : value);
            }
        }
        return string.Join(" · ", parts);
    }

    // ============ 构造辅助 ============

    private static FeatureDef L(string id, string name, string icon, string description, Action run,
        params FeatureParam[] ps) => new()
        {
            Id = id,
            Group = FeatureGroup.Ltools,
            Name = name,
            Icon = icon,
            Description = description,
            Params = ps,
            Run = _ => run(),
        };

    private static FeatureDef C(string id, string category, string name, string icon, string description,
        Action run, params FeatureParam[] ps) => C(id, category, name, icon, description, _ => run(), ps);

    private static FeatureDef C(string id, string category, string name, string icon, string description,
        Action<FeatureRunContext> run, params FeatureParam[] ps) => new()
        {
            Id = id,
            Group = FeatureGroup.Common,
            Category = category,
            Name = name,
            Icon = icon,
            Description = description,
            Params = ps,
            Run = run,
        };

    private static FeatureParam Number(string key, string label, string value, string hint, string unit) =>
        new(key, label, FeatureParamKind.Number, value, Hint: hint, Unit: unit);

    // ============ 实现 ============

    /// <summary>需要碰 WPF 的功能统一投递到 UI 线程（已在 UI 线程就直接跑）。</summary>
    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.Invoke(action);
    }

    private static (int X, int Y) Cursor(FeatureRunContext ctx)
    {
        int x = ctx.CursorX, y = ctx.CursorY;
        if (x == 0 && y == 0 && Win32.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
        }
        return (x, y);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("打不开目录：" + ex.Message, ex);
        }
    }

    private static void VolumeSteps(FeatureRunContext ctx, bool up)
    {
        int steps = Math.Clamp(ctx.GetInt("steps", 3), 1, 30);
        for (int i = 0; i < steps; i++)
            InputHelper.Tap((ushort)(up ? Win32.VkVolumeUp : Win32.VkVolumeDown));
    }

    /// <summary>
    /// 依次发送按键（写法与「模拟按键」步骤一致：<c>ctrl+z</c> / <c>shift+left</c>…）。
    /// 常用功能里的光标 / 选择 / 删除就是这一串预设按键（模拟一次按键）。
    /// </summary>
    private static void Keys(params string[] strokes)
    {
        for (int i = 0; i < strokes.Length; i++)
        {
            if (!KeyStroke.TryParse(strokes[i], out var vks, out string error))
                throw new InvalidOperationException($"按键「{strokes[i]}」无法解析：{error}");

            InputHelper.Combo(vks.ToArray());
            if (i + 1 < strokes.Length) Thread.Sleep(15);
        }
    }

    // ============ 截图 ============

    private static void Capture(FeatureRunContext ctx)
    {
        string source = ctx.Get("source");
        BitmapSource? image = source switch
        {
            "screen" => ToSource(ScreenCapture.Capture(
                ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight)),
            "window" => CaptureForeground(),
            "region" => CaptureRegion(ctx.Get("region")),
            _ => PickRegion(),
        };

        if (image == null)
        {
            ToastService.Show("已取消截图", null, 1600, ToastKind.info);
            return;
        }

        NativeClipboard.SetImage(image);
        ToastService.Show("已截图并复制到剪贴板", "截图", 1800, ToastKind.success);
    }

    private static BitmapSource? CaptureForeground()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("没有前台窗口可截图");

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("前台窗口不可见，无法截图");
        return ToSource(ScreenCapture.Capture(x, y, w, h));
    }

    private static BitmapSource? CaptureRegion(string text)
    {
        if (!ScreenshotProbe.TryParseRect(text, out int x, out int y, out int w, out int h))
            throw new InvalidOperationException("固定区域要写成 x,y,w,h");
        if (w <= 0 || h <= 0) throw new InvalidOperationException("固定区域的宽高必须大于 0");
        return ToSource(ScreenCapture.Capture(x, y, w, h));
    }

    private static BitmapSource? PickRegion()
    {
        var app = Application.Current ?? throw new InvalidOperationException("界面未初始化，无法截图");
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            return app.Dispatcher.Invoke(() =>
            {
                bool ok = ScreenPicker.TryPickRegion(null, out _, out var image);
                return ok ? image : null;
            });
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    private static BitmapSource ToSource(System.Drawing.Bitmap bitmap)
    {
        IntPtr hbitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hbitmap);
            bitmap.Dispose();
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
