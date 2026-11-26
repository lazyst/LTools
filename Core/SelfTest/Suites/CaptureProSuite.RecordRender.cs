using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>CaptureProSuite 的「录屏：渲染 / 快捷键 / 自动隐藏 / 摄像头」部分。</summary>
internal static partial class CaptureProSuite
{
    // ============ 录屏：渲染进度对话框 ============

    /// <summary>
    /// 渲染进度对话框（照上游 <c>RecordingRenderDialog</c>）：三阶段文案 / 失败与取消的文案 /
    /// 按钮显隐 / 详情写法 / 进度只增不减 / ffmpeg <c>-progress</c> 行解析。
    /// </summary>
    private static void RecordRenderCases()
    {
        Check.Equal(500, RecordRender.ModalWidth, "对话框宽 500（上游 setPreferredWidth）");
        Check.Equal("正在渲染录屏", RecordRender.Title, "标题");

        // ---- 阶段文案 ----
        var state = new RecordRenderState();
        Check.Equal("正在准备导出录屏…", RecordRender.StatusText(state), "准备阶段");
        state.Stage = RecordRenderStage.Render;
        Check.Equal("正在渲染视频…", RecordRender.StatusText(state), "渲染阶段");
        state.Stage = RecordRenderStage.Finalize;
        Check.Equal("正在完成录屏…", RecordRender.StatusText(state), "收尾阶段");
        state.CancelRequested = true;
        Check.Equal("正在取消渲染…", RecordRender.StatusText(state), "按了取消之后");
        state.Retained = true;
        Check.Equal("已取消渲染", RecordRender.StatusText(state), "取消后停下来（没有错误）");
        state.Error = "ffmpeg 炸了";
        Check.Equal("渲染失败", RecordRender.StatusText(state), "失败后停下来");

        // ---- 按钮显隐 ----
        var running = new RecordRenderState { Stage = RecordRenderStage.Render };
        Check.True(RecordRender.ShowCancel(running), "渲染中显示取消");
        Check.False(RecordRender.ShowRetained(running), "渲染中不显示另外三个");
        Check.True(RecordRender.CancelEnabled(running), "渲染中取消可点");
        Check.False(RecordRender.ShowDetails(running), "渲染中不显示详情");

        var retained = new RecordRenderState { Retained = true, Error = "炸了", Path = @"C:\tmp\rec" };
        Check.False(RecordRender.ShowCancel(retained), "停下来之后没有取消按钮");
        Check.True(RecordRender.ShowRetained(retained), "停下来之后显示重试 / 保留 / 丢弃");
        Check.True(RecordRender.ShowDetails(retained), "停下来之后显示详情");
        Check.True(RecordRender.RetainedEnabled(retained), "停下来之后按钮可点");
        Check.False(RecordRender.RetainedEnabled(new RecordRenderState { Retained = true, Busy = true }),
            "清理中三个按钮禁用");

        var cancelPending = new RecordRenderState { CancelRequested = true };
        Check.False(RecordRender.CancelEnabled(cancelPending), "按过取消之后按钮禁用（不重复发）");
        Check.False(RecordRender.CancelEnabled(new RecordRenderState { TerminalSucceeded = true }),
            "成功了就不用再取消");

        // ---- 详情写法 ----
        Check.Equal("源文件保留在：\nC:\\tmp\\rec",
            RecordRender.DetailsText(new RecordRenderState { Retained = true, Path = "C:\\tmp\\rec" }),
            "只有目录时就是一行目录");
        Check.Equal("炸了\n\n源文件保留在：\nC:\\tmp\\rec",
            RecordRender.DetailsText(new RecordRenderState { Retained = true, Error = "炸了", Path = "C:\\tmp\\rec" }),
            "失败时先写原因再写目录");
        Check.Equal("源文件保留在：\n", RecordRender.DetailsText(new RecordRenderState()),
            "路径空着也不炸");

        // ---- 进度只增不减 / 夹取 ----
        Check.Equal(40d, RecordRender.Advance(40, 30), "进度不倒退");
        Check.Equal(60d, RecordRender.Advance(40, 60), "往前就更新");
        Check.Equal(100d, RecordRender.Advance(40, 160), "超出上限压到 100");
        Check.Equal(0d, RecordRender.Advance(-5, -20), "负数压到 0");
        Check.Equal(40d, RecordRender.Advance(40, double.NaN), "NaN 不动");

        // ---- ffmpeg -progress 行 ----
        Check.True(RecordRender.TryReadProgress("out_time_us=1500000", out long micros)
            && micros == 1_500_000, "读 out_time_us");
        Check.True(RecordRender.TryReadProgress("out_time_ms=2500000", out long ms)
            && ms == 2_500_000, "读 out_time_ms（ffmpeg 里也是微秒）");
        Check.True(RecordRender.TryReadProgress("out_time=00:00:02.500000", out long clock)
            && clock == 2_500_000, "读 out_time 时钟");
        Check.False(RecordRender.TryReadProgress("out_time=N/A", out _), "N/A 不认");
        Check.False(RecordRender.TryReadProgress("progress=continue", out _), "progress 行不认");
        Check.False(RecordRender.TryReadProgress("frame=42", out _), "帧行不认");
        Check.False(RecordRender.TryReadProgress(null, out _), "空行不认");

        Check.Equal(50d, RecordRender.PercentOf(500_000, 1000), "一秒的一半 = 50%");
        Check.Equal(100d, RecordRender.PercentOf(3_000_000, 1000), "超出总时长封顶 100");
        Check.Equal(0d, RecordRender.PercentOf(500_000, 0), "总时长未知就是 0");

        // ---- 挂进度参数 ----
        var withProgress = RecordRender.WithProgress(new[] { "-y", "-i", "in.mp4", "out.mp4" });
        Check.Equal("-progress", withProgress[0], "progress 放最前面（全局选项）");
        Check.Equal("pipe:1", withProgress[1], "进度从 stdout 出来");
        Check.Equal("-nostats", withProgress[2], "顺手关掉 stderr 的统计");
        Check.Equal("out.mp4", withProgress[^1], "原参数原样跟在后面");
    }

