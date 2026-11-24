using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views;
using LTools.Views.CapturePro;

namespace LTools.Features.Builtin;

/// <summary>
/// 内置功能目录：**常用功能**（作用于系统 / 编辑） + **LTools 功能**（作用于 LTools 自身）。
/// </summary>
/// <remarks>
/// <para>
/// 这些不是动作单元，也不进动作编辑器左栏：它们只能被放到面板 / 绑到快捷键 / 悬浮。
/// 动作里想用同样的效果，请用对应的动作单元（截图 / 等待 / 模拟按键…）自己拼。
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
            () => Ui(() => PanelLauncher.Toggle())),
        L("ltools.stash", "打开暂存区", "note",
            "打开超级面板并切到暂存区（AI 草稿所在页）",
            () => Ui(() => SuperPanel.ShowStash())),
        L("ltools.note", "打开速记", "note",
            "打开速记窗口，随手记一段文字",
            () => Ui(() => QuickNote.Toggle())),
        L("ltools.settings", "打开设置", "settings",
            "打开 LTools 设置窗口",
            () => Ui(() => ConfigHelper.Toggle())),
        L("ltools.share", "局域网共享", "window",
            "在本机和同网段设备之间收发文字 / 文件：别的设备用浏览器打开一条固定地址即可",
            () => LTools.Core.Share.ShareHelper.Open()),
        L("ltools.record", "录制键鼠宏", "record",
            "开始 / 结束录制键鼠宏；录完自动生成一个「键鼠宏」动作",
            () => Ui(() => MacroRecorder.Toggle())),
        L("ltools.stop", "停止当前动作", "stop",
            "中止正在运行的自动化动作（与停止热键等效）",
            () => AutomationRunner.Stop()),
        L("ltools.capslock", "切换 CapsLock 修饰键", "power",
            "开启后 CapsLock 作快捷键修饰键、轻按发 Esc；关闭则恢复原生大小写切换",
            () => Ui(() => Settings.ToggleCapsLockModifier())),
        L("ltools.logs", "打开日志目录", "folder",
            "在资源管理器里打开 logs 目录，排查动作失败时用",
            () => OpenFolder(AppPaths.LogsDir)),
        L("ltools.exit", "退出 LTools", "power",
            "退出 LTools（等同托盘菜单的「退出」）",
            () => Ui(App.RequestExit)),

        // ============ 截图 Pro 的采集入口（照 Snow Shot 的快捷动作） ============
        L("ltools.captureDelay", "延时截图", "clock",
            "等设置里的秒数再弹框选（1–10 秒，默认 3）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.None, delay: true)),
        L("ltools.captureCopy", "截图并复制", "camera",
            "拖完选区立刻复制到剪贴板（不摆工具栏、不能标注）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Copy)),
        L("ltools.capturePin", "截图并贴图", "pin",
            "拖完选区立刻贴到屏幕上（不摆工具栏、不能标注）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Pin)),
        L("ltools.captureSave", "截图并另存为", "save",
            "拖完选区立刻弹「另存为」",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Save)),
        L("ltools.captureQuickSave", "截图并快速保存", "save",
            "拖完选区立刻按快速保存规则落盘（不弹框）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.QuickSave)),
        L("ltools.captureRecognize", "截图并识别", "edit",
            "拖完选区立刻做文字识别并打开结果窗",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Recognize)),
        L("ltools.captureRecognizeCopy", "截图并识别复制", "edit",
            "拖完选区立刻识别，算完把全文写进剪贴板（结果窗照开）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.RecognizeCopy)),
        L("ltools.captureTranslate", "截图并翻译", "edit",
            "拖完选区立刻识别并按「翻译」设置里的目标语言翻一遍",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Translate)),
        L("ltools.captureScrolling", "截图并长截图", "window",
            "拖完选区立刻进长截图（自己滚或自动滚，拼成一张长图）",
            () => CaptureWith(CaptureOverlayWindow.PendingAction.Scrolling)),
        L("ltools.captureHistory", "采集历史", "clock",
            "打开采集历史（截图 / 录屏都在里面）",
            () => CaptureWindows.Open(CaptureWindows.History)),
        L("ltools.pinManager", "贴图管理", "pin",
            "打开贴图管理页（浏览 / 批量贴回 / 删除）",
            () => CaptureWindows.Open(CaptureWindows.PinManager)),
        L("ltools.pinRestoreLast", "恢复上一张关闭的贴图", "pin",
            "把最近关掉的那张贴回原来的位置",
            () => CaptureWindows.Open(CaptureWindows.PinRestoreLast)),
        L("ltools.pinToggleAll", "隐藏 / 显示所有贴图", "pin",
            "一键把屏幕上的贴图全收起来，再按一次放回来",
            () => CaptureWindows.Open(CaptureWindows.PinToggleAll)),
        L("ltools.hotkeysToggle", "暂停 / 恢复全局快捷键", "keyboard",
            "紧急开关：暂停所有全局快捷键（这条本身例外，随时能恢复）",
            HotkeysToggle),
        L("ltools.captureScreen", "截取当前显示器", "camera",
            "不弹覆盖层：直接抓光标所在的显示器，复制并记进采集历史",
            CaptureMonitor),
        L("ltools.captureWindow", "截取前台窗口", "window",
            "不弹覆盖层：直接抓前台窗口，复制并记进采集历史",
            CaptureFrontWindow),
        L("ltools.globalCanvas", "全屏画布", "edit",
            "在光标所在显示器上开一块透明画布，随手标注 / 激光笔指引；开着时再按一次切「点击穿透」（画布不挡下面的程序）",
            () => CaptureWindows.Open(CaptureWindows.GlobalCanvas)),

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
        C("common.screenText", "屏幕", "屏幕选字", "edit",
            "拖选屏幕上的文字并识别，点候选复制",
            ScreenText),
        C("common.pinClipboard", "屏幕", "贴剪贴板", "paste",
            "把剪贴板里的图片、文件或文字贴到屏幕上（颜色文本贴成色卡）",
            _ => Ui(PinClipboard.Pin)),
        C("common.pinSelectedFiles", "屏幕", "贴选中的文件", "pin",
            "把资源管理器里选中的图片 / 文本文件贴到屏幕上",
            PinSelectedFiles),
        C("common.record", "屏幕", "录屏", "record",
            "录屏（选区 / 主屏 / 全部屏幕 / 前台窗口），存成 MP4 / GIF / APNG / WebP",
            Record,
            new FeatureParam("source", "范围", FeatureParamKind.Choice, "pick",
                Options: new[]
                {
                    new FeatureOption("pick", "框选区域"),
                    new FeatureOption("primary", "主屏"),
                    new FeatureOption("all", "全部屏幕"),
                    new FeatureOption("window", "前台窗口"),
                },
                Hint: "框选时按 Esc 取消"),
            new FeatureParam("format", "格式", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("mp4", "MP4 视频"),
                    new FeatureOption("gif", "GIF 动图"),
                    new FeatureOption("apng", "APNG 动图"),
                    new FeatureOption("webp", "WebP 动图"),
                },
                Hint: "跟随设置时用「设置 → 通用 → 截图 Pro · 录屏」里的默认格式；GIF 帧率最高 15"),
            new FeatureParam("fps", "帧率", FeatureParamKind.Number, "0",
                Hint: "0 = 跟随设置（5–60）", Unit: "fps"),
            new FeatureParam("countdown", "倒计时", FeatureParamKind.Number, "-1",
                Hint: "-1 = 跟随设置；0 = 立刻开始", Unit: "秒"),
            new FeatureParam("follow", "跟随鼠标", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("off", "不跟随"),
                    new FeatureOption("on", "跟随鼠标（选区决定成品尺寸）"),
                },
                Hint: "跟随录屏时画面跟着光标在显示器里平移，成品尺寸仍是选区大小"),
            new FeatureParam("audio", "声音", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("off", "不录声音"),
                    new FeatureOption("system", "系统声音"),
                    new FeatureOption("mic", "麦克风"),
                    new FeatureOption("both", "系统声音 + 麦克风"),
                },
                Hint: "系统声音走 WASAPI 环回，不需要「立体声混音」设备"),
            new FeatureParam("camera", "摄像头", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("off", "不录摄像头"),
                    new FeatureOption("on", "画中画（默认摄像头）"),
                },
                Hint: "画中画贴在左下角，尺寸 / 形状在「录屏选项」里调"),
            new FeatureParam("effects", "键鼠特效", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("off", "不要特效"),
                    new FeatureOption("on", "光标高亮 + 点击涟漪 + 拖尾 + 按键"),
                },
                Hint: "特效画在成品上，不改变系统鼠标外观"),
            new FeatureParam("codec", "编码", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("auto", "自动（有硬件加速就用）"),
                    new FeatureOption("h264", "H.264"),
                    new FeatureOption("hevc", "HEVC（更小，兼容性稍差）"),
                },
                Hint: "硬件编码器（NVENC / AMF / QSV）会先探测，开不起来自动退回软件"),
            new FeatureParam("trim", "录完裁剪", FeatureParamKind.Choice, "",
                Options: new[]
                {
                    new FeatureOption("", "跟随设置"),
                    new FeatureOption("off", "直接保存"),
                    new FeatureOption("on", "打开裁剪 / 倍速窗口"),
                })),
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
        bool recording = false;
        bool captured;
        BitmapSource? image = null;

        if (source is "screen" or "window" or "region")
        {
            // 这三种来源不经过覆盖层（没有 TransientMinimize）：提示卡片是置顶窗口，
            // 得自己收起来，否则会落进成品图。
            using (ToastService.SuppressForCapture())
            {
                image = source switch
                {
                    "screen" => ToSource(ScreenCapture.Capture(
                        ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                        ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight)),
                    "window" => CaptureForeground(),
                    _ => CaptureRegion(ctx.Get("region")),
                };
            }
            captured = image != null;
        }
        else
        {
            // 交互框选：覆盖层在宿主里，「复制」也在那边做完了（这里只拿「成没成」）。
            captured = PickRegion(out recording);
        }

        // 用户在覆盖层里点了「录屏」：这不是取消，录屏已经在后台开跑，别再弹「已取消截图」。
        if (recording) return;

        if (!captured)
        {
            ToastService.Show("已取消截图", null, 1600, ToastKind.info);
            return;
        }

        if (image != null) NativeClipboard.SetImage(image);
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

    /// <summary>
    /// 交互框选。返回 true = 拿到了成品（复制 / 贴图 / 另存为 / 识别都已经在宿主里做完）；
    /// 返回 false 有两种完全不同的意思，由 <paramref name="recording"/> 区分：
    /// 真的取消（要给一句「已取消截图」），还是已经交给录屏（这时什么都不用说）。
    /// </summary>
    /// <remarks>
    /// <paramref name="pending"/> 是快捷入口的「选完就做」（照 Snow Shot 的
    /// <c>PendingSelectionAction</c>）：覆盖层在选区落定后直接执行，工具条根本不摆出来。
    /// 动作失败（另存为取消 / 识别没开起来）会留在覆盖层里继续编辑，不会卡在半截。
    /// </remarks>
    private static bool PickRegion(out bool recording,
        CaptureOverlayWindow.PendingAction pending = CaptureOverlayWindow.PendingAction.None)
    {
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        bool handed = false;

        try
        {
            // 窗口在宿主里建（这个调用会一直等到用户框完）；不再需要自己 Dispatcher.Invoke。
            CaptureOutcome? outcome = CaptureSession.TryCapture(null, out var exit, pending,
                onRecordingFinished: () => RecordingIndicatorWindow.RestoreAutomation(indicator));

            handed = exit == CaptureSession.CaptureExit.Recording;
            recording = handed;
            return outcome != null;
        }
        finally
        {
            if (!handed) RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    /// <summary>
    /// 快捷入口：框选后立刻执行某个动作（复制 / 贴图 / 另存为 / 快速保存 / 识别）。
    /// </summary>
    /// <remarks>
    /// 照 Snow Shot 的 <c>beginCapture(action)</c>：「截图并贴图」这类入口拖完就直接完成，
    /// 中间没有「点工具条」这一步。取消时照上游静默（只有普通截图才说「已取消截图」）。
    /// 「延时截图」先按设置里的秒数等，等完再弹覆盖层——等待期间不摆任何窗口。
    /// </remarks>
    private static void CaptureWith(CaptureOverlayWindow.PendingAction pending, bool delay = false)
    {
        if (delay)
        {
            // 取消只可能来自退出程序：静默收摊，别再去弹覆盖层
            if (!CaptureDelay.Wait(AppState.ScreenshotDelaySeconds)) return;
        }

        bool captured = PickRegion(out bool recording, pending);
        if (recording) return;                       // 已交给录屏，不是取消

        if (pending == CaptureOverlayWindow.PendingAction.None)
        {
            if (!captured) ToastService.Show("已取消截图", null, 1600, ToastKind.info);
            return;
        }

        // 上游这几个入口成功后都不弹 toast（贴图 / 另存为 / 快速保存 / 识别自己就是反馈），
        // 只有「复制」在 LTools 里多一句提示，免得用户不知道到底复制了没有。
        if (pending == CaptureOverlayWindow.PendingAction.Copy && captured)
            ToastService.Show("已复制到剪贴板", "截图", 1600, ToastKind.success);
    }

    /// <summary>「截取当前显示器」：光标所在那块屏，不弹覆盖层（照上游 <c>captureCurrentMonitor</c>）。</summary>
    private static void CaptureMonitor()
    {
        if (!Win32.GetCursorPos(out var cursor))
            throw new InvalidOperationException("取不到光标位置，无法判断是哪块显示器");

        IntPtr monitor = Win32.MonitorFromPoint(cursor, Win32.MonitorDefaultToNearest);
        var info = new Win32.MonitorInfo { cbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("取不到显示器范围");

        var rc = info.rcMonitor;
        CaptureSession.CaptureDirect(
            new System.Drawing.Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top),
            "当前显示器", ExportSettings.ScenarioCurrentMonitor);
    }

    /// <summary>「截取前台窗口」：不弹覆盖层，抓完直接复制（照上游 <c>captureFocusedWindow</c>）。</summary>
    private static void CaptureFrontWindow()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("没有前台窗口可截图");

        IntPtr root = Win32.GetAncestor(hwnd, Win32.GaRoot);
        if (root != IntPtr.Zero) hwnd = root;

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("前台窗口不可见，无法截图");
        CaptureSession.CaptureDirect(new System.Drawing.Rectangle(x, y, w, h), "前台窗口",
            ExportSettings.ScenarioFocusedWindow);
    }

    /// <summary>「暂停 / 恢复全局快捷键」（照上游 toggle global hotkeys）。</summary>
    private static void HotkeysToggle()
    {
        bool on = !HotkeyRegistry.GlobalEnabled;
        HotkeyRegistry.GlobalEnabled = on;
        ToastService.Show(
            on ? "全局快捷键已恢复" : "全局快捷键已暂停（再按一次恢复）",
            "快捷键", 2200, on ? ToastKind.success : ToastKind.warning, key: "hotkeys-enabled");
    }

    private static void ScreenText(FeatureRunContext ctx)    {
        // 自动化指示器是置顶窗口，会被截进冻结画面：先收起，结束再恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            ScreenTextSession.PickOnUi(null);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    // ============ 录屏 ============

    /// <summary>
    /// 「贴选中的文件」：资源管理器 / 桌面里选中的图片与文本文件，按顺序贴到屏幕上。
    /// </summary>
    /// <remarks>
    /// 读选中项要跨进程 COM（<see cref="Selection.ShellBridge.SelectedAny"/> 自己会起 STA 线程），
    /// 所以整段留在 <see cref="FeatureRunner"/> 给的后台线程上，只有贴图投递回 UI 线程。
    /// 读不到只是提示，不弹窗（与「选中的文件」取值器同口径）。
    /// </remarks>
    private static void PinSelectedFiles()
    {
        var selection = Selection.ShellBridge.SelectedAny();
        if (selection == null || selection.Paths.Count == 0)
        {
            Ui(() => ToastService.Show("先在资源管理器里选中要贴的图片或文本文件", "贴图", 3200,
                ToastKind.warning, key: "pin-files"));
            return;
        }

        var paths = selection.Paths.ToArray();
        Ui(() => PinFiles.PinPaths(paths));
    }

    private static void Record(FeatureRunContext ctx)
    {
        // 设置里的默认开关 + 这次参数里点名的覆盖项（没点的项继续跟随设置）
        var options = RecordOptions.Apply(AppState.Record,
            ctx.Get("audio"), ctx.Get("camera"), ctx.Get("effects"),
            ctx.Get("codec"), ctx.Get("trim"));

        string? follow = ctx.Get("follow");
        bool? followMouse = string.IsNullOrWhiteSpace(follow)
            ? null
            : follow.Trim().ToLowerInvariant() is "on" or "true" or "1";

        var plan = RecordPlan.FromValues(
            ctx.Get("source"),
            ctx.Get("format"),
            ctx.GetInt("fps", 0),
            ctx.GetInt("countdown", -1),
            followMouse,
            options);

        RecordSession.TryRun(null, plan);
    }

    private static BitmapSource ToSource(System.Drawing.Bitmap bitmap)
    {
        using (bitmap) return ScreenCapture.ToSource(bitmap);
    }
}