    /// <summary>
    /// 录屏面板的快捷键（照上游 <c>screenrecordingshortcutcontroller.cpp</c> +
    /// <c>drawing_shortcuts</c> / <c>canvashistoryshortcuts.h</c>）与录屏区域历史。
    /// </summary>
    private static void RecordShortcutCases()
    {
        // ---- 动作表：默认组合键与上游一致 ----
        Check.Equal("Ctrl+E", RecordShortcuts.ChordsOf(RecordShortcutAction.Export), "导出 = Ctrl+E");
        Check.Equal("Ctrl+S", RecordShortcuts.ChordsOf(RecordShortcutAction.ToggleRecording), "开始 / 暂停 = Ctrl+S");
        Check.Equal("Ctrl+C", RecordShortcuts.ChordsOf(RecordShortcutAction.CopyToClipboard), "复制 = Ctrl+C");
        Check.Equal("Esc", RecordShortcuts.ChordsOf(RecordShortcutAction.EndRecording), "关闭 = Esc");
        Check.Equal("R", RecordShortcuts.ChordsOf(RecordShortcutAction.PreviousArea), "上一块区域 = R");
        Check.Equal("Shift+R", RecordShortcuts.ChordsOf(RecordShortcutAction.NextArea), "下一块区域 = Shift+R");
        Check.Equal("Ctrl+Z", RecordShortcuts.ChordsOf(RecordShortcutAction.Undo), "撤销 = Ctrl+Z");
        Check.Equal("Ctrl+Y", RecordShortcuts.ChordsOf(RecordShortcutAction.Redo),
            "重做 = Ctrl+Y（Ctrl+Shift+Z 在表里是「清空标注」）");
        Check.Equal("Ctrl+Shift+Z", RecordShortcuts.ChordsOf(RecordShortcutAction.ResetCanvas),
            "清空标注 = Ctrl+Shift+Z");
        Check.Equal("V / Q", RecordShortcuts.ChordsOf(RecordShortcutAction.ToolSelect), "选择 = V / Q");
        Check.Equal("3 / P", RecordShortcuts.ChordsOf(RecordShortcutAction.ToolPen), "画笔 = 3 / P");
        Check.Equal("9 / W", RecordShortcuts.ChordsOf(RecordShortcutAction.ToolWatermark), "水印 = 9 / W");

        // 每个绘制工具都有快捷键，且反过来也能查回去
        foreach (var tool in RecordTools.All)
        {
            var action = RecordShortcuts.ActionOf(tool);
            Check.True(RecordShortcuts.ChordsOf(action).Length > 0, $"{tool} 有默认快捷键");
            Check.True(RecordShortcuts.ToolOf(action) == tool, $"{tool} 的动作能查回工具");
        }

        // ---- 只有 Esc 在抬起时才触发（上游 end_recording 的 Release）----
        Check.True(RecordShortcuts.FiresOnRelease(RecordShortcutAction.EndRecording), "Esc 抬起才关面板");
        Check.False(RecordShortcuts.FiresOnRelease(RecordShortcutAction.Export), "导出是按下就触发");
        Check.False(RecordShortcuts.FiresOnRelease(RecordShortcutAction.ToggleRecording), "开始 / 暂停是按下就触发");

        // ---- 匹配：修饰键精确 ----
        const ushort KeyS = 0x53, KeyE = 0x45, KeyC = 0x43, KeyZ = 0x5A, KeyY = 0x59;
        const ushort KeyR = 0x52, Esc = 0x1B, KeyV = 0x56, Key1 = 0x31, KeyP = 0x50;

        var idle = new RecordShortcutState();
        Check.Equal(RecordShortcutAction.ToggleRecording,
            RecordShortcuts.Match(KeyS, true, false, false, false, idle), "空闲 Ctrl+S = 开始录制");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyS, false, false, false, false, idle), "裸 S 不触发");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyS, true, true, false, false, idle), "多按 Shift 就不算命中");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyE, true, false, false, false, idle), "空闲 Ctrl+E 不导出（没在录）");

        var recording = new RecordShortcutState { Recording = true };
        Check.Equal(RecordShortcutAction.Export,
            RecordShortcuts.Match(KeyE, true, false, false, false, recording), "录制中 Ctrl+E = 结束并导出");
        Check.Equal(RecordShortcutAction.ToggleRecording,
            RecordShortcuts.Match(KeyS, true, false, false, false, recording), "录制中 Ctrl+S = 暂停");
        Check.Equal(RecordShortcutAction.CopyToClipboard,
            RecordShortcuts.Match(KeyC, true, false, false, false, recording), "录制中 Ctrl+C = 复制");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyC, true, false, false, false, idle), "空闲且没有历史：Ctrl+C 不触发");
        Check.Equal(RecordShortcutAction.CopyToClipboard,
            RecordShortcuts.Match(KeyC, true, false, false, false, new RecordShortcutState { HasLastRecord = true }),
            "有历史就能复制");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyE, true, false, false, false, new RecordShortcutState { Recording = true, Busy = true }),
            "收尾中不再响应导出");

        // ---- Esc：任何状态下都能关面板 ----
        Check.Equal(RecordShortcutAction.EndRecording,
            RecordShortcuts.Match(Esc, false, false, false, false, idle), "空闲 Esc = 关面板");
        Check.Equal(RecordShortcutAction.EndRecording,
            RecordShortcuts.Match(Esc, false, false, false, false, recording), "录制中 Esc = 关面板");

        // ---- R / Shift+R：区域历史可用时翻历史；不可用时 R 就是「矩形」工具 ----
        var noHistory = new RecordShortcutState { ExportRowVisible = true };
        Check.Equal(RecordShortcutAction.ToolRect,
            RecordShortcuts.Match(KeyR, false, false, false, false, noHistory),
            "历史里没有别的区域：R 落到矩形工具");
        var hasHistory = new RecordShortcutState { ExportRowVisible = true, AreaHistory = true };
        Check.Equal(RecordShortcutAction.PreviousArea,
            RecordShortcuts.Match(KeyR, false, false, false, false, hasHistory), "R = 上一块区域");
        Check.Equal(RecordShortcutAction.NextArea,
            RecordShortcuts.Match(KeyR, false, true, false, false, hasHistory), "Shift+R = 下一块区域");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyR, false, false, false, false,
                new RecordShortcutState
                {
                    ExportRowVisible = true, AreaHistory = true, Recording = true, DrawingAllowed = false,
                }),
            "录制中不翻区域");

        // ---- 绘制工具键 ----
        var drawing = new RecordShortcutState();
        Check.Equal(RecordShortcutAction.ToolSelect,
            RecordShortcuts.Match(KeyV, false, false, false, false, drawing), "V = 选择");
        Check.Equal(RecordShortcutAction.ToolRect,
            RecordShortcuts.Match(Key1, false, false, false, false, drawing), "1 = 矩形");
        Check.Equal(RecordShortcutAction.ToolPen,
            RecordShortcuts.Match(KeyP, false, false, false, false, drawing), "P = 画笔");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyV, false, false, false, false, new RecordShortcutState { DrawingAllowed = false }),
            "不能画时工具键无效");

        // ---- 撤销 / 重做看栈 ----
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyZ, true, false, false, false, drawing), "没有可撤销的就不吞 Ctrl+Z");
        var undoable = new RecordShortcutState { CanUndo = true, CanRedo = true };
        Check.Equal(RecordShortcutAction.Undo,
            RecordShortcuts.Match(KeyZ, true, false, false, false, undoable), "Ctrl+Z = 撤销");
        Check.Equal(RecordShortcutAction.Redo,
            RecordShortcuts.Match(KeyY, true, false, false, false, undoable), "Ctrl+Y = 重做");
        Check.Equal(RecordShortcutAction.ResetCanvas,
            RecordShortcuts.Match(KeyZ, true, true, false, false, undoable), "Ctrl+Shift+Z = 清空标注");
        Check.Equal(RecordShortcutAction.None,
            RecordShortcuts.Match(KeyZ, true, true, false, false,
                new RecordShortcutState { CanUndo = true, CanRedo = true, DrawingAllowed = false }),
            "不能画时清空标注也不响应");

        // ---- 按钮提示带上快捷键 ----
        Check.Equal("复制：最近一条录屏文件（Ctrl+C）",
            RecordShortcuts.WithChord("复制：最近一条录屏文件", RecordShortcutAction.CopyToClipboard),
            "提示文案带组合键");

        // ---- 区域历史：翻页顺序 / 跳过当前 / 跳过屏幕外 ----
        var screens = new List<Int32Rect> { new(0, 0, 1920, 1080) };
        var entries = new List<Int32Rect> { new(0, 0, 400, 300), new(0, 0, 800, 600) };
        var current = new Int32Rect(0, 0, 1000, 700);

        Check.False(RecordAreaHistory.TryVisit(entries, current, false, -1, screens, out _, out _),
            "头一次「下一块」什么都没有");
        Check.True(RecordAreaHistory.TryVisit(entries, current, true, -1, screens, out var newest, out int idx),
            "往回翻拿得到");
        Check.Equal(entries[1], newest, "往回翻先拿最新的那块");
        Check.Equal(1, idx, "游标落在最新那块");
        Check.True(RecordAreaHistory.TryVisit(entries, current, true, 1, screens, out var older, out _),
            "接着往回翻");
        Check.Equal(entries[0], older, "第二块");
        Check.False(RecordAreaHistory.TryVisit(entries, current, true, 0, screens, out _, out _), "到头了");
        Check.True(RecordAreaHistory.TryVisit(entries, current, false, 0, screens, out var forward, out _),
            "往前翻到下一块");
        Check.Equal(entries[1], forward, "下一块是最新那块");
        Check.False(RecordAreaHistory.TryVisit(entries, current, false, 1, screens, out _, out _),
            "已经在最新那块，再往前就没有了");

        Check.True(RecordAreaHistory.TryVisit(entries, entries[1], true, -1, screens, out var skipped, out _),
            "当前这块在列表里也能翻");
        Check.Equal(entries[0], skipped, "跳过当前这块");

        var offscreen = new List<Int32Rect> { new(5000, 5000, 400, 300), new(0, 0, 800, 600) };
        Check.True(RecordAreaHistory.TryVisit(offscreen, current, true, -1, screens, out var visible, out _),
            "屏幕外的旧坐标自动跳过");
        Check.Equal(offscreen[1], visible, "落到还在屏幕上的那一块");
        Check.False(RecordAreaHistory.TryVisit(offscreen.GetRange(0, 1), current, true, -1, screens, out _, out _),
            "全都出了屏幕 = 到底");

        // ---- 有没有「别的区域」（R / Shift+R 能不能点）----
        Check.True(RecordAreaHistory.AnyOther(entries, current, screens), "历史里有别的可用区域");
        Check.False(RecordAreaHistory.AnyOther(new List<Int32Rect> { current }, current, screens),
            "只有当前这块 = 没有别的");
        Check.False(RecordAreaHistory.AnyOther(offscreen.GetRange(0, 1), current, screens),
            "屏幕外的不算可用");

        // ---- 清洗：去重留最后 / 丢空 / 限长 ----
        var cleaned = RecordAreaHistory.Normalize(new List<Int32Rect>
        {
            new(0, 0, 10, 10), new(0, 0, 20, 20), new(0, 0, 10, 10),
        });
        Check.Equal(2, cleaned.Count, "重复区域只留一条");
        Check.Equal(new Int32Rect(0, 0, 20, 20), cleaned[0], "挪到后面去");
        Check.Equal(new Int32Rect(0, 0, 10, 10), cleaned[1], "最后出现的位置决定顺序");

        Check.Equal(0, RecordAreaHistory.Normalize(new List<Int32Rect>
        {
            new(0, 0, 0, 10), new(0, 0, 10, 0), new(0, 0, -5, 10),
        }).Count, "空区域 / 负尺寸不收");

        var many = new List<Int32Rect>();
        for (int i = 0; i < RecordAreaHistory.Limit + 20; i++) many.Add(new Int32Rect(0, 0, i + 1, 10));
        var limited = RecordAreaHistory.Normalize(many);
        Check.Equal(RecordAreaHistory.Limit, limited.Count, "上限 100");
        Check.Equal(new Int32Rect(0, 0, RecordAreaHistory.Limit + 20, 10), limited[^1], "超出时丢最旧的");
    }

    /// <summary>
    /// 录屏工具条「贴边自动收起」的纯几何（照上游
    /// <c>screenrecordingtoolbarautohide.h</c>）：贴边判定 / 小条几何 / 过渡区 / 可收起门槛。
    /// </summary>
    private static void RecordAutoHideCases()
    {
        var bounds = new Int32Rect(0, 0, 1920, 1080);

        // ---- 贴边判定：两边都离得远 = 不贴边 ----
        Check.Equal(RecordToolbarSide.None,
            RecordAutoHide.SideForRow(new Int32Rect(600, 800, 700, 76), bounds), "屏幕中间不收");

        // ---- 贴着左边（间距 0）/ 贴着右边 ----
        Check.Equal(RecordToolbarSide.Left,
            RecordAutoHide.SideForRow(new Int32Rect(0, 800, 700, 76), bounds), "贴左 = 左");
        Check.Equal(RecordToolbarSide.Right,
            RecordAutoHide.SideForRow(new Int32Rect(1220, 800, 700, 76), bounds), "贴右 = 右");

        // ---- 阈值 16：15 算贴边、17 不算 ----
        Check.Equal(RecordToolbarSide.Left,
            RecordAutoHide.SideForRow(new Int32Rect(15, 800, 700, 76), bounds), "左间距 15 还算贴边");
        Check.Equal(RecordToolbarSide.None,
            RecordAutoHide.SideForRow(new Int32Rect(17, 800, 700, 76), bounds), "左间距 17 不算");
        Check.Equal(RecordToolbarSide.Right,
            RecordAutoHide.SideForRow(new Int32Rect(1920 - 700 - 16, 800, 700, 76), bounds),
            "右间距正好 16 算贴边");

        // ---- 两边一样近：沿用上一次的边，没有上一次就取右 ----
        // （两边都超过阈值时先判「不贴边」，所以只有接近满宽的行才会走到这一步）
        var even = new Int32Rect(10, 800, 1900, 76);   // 左 10、右 10
        Check.Equal(RecordToolbarSide.Right, RecordAutoHide.SideForRow(even, bounds), "相等且没有上次 = 右");
        Check.Equal(RecordToolbarSide.Left,
            RecordAutoHide.SideForRow(even, bounds, RecordToolbarSide.Left), "相等时保持上次的左");
        Check.Equal(RecordToolbarSide.Right,
            RecordAutoHide.SideForRow(even, bounds, RecordToolbarSide.Right), "相等时保持上次的右");
        Check.Equal(RecordToolbarSide.None,
            RecordAutoHide.SideForRow(new Int32Rect(610, 800, 700, 76), bounds, RecordToolbarSide.Left),
            "居中时两边都远 → 不贴边（先判阈值再看相等）");

        // ---- 越过边缘（负间距）仍然算那一边，越得多的一边优先 ----
        Check.Equal(RecordToolbarSide.Left,
            RecordAutoHide.SideForRow(new Int32Rect(-40, 800, 700, 76), bounds), "拖出左边缘仍算左");
        Check.Equal(RecordToolbarSide.Right,
            RecordAutoHide.SideForRow(new Int32Rect(1260, 800, 700, 76), bounds), "拖出右边缘仍算右");

        // ---- 不相交 / 空矩形 = 不贴边 ----
        Check.Equal(RecordToolbarSide.None,
            RecordAutoHide.SideForRow(new Int32Rect(0, 1400, 700, 76), bounds), "和屏幕不相交");
        Check.Equal(RecordToolbarSide.None,
            RecordAutoHide.SideForRow(new Int32Rect(0, 800, 0, 76), bounds), "空主行");

        // ---- 小条几何：20 DIP × scale，贴边、纵向居中 ----
        var row = new Int32Rect(0, 500, 700, 76);
        var tab = RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.Left, 1);
        Check.Equal(new Int32Rect(0, 500, 20, 76), tab, "100% 缩放：20 × 76，贴左边、与主行同高");

        var tab125 = RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.Left, 1.25);
        Check.Equal(25, tab125.Width, "125% 缩放：20 DIP = 25 物理像素");
        Check.Equal(76, tab125.Height, "高度跟主行（不超过屏高）");

        var right = RecordAutoHide.TabGeometry(new Int32Rect(1220, 500, 700, 76), bounds,
            RecordToolbarSide.Right, 1);
        Check.Equal(new Int32Rect(1900, 500, 20, 76), right, "贴右边：右对齐到屏幕边");

        // 主行贴在屏幕顶 / 底：小条仍留在屏幕内
        var topRow = new Int32Rect(0, 0, 700, 76);
        Check.Equal(new Int32Rect(0, 0, 20, 76),
            RecordAutoHide.TabGeometry(topRow, bounds, RecordToolbarSide.Left, 1), "贴顶不越界");
        var bottomRow = new Int32Rect(0, 1070, 700, 76);
        var bottomTab = RecordAutoHide.TabGeometry(bottomRow, bounds, RecordToolbarSide.Left, 1);
        Check.Equal(1080 - 76, bottomTab.Y, "贴底收到屏幕里");

        // 比例认得不对时按 1.0 兜底（上游 !isfinite || <= 0）
        Check.Equal(20, RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.Left, 0).Width, "scale 0 → 1");
        Check.Equal(20, RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.Left, -2).Width, "负 scale → 1");
        Check.Equal(20, RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.Left, double.NaN).Width,
            "NaN scale → 1");

        // 不贴边 / 空矩形 / 屏比小条还窄：不返回畸形矩形
        Check.True(RecordAutoHide.IsEmpty(
            RecordAutoHide.TabGeometry(row, bounds, RecordToolbarSide.None, 1)), "没有边就没有小条");
        Check.True(RecordAutoHide.IsEmpty(
            RecordAutoHide.TabGeometry(default, bounds, RecordToolbarSide.Left, 1)), "空主行没有小条");
        Check.Equal(12, RecordAutoHide.TabGeometry(new Int32Rect(0, 10, 30, 20),
            new Int32Rect(0, 0, 12, 1080), RecordToolbarSide.Left, 1).Width, "小条不超过屏宽");

        // ---- 过渡区：小条 → 主行之间那一条竖直带（不是整行） ----
        var transfer = RecordAutoHide.TransferRegion(new Int32Rect(8, 500, 700, 76), tab,
            RecordToolbarSide.Left);
        Check.Equal(new Int32Rect(0, 500, 20, 76), transfer,
            "左侧：小条与主行左边缘之间那条带");

        var rightTransfer = RecordAutoHide.TransferRegion(new Int32Rect(1212, 500, 700, 76), right,
            RecordToolbarSide.Right);
        Check.Equal(new Int32Rect(1900, 500, 20, 76), rightTransfer, "右侧同理（小条那一列）");

        // 主行整块在小条上方：竖直方向取「主行下沿 → 小条下沿」
        var above = RecordAutoHide.TransferRegion(new Int32Rect(8, 400, 700, 76), tab,
            RecordToolbarSide.Left);
        Check.Equal(new Int32Rect(0, 475, 20, 101), above, "主行在小条上方时仍连成一条带");

        Check.True(RecordAutoHide.IsEmpty(
            RecordAutoHide.TransferRegion(row, tab, RecordToolbarSide.None)), "没有边就没有过渡区");
        Check.True(RecordAutoHide.IsEmpty(
            RecordAutoHide.TransferRegion(row, default, RecordToolbarSide.Left)), "没有小条就没有过渡区");

        // ---- 命中 / 相交 ----
        Check.True(RecordAutoHide.Contains(tab, 10, 510), "小条里命中");
        Check.False(RecordAutoHide.Contains(tab, 20, 510), "右边界外不命中");
        Check.True(RecordAutoHide.Intersects(new Int32Rect(0, 0, 10, 10), new Int32Rect(9, 9, 10, 10)),
            "共享一个像素也算相交");
        Check.False(RecordAutoHide.Intersects(new Int32Rect(0, 0, 10, 10), new Int32Rect(10, 0, 10, 10)),
            "相邻不算相交");

        // ---- 可收起门槛：开着 + 真的在录 + 不在收尾 ----
        Check.True(RecordAutoHide.IsEligible(true, true, false), "开着、在录、没收尾");
        Check.False(RecordAutoHide.IsEligible(false, true, false), "设置关着");
        Check.False(RecordAutoHide.IsEligible(true, false, false), "摆着等开始 / 已经停了");
        Check.False(RecordAutoHide.IsEligible(true, true, true), "正在收尾");

        // ---- 设置项：默认关，改过之后能存下来（Normalized / CopyFrom 都不能丢） ----
        var defaults = new RecordOptions();
        Check.False(defaults.AutoHideToolbar, "「贴边自动收起」默认关（上游 booleanDefault(false)）");
        Check.True(defaults.Normalized().AutoHideToolbar == false, "清洗后仍是关");

        var on = new RecordOptions { AutoHideToolbar = true };
        Check.True(on.Normalized().AutoHideToolbar, "清洗后保留开");
        var target = new RecordOptions();
        target.CopyFrom(on);
        Check.True(target.AutoHideToolbar, "CopyFrom 带过去（面板上的这一轮立刻生效）");
    }

    /// <summary>
    /// 录屏空闲期的摄像头画中画预览（照上游 <c>recordingwebcamcontrol.cpp</c> +
    /// <c>snow-recording-model/src/webcam.rs</c>）：方向位语义与四个按钮的换算、拖动夹取、
    /// 缩放 50–500、右上角缩放钮、正上方方向工具条的落点与夹取。
    /// </summary>
    private static void RecordWebcamCases()
    {
        // ---- 方向：默认 identity（上游 webcam_orientation 默认 0），落盘只认 0…7 ----
        var defaults = new RecordOptions();
        Check.Equal(RecordCameraOrientation.identity, defaults.CameraOrientation,
            "画中画默认不旋转、不镜像（上游 integerDefault(0)）");
        Check.Equal(RecordCameraOrientation.identity, defaults.Normalized().CameraOrientation,
            "清洗后仍是 identity");

        Check.Equal(RecordCameraOrientation.clockwise90,
            new RecordOptions { CameraOrientation = RecordCameraOrientation.clockwise90 }.Normalized().CameraOrientation,
            "清洗保留合法值");
        Check.Equal(RecordCameraOrientation.mirroredClockwise270,
            new RecordOptions { CameraOrientation = (RecordCameraOrientation)7 }.Normalized().CameraOrientation,
            "7 = 左右翻 + 逆时针 90°");
        Check.Equal(RecordCameraOrientation.clockwise270,
            new RecordOptions { CameraOrientation = (RecordCameraOrientation)11 }.Normalized().CameraOrientation,
            "越界值按低三位掩回（11 & 7 = 3）");

        var carried = new RecordOptions();
        carried.CopyFrom(new RecordOptions { CameraOrientation = RecordCameraOrientation.mirroredClockwise90 });
        Check.Equal(RecordCameraOrientation.mirroredClockwise90, carried.CameraOrientation,
            "CopyFrom 把方向带过去");

        // ---- 四个按钮（上游 screenrecordingcontroller.cpp 的 webcamOrientationRequested） ----
        Check.Equal(RecordCameraOrientation.clockwise90,
            RecordWebcamLayout.Next(RecordCameraOrientation.identity, RecordWebcamAction.rotateClockwise),
            "原方向 → 顺时针 90°");
        Check.Equal(RecordCameraOrientation.identity,
            RecordWebcamLayout.Next(RecordCameraOrientation.clockwise270, RecordWebcamAction.rotateClockwise),
            "逆时针 90° 再顺时针 → 回到原方向");
        Check.Equal(RecordCameraOrientation.clockwise270,
            RecordWebcamLayout.Next(RecordCameraOrientation.identity, RecordWebcamAction.rotateCounterclockwise),
            "原方向 → 逆时针 90°");
        Check.Equal(RecordCameraOrientation.mirroredClockwise90,
            RecordWebcamLayout.Next(RecordCameraOrientation.mirrored, RecordWebcamAction.rotateClockwise),
            "镜像态旋转只动圈数、镜像位保持");

        Check.Equal(RecordCameraOrientation.mirrored,
            RecordWebcamLayout.Next(RecordCameraOrientation.identity, RecordWebcamAction.flipHorizontal),
            "左右翻：原方向 → 左右镜像");
        Check.Equal(RecordCameraOrientation.mirroredClockwise270,
            RecordWebcamLayout.Next(RecordCameraOrientation.clockwise90, RecordWebcamAction.flipHorizontal),
            "左右翻：顺时针 90° → 镜像 + 逆时针 90°");
        Check.Equal(RecordCameraOrientation.mirroredClockwise180,
            RecordWebcamLayout.Next(RecordCameraOrientation.identity, RecordWebcamAction.flipVertical),
            "上下翻：原方向 = 镜像 + 180°");
        Check.Equal(RecordCameraOrientation.mirroredClockwise90,
            RecordWebcamLayout.Next(RecordCameraOrientation.clockwise90, RecordWebcamAction.flipVertical),
            "上下翻：顺时针 90° → 镜像 + 顺时针 90°");

        Check.Equal(RecordCameraOrientation.identity,
            RecordWebcamLayout.Next(
                RecordWebcamLayout.Next(RecordCameraOrientation.identity, RecordWebcamAction.flipHorizontal),
                RecordWebcamAction.flipHorizontal),
            "左右翻两次回到原方向");

        var spun = RecordCameraOrientation.identity;
        for (int i = 0; i < 4; i++) spun = RecordWebcamLayout.Next(spun, RecordWebcamAction.rotateClockwise);
        Check.Equal(RecordCameraOrientation.identity, spun, "顺时针转四次回到原方向");

        // ---- 文案 / 图标（悬停提示与按钮资源键） ----
        Check.Equal("原方向", RecordWebcamLayout.Describe(RecordCameraOrientation.identity), "方向短说明：原方向");
        Check.Equal("顺时针 90°", RecordWebcamLayout.Describe(RecordCameraOrientation.clockwise90),
            "方向短说明：顺时针 90°");
        Check.Equal("左右翻转 · 180°", RecordWebcamLayout.Describe(RecordCameraOrientation.mirroredClockwise180),
            "方向短说明：镜像 + 180°");

        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (RecordWebcamAction action in Enum.GetValues<RecordWebcamAction>())
        {
            labels.Add(RecordWebcamLayout.ActionLabel(action));
            Check.True(RecordWebcamLayout.ActionIcon(action).StartsWith("Icon", StringComparison.Ordinal),
                $"{action} 有图标键（{RecordWebcamLayout.ActionIcon(action)}）");
        }

        Check.Equal(4, labels.Count, "四个方向按钮的提示各不相同");

        // ---- 落点：区域在屏幕上的偏移要带上 ----
        var area = new Int32Rect(100, 200, 700, 460);
        Check.Equal(new Int32Rect(148, 462, 150, 150),
            RecordWebcamLayout.PreviewRect(area, 150, 48, 48),
            "画中画 = 区域左上 + 边距（贴左 / 贴下）");

        // 边长先按区域短边收成 460，此时「离底边 48」也被夹成 0（460 − 460），所以纵向落回区域顶部
        Check.Equal(new Int32Rect(148, 200, 460, 460),
            RecordWebcamLayout.PreviewRect(area, 600, 48, 48),
            "区域比画中画小时按短边收（宽 700 高 460 → 460），边距跟着夹");

        var fitted = RecordWebcamLayout.FittedOffset(area, 150, 1000, 1000);
        Check.Equal(550, fitted.Left, "边距超界按区域夹（左）");
        Check.Equal(310, fitted.Bottom, "边距超界按区域夹（下）");
        var negative = RecordWebcamLayout.FittedOffset(area, 150, -20, -20);
        Check.Equal(0, negative.Left, "负边距收成 0（左）");
        Check.Equal(0, negative.Bottom, "负边距收成 0（下）");

        // ---- 拖动：从夹过的起点加位移，两边都夹回区域 ----
        var moved = RecordWebcamLayout.OffsetForMove(area, 150, 48, 48, 30, -20);
        Check.Equal(78, moved.Left, "往右拖 30 → 左边距 +30");
        Check.Equal(68, moved.Bottom, "往上拖 20 → 离底边 +20");
        Check.Equal(550, RecordWebcamLayout.OffsetForMove(area, 150, 48, 48, 10000, 0).Left,
            "拖出右边就夹在区域内（700 − 150）");
        Check.Equal(310, RecordWebcamLayout.OffsetForMove(area, 150, 48, 48, 0, -10000).Bottom,
            "拖出上边就夹在区域内（460 − 150）");
        Check.Equal(0, RecordWebcamLayout.OffsetForMove(area, 150, 48, 48, -10000, 10000).Left,
            "拖出左边夹到 0");
        Check.Equal(0, RecordWebcamLayout.OffsetForMove(area, 150, 48, 48, -10000, 10000).Bottom,
            "拖出下边夹到 0");

        // ---- 缩放：size = clamp(初始边长 + (dx − dy) / 2, 50, 500) ----
        Check.Equal(180, RecordWebcamLayout.SizeForResize(150, 60, 0), "右拖 60 → 边长 +30");
        Check.Equal(120, RecordWebcamLayout.SizeForResize(150, 0, 60), "下拖 60 → 边长 −30");
        Check.Equal(RecordOptions.MaxCameraSize, RecordWebcamLayout.SizeForResize(150, 10000, 0),
            "放大夹到 500");
        Check.Equal(RecordOptions.MinCameraSize, RecordWebcamLayout.SizeForResize(150, -10000, 0),
            "缩小夹到 50");
        Check.Equal(152, RecordWebcamLayout.SizeForResize(150, 3, 0), "1.5 进位（AwayFromZero）");
        Check.Equal(150, RecordWebcamLayout.ResizeAnchor(area, 150), "缩放起点 = 区域夹过的边长");

        var tiny = new Int32Rect(0, 0, 100, 100);
        Check.Equal(100, RecordWebcamLayout.ResizeAnchor(tiny, 500), "区域比画中画小时从短边起算");

        // ---- 右上角缩放钮：32 DIP × 比例，夹在画中画里 ----
        var preview = new Int32Rect(100, 100, 150, 150);
        Check.Equal(new Int32Rect(218, 100, 32, 32), RecordWebcamLayout.HandleRect(preview, 1),
            "缩放钮贴右上角（150 − 32）");
        Check.Equal(new Int32Rect(210, 100, 40, 40), RecordWebcamLayout.HandleRect(preview, 1.25),
            "125% 缩放下 40 像素");
        Check.Equal(new Int32Rect(100, 100, 20, 20),
            RecordWebcamLayout.HandleRect(new Int32Rect(100, 100, 20, 20), 1),
            "画中画比钮还小时夹到画中画大小");

        // ---- 方向工具条：4 个 24 DIP 按钮 + 3/2 内边距 ----
        Check.Equal((102, 28), RecordWebcamLayout.ToolbarSize(1), "工具条 102 × 28 DIP");
        Check.Equal((128, 36), RecordWebcamLayout.ToolbarSize(1.25), "125% 缩放：128 × 36 像素");

        var screen = new Int32Rect(0, 0, 1920, 1080);
        var bar = RecordWebcamLayout.ToolbarRect(new Int32Rect(300, 400, 150, 150), screen, 1);
        Check.Equal(new Int32Rect(324, 366, 102, 28), bar, "工具条居中贴在画中画上方 6 DIP");

        var below = RecordWebcamLayout.ToolbarRect(new Int32Rect(300, 0, 150, 150), screen, 1);
        Check.Equal(156, below.Y, "上面放不下就翻到下方（150 + 6）");

        var clamped = RecordWebcamLayout.ToolbarRect(new Int32Rect(1900, 400, 150, 150), screen, 1);
        Check.Equal(1818, clamped.X, "靠右边缘时横向夹回屏幕里（1920 − 102）");
        Check.Equal(102, clamped.Width, "夹回来不改宽度");
    }

    /// <summary>
    /// 录屏设置（P9-g）：照上游 <c>screen_recording/*</c> 的默认值与归一、质量换算、
    /// 清晰度缩放、文件名模板、分离音轨、后期处理进度条。
    /// </summary>
    private static void RecordSettingsRules()
    {
        // ---- 默认值（configurationschema.cpp 的 kSchemaStrings* / kSchemaRange*）----
        var d = new RecordOptions();
        Check.Equal("1080p", d.Clarity, "清晰度默认 1080p");
        Check.Equal(30, d.FrameRate, "帧率默认 30");
        Check.Equal("720p", d.AnimatedClarity, "动图清晰度默认 720p");
        Check.Equal(10, d.AnimatedFrameRate, "动图帧率默认 10");
        Check.True(d.LoopAnimated, "动图默认无限循环");
        Check.Equal("mp4", d.OutputFormat, "默认格式 mp4");
        Check.Equal(500, d.TrailDurationMs, "拖尾默认 500ms");
        Check.Equal(64, d.KeyboardSize, "按键浮层默认 64");
        Check.Equal("#000000CC", d.KeyboardBackgroundColor, "按键底色默认 #000000CC");
        Check.Equal("#FFFFFFFF", d.KeyboardForegroundColor, "按键字色默认白");
        Check.Equal("#FFFF0080", d.MouseHighlightColor, "光标高亮默认玫红");
        Check.Equal("#1677FFFF", d.ProgressBarColor, "进度条默认蓝");
        Check.False(d.FollowMouse, "默认不跟随鼠标");
        Check.True(d.FollowMouseSmooth && d.FollowMouseReduceJitter, "跟随默认平滑 + 抑抖");
        Check.Equal(80, d.VideoQuality, "默认质量 80");
        Check.Equal("veryfast", d.EncodingPreset, "默认速度档 veryfast");
        Check.True(d.CaptureToolbarInRecording, "工具条默认录进画面");
        Check.True(d.ShowStatusPrompts, "默认显示状态提示");
        Check.False(d.AutoExitAfterRecording, "默认不自动收起");
        Check.False(d.NotifyAfterExport, "默认不额外提示导出");
        Check.False(d.DeleteFilesOnClose, "默认关窗不删文件");
        Check.False(d.SeparateAudioTracks, "默认混成一条音轨");
        Check.Equal(0, d.StartDelaySeconds, "默认不等待");
        Check.Equal("", d.SaveDirectory, "默认保存目录留空（系统视频目录）");
        Check.Equal("SnowShot_Video_{YYYY-MM-DD_HH-mm-ss}", d.FilenameFormat, "默认文件名模板");

        // ---- 归一 ----
        Check.Equal("4k", RecordOptions.NormalizeClarity("4K", animated: false), "清晰度不分大小写");
        Check.Equal("1080p", RecordOptions.NormalizeClarity("???", animated: false), "认不出回视频默认");
        Check.Equal("720p", RecordOptions.NormalizeClarity("4k", animated: true), "动图不认 4k → 720p");
        Check.Equal("mp4", RecordOptions.NormalizeFormat("webm"), "认不出的格式回 mp4");
        Check.Equal("webp", RecordOptions.NormalizeFormat("WebP"), "格式不分大小写");
        Check.Equal("veryfast", RecordOptions.NormalizePreset("nope"), "认不出的速度档回 veryfast");
        Check.Equal(RecordOptions.MinFrameRate, RecordOptions.NormalizeFrameRate(1), "帧率夹到下限");
        Check.Equal(RecordOptions.MaxFrameRate, RecordOptions.NormalizeFrameRate(120), "帧率夹到上限");
        Check.Equal(30, RecordOptions.NormalizeFrameRate(0), "帧率 0 = 默认 30");
        Check.Equal(10, RecordOptions.NormalizeAnimatedFrameRate(0), "动图帧率 0 = 默认 10");
        Check.Equal(10, RecordOptions.NormalizeAnimatedFrameRate(-3), "动图帧率负数也回默认");
        Check.Equal(30, RecordOptions.NormalizeAnimatedFrameRate(99), "动图帧率夹到上限");
        Check.Equal(300, RecordOptions.NormalizeTrailDuration(260), "拖尾按 100ms 取整");
        Check.Equal(100, RecordOptions.NormalizeTrailDuration(10), "拖尾夹到下限");
        Check.Equal(2000, RecordOptions.NormalizeTrailDuration(5000), "拖尾夹到上限");
        Check.Equal(64, RecordOptions.NormalizeKeyboardSize(0), "按键高度 0 = 默认 64");
        Check.Equal(40, RecordOptions.NormalizeKeyboardSize(38), "按键高度按 8px 取整");
        Check.Equal(128, RecordOptions.NormalizeKeyboardSize(200), "按键高度夹到上限");
        Check.Equal(0, RecordOptions.NormalizeQuality(-5), "质量夹到 0");
        Check.Equal(100, RecordOptions.NormalizeQuality(500), "质量夹到 100");
        Check.Equal(10, RecordOptions.NormalizeStartDelay(99), "等待秒数夹到 10");
        Check.Equal(2160, RecordOptions.ClarityHeight("4k"), "4k = 2160");
        Check.Equal(1440, RecordOptions.ClarityHeight("2k"), "2k = 1440");
        Check.Equal(1080, RecordOptions.ClarityHeight("???"), "认不出按 1080");
        Check.Equal(480, RecordOptions.AnimatedClarityHeight("480p"), "动图 480p");
        Check.Equal(720, RecordOptions.AnimatedClarityHeight("4k"), "动图不认 4k → 720");
        Check.Equal(16, RecordOptions.CrfOf(100), "质量 100 → crf 16");
        Check.Equal(23, RecordOptions.CrfOf(80), "质量 80 → crf 23");
        Check.Equal(51, RecordOptions.CrfOf(0), "质量 0 → crf 51");
        Check.Equal("scale=-2:'min(ih,720)'", RecordOptions.ScaleFilter(720), "缩放滤镜只往下缩");

        // ---- 颜色（#AARRGGBB）----
        Check.Equal("#FF112233", RecordOptions.NormalizeColor("112233", "#FF000000"), "6 位补 FF");
        Check.Equal("#80112233", RecordOptions.NormalizeColor("#80112233", "#FF000000"), "8 位原样");
        Check.Equal("#FF000000", RecordOptions.NormalizeColor("zzz", "#FF000000"), "认不出用兜底");
        Check.Equal(0x80, RecordOptions.AlphaOf("#80112233"), "alpha 取前两位");
        Check.Equal(0, RecordOptions.AlphaOf("???"), "认不出按透明");

        // ---- 文件名模板 ----
        var when = new DateTime(2026, 3, 4, 5, 6, 7);
        Check.Equal("SnowShot_Video_2026-03-04_05-06-07",
            RecordOptions.FileNameFor("SnowShot_Video_{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", when), "模板逐项展开");
        Check.Equal("rec-26", RecordOptions.FileNameFor("rec-{YY}", when), "{YY} 两位年份");
        Check.Equal("2026-03-04_05-06-07", RecordOptions.FileNameFor("{YYYY-MM-DD_HH-mm-ss}", when),
            "一对花括号里可以是一整段日期格式（上游写法）");
        Check.Equal("SnowShot_Video_2026-03-04_05-06-07", RecordOptions.FileNameFor("   ", when), "空模板回默认");
        Check.Equal("a_b", RecordOptions.FileNameFor("a:b", when), "非法字符换下划线");

        // ---- CopySettingsFrom 只搬全局字段 ----
        var global = new RecordOptions
        {
            Clarity = "720p",
            FrameRate = 24,
            VideoQuality = 60,
            EncodingPreset = "medium",
            TrailDurationMs = 800,
            KeyboardSize = 96,
            FollowMouse = true,
            SaveDirectory = @"D:\rec",
            FilenameFormat = "x_{YYYY}",
        };
        var local = new RecordOptions
        {
            SystemAudio = false,
            Microphone = true,
            CameraDevice = 2,
            Codec = RecordCodec.hevc,
        };
        local.CopySettingsFrom(global);
        Check.Equal("720p", local.Clarity, "全局：清晰度");
        Check.Equal(24, local.FrameRate, "全局：帧率");
        Check.Equal(60, local.VideoQuality, "全局：质量");
        Check.Equal(800, local.TrailDurationMs, "全局：拖尾");
        Check.Equal(@"D:\rec", local.SaveDirectory, "全局：保存目录");
        Check.True(local.FollowMouse, "全局：跟随鼠标");
        Check.False(local.SystemAudio, "本地：声音不被覆盖");
        Check.Equal(2, local.CameraDevice, "本地：摄像头不被覆盖");
        Check.Equal(RecordCodec.hevc, local.Codec, "本地：编码器不被覆盖");

        // ---- 编码器参数：质量 → crf / cq / qp，速度档 → preset ----
        string x264 = string.Join(" ", RecordEncoders.Arguments(RecordEncoders.X264, 80, "medium"));
        Check.True(x264.Contains("-preset medium") && x264.Contains("-crf 23"), "x264 质量 80 → crf 23");
        string x265 = string.Join(" ", RecordEncoders.Arguments(RecordEncoders.X265, 100, "slow"));
        Check.True(x265.Contains("-crf 16") && x265.Contains("-preset slow") && x265.Contains("hvc1"),
            "x265 质量 100 → crf 16 + hvc1");
        string nv = string.Join(" ", RecordEncoders.Arguments("h264_nvenc", 80, "veryfast"));
        Check.True(nv.Contains("-cq 23") && nv.Contains("-preset p1"), "NVENC 用 cq 与 p 档");
        string nvSlow = string.Join(" ", RecordEncoders.Arguments("h264_nvenc", 80, "veryslow"));
        Check.True(nvSlow.Contains("-preset p6"), "慢档映射 p6");
        string amf = string.Join(" ", RecordEncoders.Arguments("h264_amf", 80, "medium"));
        Check.True(amf.Contains("-qp_i 23") && amf.Contains("-qp_p 23") && amf.Contains("-qp_b 25"),
            "AMF 用 qp");
        string qsv = string.Join(" ", RecordEncoders.Arguments("hevc_qsv", 80, "medium"));
        Check.True(qsv.Contains("-global_quality 26"), "HEVC 质量偏保守（+3）");

        // ---- MP4：清晰度 / 质量 / 速度档 ----
        string mp4 = string.Join(" ", RecordPlan.Mp4Args(new Int32Rect(0, 0, 1920, 1080), 30, "o.mp4",
            RecordEncoders.X264, 720, 60, "medium"));
        Check.True(mp4.Contains("min(ih,720)"), "清晰度按高度缩");
        Check.True(mp4.Contains("-preset medium") && mp4.Contains("-crf 30"), "质量 60 → crf 30");
        string noScale = string.Join(" ", RecordPlan.Mp4Args(new Int32Rect(0, 0, 1280, 720), 30, "o.mp4",
            RecordEncoders.X264, 1080));
        Check.False(noScale.Contains("min(ih,1080)"), "比清晰度小的画面不放大");

        // ---- 动图：清晰度 / 循环 ----
        string palette = string.Join(" ", RecordPlan.GifPaletteArgs("in.mp4", "p.png", 10, 480));
        Check.True(palette.Contains("fps=10") && palette.Contains("min(ih,480)"), "GIF 按设置帧率与清晰度");
        string gifOnce = string.Join(" ", RecordPlan.GifEncodeArgs("in.mp4", "p.png", "o.gif", 10, 480,
            loop: false));
        Check.True(gifOnce.Contains("-loop 1"), "不循环 = -loop 1");
        string apng = string.Join(" ", RecordPlan.AnimatedArgs("in.mp4", "o.apng", RecordFormat.Apng, 20,
            1080, loop: false));
        Check.True(apng.Contains("fps=20") && apng.Contains("min(ih,1080)") && apng.Contains("-plays 1"),
            "APNG 帧率 / 清晰度 / 只播一次");
        string webp = string.Join(" ", RecordPlan.AnimatedArgs("in.mp4", "o.webp", RecordFormat.Webp, 20, 720));
        Check.True(webp.Contains("min(ih,720)") && webp.Contains("-loop 0"), "WebP 无限循环");

        // ---- 收尾：分离音轨（上游 separate_audio_tracks）----
        string separate = string.Join(" ", RecordPlan.FinalizeArgs("in.mp4", "out.mp4",
            new[] { "sys.wav", "mic.wav" }, new[] { 0, 0 }, separateTracks: true));
        Check.False(separate.Contains("amix"), "分离音轨不混音");
        Check.True(separate.Contains("-map [a0]") && separate.Contains("-map [a1]"), "两路各映射一条");
        Check.True(separate.Contains("-c:a aac"), "两条都编 AAC");

        // ---- 后期处理「进度条」：收尾后单独过一遍 drawbox ----
        string bar = string.Join(" ", RecordPlan.ProgressBarArgs("in.mp4", "out.mp4", 10_000,
            "#1677FFFF", 80, "veryfast"));
        Check.True(bar.Contains("drawbox"), "进度条用 drawbox");
        Check.True(bar.Contains("0x77FFFF@0.086"), "颜色取 #RRGGBB + alpha 0–1（#16 = 0.086）");
        Check.True(bar.Contains("t/10"), "按总时长填满");
        Check.True(bar.Contains("-crf 23"), "重编码沿用同一套质量");

        // ---- 设置默认值进计划（覆盖层 / 内置功能入口）----
        var plan = RecordPlan.FromSettings(new RecordOptions
        {
            OutputFormat = "gif",
            FrameRate = 24,
            StartDelaySeconds = 3,
            FollowMouse = true,
            AnimatedFrameRate = 20,
        });
        Check.Equal(RecordFormat.Gif, plan.Format, "格式取设置默认值");
        Check.Equal(24, plan.Fps, "帧率取设置默认值");
        Check.Equal(3, plan.Countdown, "等待秒数取设置默认值");
        Check.True(plan.FollowMouse, "跟随鼠标取设置默认值");
        Check.Equal(15, plan.GifFps, "GIF 动图帧率取设置并压到 15");
        Check.Equal(20, RecordPlan.FromSettings(new RecordOptions
        {
            OutputFormat = "apng",
            AnimatedFrameRate = 20,
        }).GifFps, "APNG 帧率不受 15 限制");
        Check.False(plan.PromptStart, "默认不摆面板");
        Check.True(RecordPlan.FromSettings(null, promptStart: true).PromptStart, "覆盖层入口要摆面板");
    }

    /// <summary>
    /// 键鼠特效的纯函数（涟漪 / 拖尾 / 按键 / 坐标映射）+ 摄像头画中画落点。
    /// 参数照上游 <c>snow-recording-effects</c> 与 <c>snow-recording-model/src/webcam.rs</c>。
    /// </summary>
    private static void RecordEffectRules()
    {
        // ---- 涟漪：600ms 内 7 → 40 半径，透明度线性降到 0 ----
        var first = RecordEffectsMath.ClickRipple(0, true);
        Check.True(first != null, "0ms 还在生命周期里");
        Check.Equal(RecordEffectsMath.ClickStartRadius, first!.Value.Radius, "起点半径 7");
        Check.Equal(255, first.Value.Alpha, "起点不透明");
        Check.Equal(3, first.Value.Thickness, "左键描边 3");

        var mid = RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs / 2, false);
        Check.True(mid != null, "过半还在生命周期里");
        Check.Equal(23, mid!.Value.Radius, "过半半径 7 + round(16.5) = 23");
        Check.Equal(2, mid.Value.Thickness, "右键描边 2");

        var last = RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs, true);
        Check.True(last != null, "最后一刻还在");
        Check.Equal(RecordEffectsMath.ClickEndRadius, last!.Value.Radius, "终点半径 40");
        Check.Equal(0, last.Value.Alpha, "终点完全透明");

        Check.True(RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs + 1, true) == null,
            "超过 600ms 就没了");
        Check.True(RecordEffectsMath.ClickRipple(-5, true) == null, "时间倒流当作没有");

        // ---- 拖尾：500ms 线性淡出，上限 50 点 ----
        Check.Equal(220, RecordEffectsMath.TrailAlpha(0), "刚画的点最深");
        Check.Equal(110, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs / 2), "半程一半深");
        Check.Equal(0, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs), "到期透明");
        Check.Equal(0, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs + 1), "过期透明");
        Check.Equal(50, RecordEffectsMath.LaserMaxPoints, "最多 50 个点");

        // ---- 按键浮层：先上浮 180ms，停留到 1200ms，再淡出 400ms ----
        var start = RecordEffectsMath.KeyCapState(0);
        Check.True(start != null, "刚按下还在");
        Check.Equal(255, start!.Value.Alpha, "刚按下不透明");
        Check.Equal(0.0, start.Value.Slide, "刚按下没上浮");

        var risen = RecordEffectsMath.KeyCapState(RecordEffectsMath.KeyMoveMs);
        Check.Equal(1.0, risen!.Value.Slide, "180ms 时浮到位");

        var hold = RecordEffectsMath.KeyCapState(RecordEffectsMath.KeyHoldMs);
        Check.Equal(255, hold!.Value.Alpha, "停留期内仍然不透明");
        Check.Equal(1.0, hold.Value.Slide, "停留期内保持浮起");

        int halfFade = RecordEffectsMath.KeyHoldMs + RecordEffectsMath.KeyFadeMs / 2;
        Check.True(RecordEffectsMath.KeyCapState(halfFade)!.Value.Alpha is > 100 and < 200,
            "淡出过半时透明度落在中间");

        Check.True(RecordEffectsMath.KeyCapState(
            RecordEffectsMath.KeyHoldMs + RecordEffectsMath.KeyFadeMs + 1) == null, "淡出结束就没了");
        Check.True(RecordEffectsMath.KeyCapState(-1) == null, "时间倒流当作没有");

        // ---- 修饰键组合名 ----
        Check.Equal("Ctrl+Shift+A",
            RecordEffectsMath.ComboLabel(new HashSet<int> { 0x11, 0x10 }, "A"), "修饰键拼在前面");
        Check.Equal("A", RecordEffectsMath.ComboLabel(new HashSet<int>(), "A"), "没有修饰键就是键名");
        Check.True(RecordEffectsMath.IsModifier(0x11) && RecordEffectsMath.IsModifier(0x5B), "Ctrl / Win 是修饰键");
        Check.False(RecordEffectsMath.IsModifier(0x41), "字母不是修饰键");
        Check.True(RecordEffectsMath.WatchedKeys.Count > 60, "要轮询的键有全套（字母 / 数字 / 功能键）");

        // ---- 坐标映射（跟随鼠标换区域后仍对得上）----
        var area = new Int32Rect(100, 200, 800, 600);
        var mapped = RecordEffectsMath.Map(500, 500, area, 400, 300);
        Check.Equal(200, mapped.X, "横向按比例缩到一半");
        Check.Equal(150, mapped.Y, "纵向按比例缩到一半");

        Check.Equal(0, RecordEffectsMath.Map(100, 200, area, 400, 300).X, "区域左上角映射到 0");
        Check.Equal(-19, RecordEffectsMath.MapAxis(-40, 800, 400),
            "区域外的点映射成负数（不夹到边界，画面上自然裁掉）");
        Check.Equal(7, RecordEffectsMath.MapAxis(7, 0, 400), "源尺寸为 0 时原样返回（不做除法）");
        Check.Equal(7, RecordEffectsMath.MapAxis(14, 800, 400), "整数四舍五入：14/800*400 = 7");

        // ---- 画中画落点：贴左下，放不下就收进来 ----
        var corner = RecordOptions.CameraPlacement(1920, 1080, 150, 48, 48);
        Check.Equal(48, corner.X, "左边距 48");
        Check.Equal(1080 - 48 - 150, corner.Y, "底边距 48（Y 从上算）");
        Check.Equal(150, corner.Side, "边长照给的值");

        var tight = RecordOptions.CameraPlacement(100, 100, 400, 48, 48);
        Check.Equal(100, tight.Side, "画中画不能比画面还大");
        Check.Equal(0, tight.X, "放不下就贴到左上（不留负边距）");
        Check.Equal(0, tight.Y, "纵向同理");

        var huge = RecordOptions.CameraPlacement(600, 400, 150, 999, 999);
        Check.Equal(600 - 150, huge.X, "边距超过画面时收到右下最大位置");
        Check.Equal(0, huge.Y, "纵向收到 0");
    }

}
