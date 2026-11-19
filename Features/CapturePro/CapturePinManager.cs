using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图组：所有还开着的贴图窗口的登记处（分组 / 多选批量 / 「关闭全部贴图」/ 顶回最前 / 成组移动）。
/// </summary>
/// <remarks>
/// <para>
/// 贴图**不进配置**（与悬浮项同一口径）：关掉程序就没了，重启不恢复——
/// 它们是一次性的临时视图，不是用户资产。分组因此也只在内存里，组空了自动消失（默认组永远在）。
/// </para>
/// <para>
/// **切分组是隐藏不是关闭**：别的组只是 <c>Hide()</c>，切回来原位 <c>Show()</c>。
/// 所以凡是「把所有贴图一起做点什么」的地方（拖动参与者、全选、顶回最前）都必须按
/// <see cref="IsShown"/> 过滤，否则会把藏起来的贴图一起拖走 / 在看不见的分组上做批量。
/// </para>
/// <para>
/// 只在 UI 线程用：建窗口、<c>Show()</c>、关闭都必须在 WPF 的 Dispatcher 上。
/// 关窗由窗口自己 <see cref="Forget"/> 摘掉，所以用户的 ✕ / 双击 / 关闭全部三条路都对得上。
/// </para>
/// </remarks>
internal static class CapturePinManager
{
    private static readonly List<CapturePinWindow> Pins = new();

    /// <summary>分组名单（第一个永远是 <see cref="PinGroups.Default"/>）。</summary>
    private static readonly List<string> GroupList = new() { PinGroups.Default };

    /// <summary>多选中的贴图（批量操作的接收方）。</summary>
    private static readonly List<CapturePinWindow> Selection = new();

    /// <summary>拖动开始时的落点快照（成组移动按「起点 + 同一个位移」算，逐次累加会攒误差）。</summary>
    private static readonly List<(CapturePinWindow Pin, Rect Box)> MoveBase = new();

    private static CapturePinWindow? _moving;

    /// <summary>还开着的贴图数量（含被分组藏起来的）。</summary>
    public static int Count => Pins.Count;

    /// <summary>当前贴图（按出现的先后，含被分组藏起来的）。</summary>
    public static IReadOnlyList<CapturePinWindow> All => Pins;

    /// <summary>看得见的贴图（当前没有分组过滤 = 全部）。</summary>
    public static IReadOnlyList<CapturePinWindow> Visible => Pins.Where(IsShown).ToArray();

    /// <summary>分组名单（默认组在最前，其余按建的先后）。</summary>
    public static IReadOnlyList<string> Groups => GroupList;

    /// <summary>现在只看哪个分组（<c>null</c> = 显示全部）。</summary>
    public static string? ActiveGroup { get; private set; }

    /// <summary>多选中的贴图（批量关闭 / 另存 / 锁定 / 缩略图的接收方）。</summary>
    public static IReadOnlyList<CapturePinWindow> Selected => Selection;

    /// <summary>
    /// 成组移动：开着时拖任意一张，全组一起走（默认关，跟着进程，不落盘）。
    /// 只有一张时没有「组」可言，菜单里会置灰。
    /// </summary>
    public static bool GroupMove { get; set; }

    /// <summary>这张贴图现在该不该看得见（分组过滤）。</summary>
    public static bool IsShown(CapturePinWindow pin) =>
        ActiveGroup == null || PinGroups.Same(pin.Group, ActiveGroup);

    /// <summary>
    /// 把一张图贴到屏幕上（内容正好盖住它截下来的那块区域）。
    /// </summary>
    /// <param name="source">来源（<see cref="PinStore.SourceScreenshot"/> 等），进贴图历史用。</param>
    /// <param name="appearance">
    /// 图自带的选区外观（圆角 / 阴影 / 边框）。带效果时图比选区大一圈，
    /// 窗口按它往四周让出内边距，**内容仍然落在选区上**（照上游 <c>PinnedBorderAppearance</c>）。
    /// </param>
    public static CapturePinWindow Pin(BitmapSource image, Int32Rect area,
        string source = PinStore.SourceScreenshot, PinAppearance? appearance = null)
    {
        var look = (appearance ?? PinAppearance.None).Clamp(image.PixelWidth, image.PixelHeight);
        var box = look.WindowRect(area, image.PixelWidth, image.PixelHeight);

        // 缩放比按这块区域所在显示器算：跨屏选区时不能用「覆盖层那块屏」的比例
        double scale = ScreenShot.ScaleOf(box);
        var pin = new CapturePinWindow(image, box, scale);
        pin.SetAppearance(look);
        return Register(pin, image, box, scale, source, PinStore.KindImage, "", 0, null, look);
    }

    /// <summary>
    /// 贴剪贴板里的文字 / 颜色：按屏幕缩放渲染成卡片，放在 <paramref name="workArea"/>（DIP）中间，
    /// 装不下就等比缩小（照上游 <c>fitPinnedImageOnScreen</c>）。
    /// </summary>
    public static CapturePinWindow PinText(string text, double scale, Rect workArea,
        string source = PinStore.SourceClipboard)
    {
        double s = scale > 0 ? scale : 1;
        var style = PinTextStyle.Current();
        var snapshot = PinTextSnapshot.Of(style);
        var card = PinCardRenderer.Render(text, style, s);

        var (box, zoom) = PinPlacement.Center(card.Logical, workArea);
        var area = new Int32Rect(
            (int)Math.Round(box.X * s), (int)Math.Round(box.Y * s),
            Math.Max(1, (int)Math.Round(box.Width * s)), Math.Max(1, (int)Math.Round(box.Height * s)));

        var pin = new CapturePinWindow(card, area, s, zoom, text);
        return Register(pin, card.Image, area, s, source,
            card.Kind == PinContentKind.Color ? PinStore.KindColor : PinStore.KindText,
            text, card.Kind == PinContentKind.Color ? (int)card.Logical.Width : 0, snapshot);
    }

    /// <summary>贴剪贴板里的一张图：居中放到 <paramref name="workArea"/>（DIP），超大等比缩小。</summary>
    public static CapturePinWindow PinClipboardImage(BitmapSource image, double scale, Rect workArea,
        string source = PinStore.SourceClipboard, PinAppearance? appearance = null)
    {
        double s = scale > 0 ? scale : 1;
        var logical = new Size(Math.Max(1, image.PixelWidth / s), Math.Max(1, image.PixelHeight / s));
        var (box, zoom) = PinPlacement.Center(logical, workArea);
        var area = new Int32Rect(
            (int)Math.Round(box.X * s), (int)Math.Round(box.Y * s),
            Math.Max(1, (int)Math.Round(box.Width * s)), Math.Max(1, (int)Math.Round(box.Height * s)));

        var look = (appearance ?? PinAppearance.None).Clamp(image.PixelWidth, image.PixelHeight);
        var pin = new CapturePinWindow(image, area, s, zoom);
        pin.SetAppearance(look);
        return Register(pin, image, area, s, source, PinStore.KindImage, "", 0, null, look);
    }

    /// <summary>
    /// 采集历史「贴到屏幕」（照上游 <c>pinScreenshotHistoryRecord</c>）：
    /// 能按原选区贴回就贴回它当初的位置（内容正好盖住原来那块），
    /// 不能（长截图 / 老记录 / 那块屏现在没了）就居中贴到光标所在屏。
    /// </summary>
    /// <returns>贴上了返回窗口；图片文件读不出来返回 <c>null</c>。</returns>
    public static CapturePinWindow? PinHistory(CaptureEntry entry)
    {
        if (entry == null || entry.IsRecord) return null;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id));
        if (image == null) return null;

        var appearance = HistoryPinPlacement.Appearance(entry);

        // 原位置：选区还在当前桌面上（显示器拔了 / 分辨率改了就不硬摆到看不见的地方）
        if (HistoryPinPlacement.WindowRect(entry, image.PixelWidth, image.PixelHeight) is Int32Rect at
            && HistoryPinPlacement.MeetsDesktop(
                HistoryPinPlacement.Selection(entry) ?? at, ScreenShot.Monitors()))
        {
            double scale = ScreenShot.ScaleOf(at);
            var pin = new CapturePinWindow(image, at, scale);
            pin.SetAppearance(appearance);
            return Register(pin, image, at, scale, PinStore.SourceHistory, PinStore.KindImage,
                "", 0, null, appearance);
        }

        double fallbackScale = PinClipboard.ScreenScale();
        var work = PinClipboard.WorkAreaDip(fallbackScale);
        return PinClipboardImage(image, fallbackScale, work, PinStore.SourceHistory, appearance);
    }

    /// <summary>出场收尾：分组、历史记录（先写 pins.json）、显示、顶最前，图片丢后台写。</summary>
    private static CapturePinWindow Register(CapturePinWindow pin, BitmapSource image, Int32Rect area,
        double scale, string source, string kind, string text, int cardWidth,
        PinTextSnapshot? textStyle = null, PinAppearance? appearance = null)
    {
        // 正只看某个分组时，新贴图落进那个组 —— 否则贴上来立刻被自己藏起来，看着像「贴图没反应」
        string group = ActiveGroup ?? PinGroups.Default;
        pin.SetGroup(group);

        // 贴图历史：先记一条（只写 pins.json），图片丢后台写，别卡 UI 线程
        var record = PinStore.Create(image.PixelWidth, image.PixelHeight,
            area.X, area.Y, area.Width, area.Height, scale, source, group, live: false,
            kind: kind, text: text, cardWidth: cardWidth, textStyle: textStyle,
            appearance: appearance);
        pin.PinId = record.Id;

        Pins.Add(pin);
        AllHidden = false;   // 新贴上来一张 = 用户现在要看屏幕上的贴图
        pin.Show();          // 无激活窗口：ShowActivated=False + WS_EX_NOACTIVATE
        pin.RaiseToTop();
        PinStacking.Presented(pin, preserveFocus: false);   // 新贴的排到组最前
        Sync(pin);

        System.Threading.Tasks.Task.Run(() =>
        {
            PinStore.SaveAssets(record, image);
            // 「贴图到屏幕」勾了自动保存：贴图历史那份是截图缩略 + 原图，这里再按「截图输出」写一份
            if (kind == PinStore.KindImage)
                CaptureSave.AutoSave(image, ExportSettings.ScenarioPin);
        });
        return pin;
    }

    /// <summary>
    /// 把窗口当前的落点 / 尺寸 / 分组 / 实时开关 / 点击穿透写回贴图历史
    /// （记录不存在或 Id 为空是空操作）。
    /// </summary>
    internal static void Sync(CapturePinWindow pin)
    {
        PinStore.UpdateState(pin.PinId, pin.Group, pin.Left, pin.Top, pin.Width, pin.Height, pin.Live);
        PinStore.UpdateClickThrough(pin.PinId, pin.ClickThrough, pin.ClickThroughOpacity);
    }

    /// <summary>
    /// 贴一块**实时**的图：跟住 <paramref name="hwnd"/>，每 200ms 刷新一次内容，
    /// 窗口尺寸 / 位置仍是 <paramref name="area"/> 那块（源窗口挪开的地方留透明）。
    /// </summary>
    /// <remarks>
    /// 只是 <see cref="Pin"/> 之后多开一次实时刷新，所以分组 / 多选 / 拖动 / 关闭全部
    /// 与普通贴图完全同一条路径（<see cref="CapturePinWindow.StopLive"/> 之后就是普通贴图）。
    /// </remarks>
    public static CapturePinWindow PinLive(BitmapSource image, Int32Rect area, IntPtr hwnd)
    {
        var pin = Pin(image, area);
        pin.StartLive(hwnd);
        Sync(pin);            // 记成实时贴图（历史里看得见，关闭时改回 false）
        return pin;
    }

    /// <summary>找这张贴图当前对应的记录（关掉窗口之后就没有了）。</summary>
    public static CapturePinWindow? FindByRecord(string id) =>
        id.Length == 0 ? null : Pins.FirstOrDefault(pin => pin.PinId == id);

    /// <summary>关掉某条记录对应的贴图（管理页删除 / 关闭用）；不在屏幕上返回 false。</summary>
    public static bool CloseRecord(string id)
    {
        var pin = FindByRecord(id);
        if (pin == null) return false;
        pin.Close();
        return true;
    }

    /// <summary>
    /// 把历史里的一条重新贴到屏幕上（管理页「贴回屏幕」）：整条记录复用，不新建。
    /// 图读不出来返回 <c>null</c>（调用方提示「图片文件不见了」）。
    /// </summary>
    public static CapturePinWindow? Restore(PinRecord record) => RestoreCore(record, preserveFocus: false);

    /// <summary>
    /// 批量贴回（管理页「批量贴到屏幕」）：按给定顺序逐张贴出来，整批结束再按记录槽位排一次。
    /// </summary>
    /// <remarks>
    /// 照上游管理页走的 <c>showRecords</c>：全程 <c>PreserveFocus</c>，**每张都回到它在
    /// 记录里的槽位**（关掉时槽位保留），不因为「这一批刚贴出来」就整批顶到最前面。
    /// 整批期间挂起「原生 z 序观测」（中间态没有意义、每张都写一次盘也白费），结束统一落一次。
    /// 同一张已经贴在屏幕上的直接复用（不新建窗口）。
    /// </remarks>
    /// <returns>这次新贴出来几张（已开着的不算）。</returns>
    public static int RestoreBatch(IReadOnlyList<PinRecord> records)
    {
        if (records == null || records.Count == 0) return 0;

        int opened = 0;

        using (PinStacking.Batch())
        {
            foreach (var record in records)
            {
                var existing = FindByRecord(record.Id);
                if (existing != null)
                {
                    if (!existing.IsVisible) existing.Show();
                    continue;
                }

                if (RestoreCore(record, preserveFocus: true) != null) opened++;
            }
        }

        PinStacking.Apply();
        return opened;
    }

    private static CapturePinWindow? RestoreCore(PinRecord record, bool preserveFocus)
    {
        // 文字 / 颜色卡：按原文重画（上游也是从 stored text 重渲染，不依赖图片文件）
        if (PinStore.IsTextual(record))
        {
            double scale = record.Scale > 0 ? record.Scale : 1.0;
            var style = record.TextStyle?.ToStyle() ?? PinTextStyle.Current();
            var card = PinCardRenderer.Render(record.Text, style, scale, record.CardWidth);

            double width = record.WinWidth >= 1 ? record.WinWidth : card.Logical.Width;
            double zoom = card.Logical.Width > 0 ? PinMath.ClampZoom(width / card.Logical.Width) : 1;

            var textArea = new Int32Rect(record.AreaX, record.AreaY,
                Math.Max(1, (int)Math.Round(card.Logical.Width * zoom * scale)),
                Math.Max(1, (int)Math.Round(card.Logical.Height * zoom * scale)));
            var textPin = new CapturePinWindow(card, textArea, scale, zoom, record.Text)
            {
                PinId = record.Id,
            };

            if (record.Left != 0 || record.Top != 0)
            {
                textPin.Left = record.Left;
                textPin.Top = record.Top;
            }

            return FinishRestore(textPin, record, preserveFocus);
        }

        var image = PinStore.LoadAsset(record.Id);
        if (image == null) return null;

        var area = new Int32Rect(record.AreaX, record.AreaY,
            record.AreaW > 0 ? record.AreaW : record.Width,
            record.AreaH > 0 ? record.AreaH : record.Height);
        var pin = new CapturePinWindow(image, area, record.Scale > 0 ? record.Scale : 1.0)
        {
            PinId = record.Id,
        };
        pin.SetAppearance(record.Appearance);

        string group = CapturePinManager.CreateGroup(record.Group) ?? PinGroups.Default;
        pin.SetGroup(group);

        // 尺寸与落点按记录复位；从没记过落点（0,0）就用默认的「盖住源区域」
        if (record.WinWidth >= 1 && record.WinHeight >= 1)
        {
            pin.Width = record.WinWidth;
            pin.Height = record.WinHeight;
        }

        if (record.Left != 0 || record.Top != 0)
        {
            var box = new Rect(record.Left, record.Top, pin.Width, pin.Height);
            Vector delta = PinMath.ConstrainGroupMove(new Vector(0, 0), new[] { box },
                PinMath.VirtualDesktop, PinMath.KeepVisible);
            pin.Left = record.Left + delta.X;
            pin.Top = record.Top + delta.Y;
        }

        return FinishRestore(pin, record, preserveFocus);
    }

    /// <summary>贴回的共同收尾：分组、记录回到「还开着」、显示、顶最前、同步落点。</summary>
    private static CapturePinWindow FinishRestore(CapturePinWindow pin, PinRecord record, bool preserveFocus)
    {
        string group = CreateGroup(record.Group) ?? PinGroups.Default;
        pin.SetGroup(group);

        PinStore.Reopen(record.Id);
        Pins.Add(pin);
        AllHidden = false;   // 贴回一张 = 现在要看见它
        pin.Show();
        pin.RaiseToTop();
        PinStacking.Presented(pin, preserveFocus);
        Sync(pin);

        // 关的时候是穿透态就还回穿透态（连不透明度一起），否则贴回一张「点不到的图」会让人以为卡死
        if (record.ClickThrough)
        {
            pin.SetClickThroughOpacity(record.ClickThroughOpacity);
            pin.SetClickThrough(true);
        }

        return pin;
    }

    /// <summary>
    /// 恢复**最近关闭的那一张**（照上游 <c>restoreLastClosedPinnedWindow</c>）：
    /// 按关闭时间取最新的一条记录整条贴回来（原落点、原分组、原外观）。
    /// 没有可恢复的记录返回 <c>null</c>。
    /// </summary>
    public static CapturePinWindow? RestoreLastClosed()
    {
        var open = new HashSet<string>(Pins.Select(pin => pin.PinId), StringComparer.Ordinal);
        var record = PinStore.Load()
            .Where(item => item.ClosedAt.Length > 0 && !open.Contains(item.Id))
            .OrderByDescending(item => item.ClosedAt, StringComparer.Ordinal)
            .FirstOrDefault();

        return record == null ? null : Restore(record);
    }

    /// <summary>贴图被「隐藏所有贴图」收起来了（再按一次会放回来）。</summary>
    public static bool AllHidden { get; private set; }

    /// <summary>收起所有看得见的贴图（不关窗、不写历史，位置原样留着）。返回收起几张。</summary>
    public static int HideAll()
    {
        EndMove();
        ClearSelection();

        int count = 0;
        foreach (var pin in Pins)
        {
            if (!pin.IsVisible) continue;
            pin.Hide();
            count++;
        }

        if (count > 0) AllHidden = true;
        return count;
    }

    /// <summary>把「隐藏所有贴图」收起来的放回来（分组过滤里看不见的那些不放）。返回放回几张。</summary>
    public static int ShowAll()
    {
        int count = 0;
        foreach (var pin in Pins)
        {
            if (pin.IsVisible || !IsShown(pin)) continue;
            pin.Show();
            count++;
        }

        AllHidden = false;
        return count;
    }

    /// <summary>全关（右键菜单「关闭全部贴图」，含被分组藏起来的）。</summary>
    public static void CloseAll()    {
        EndMove();
        ClearSelection();

        using (PinStacking.Batch())
        {
            foreach (var pin in Pins.ToArray()) pin.Close();
        }

        Pins.Clear();
        PinStacking.Reset();
        GroupList.RemoveAll(group => !PinGroups.Same(group, PinGroups.Default));
        ActiveGroup = null;
    }

    // ============ 分组 ============

    /// <summary>
    /// 新建分组；名字清洗后为空 = <c>null</c>（调用方提示）。已经存在（忽略大小写）就返回原来那个，
    /// 不会出现「外部」与「外部 」两个组。
    /// </summary>
    public static string? CreateGroup(string? raw)
    {
        string? name = PinGroups.Clean(raw);
        if (name == null) return null;

        var existing = PinGroups.Find(name, GroupList);
        if (existing != null) return existing;

        GroupList.Add(name);
        return name;
    }

    /// <summary>把一张贴图挪到某个分组（组不存在就先建）。挪出当前看着的组就当场藏起来。</summary>
    public static void MoveToGroup(CapturePinWindow pin, string group)
    {
        string? name = PinGroups.Find(group, GroupList) ?? CreateGroup(group);
        if (name == null) return;

        pin.SetGroup(name);
        if (!Pins.Contains(pin)) return;

        Sync(pin);            // 分组变了要落进历史（管理页按分组筛）

        if (IsShown(pin))
        {
            if (!pin.IsVisible) pin.Show();
            pin.RaiseToTop();
            PinStacking.Presented(pin, preserveFocus: false);   // 搬进新组 = 排到那组最前
        }
        else if (pin.IsVisible)
        {
            pin.Hide();
        }
    }

    /// <summary>
    /// 只看一个分组（<c>null</c> = 显示全部）：别的组 <c>Hide()</c>，切回来原位 <c>Show()</c>。
    /// 拖动与多选一并收掉——藏起来的窗口不该还留在「已经选中 / 正在拖」的状态里。
    /// </summary>
    public static void SetActiveGroup(string? group)
    {
        EndMove();
        ClearSelection();

        ActiveGroup = group == null ? null : PinGroups.Find(group, GroupList);

        foreach (var pin in Pins)
        {
            bool show = IsShown(pin);
            if (show && !pin.IsVisible)
            {
                pin.Show();
                pin.RaiseToTop();
            }
            else if (!show && pin.IsVisible)
            {
                pin.Hide();
            }
        }

        // 露出来的按记录槽位排一次：否则「最后 Show 的那张」会盖住本该在它前面的
        PinStacking.Apply(ActiveGroup);
    }

    // ============ 多选与批量 ============

    /// <summary>这张贴图在不在多选里。</summary>
    public static bool IsSelected(CapturePinWindow pin) => Selection.Contains(pin);

    /// <summary><c>Ctrl</c>+单击：选中 / 取消选中这一张（贴图不抢焦点，键盘收不到，所以只有鼠标这一条路）。</summary>
    public static void ToggleSelect(CapturePinWindow pin)
    {
        if (Selection.Remove(pin))
        {
            pin.SetSelected(false);
            return;
        }

        Selection.Add(pin);
        pin.SetSelected(true);
    }

    /// <summary>取消全部选择。</summary>
    public static void ClearSelection()
    {
        if (Selection.Count == 0) return;
        foreach (var pin in Selection) pin.SetSelected(false);
        Selection.Clear();
    }

    /// <summary>全选（只看当前分组里看得见的那些）。</summary>
    public static int SelectAll()
    {
        ClearSelection();
        foreach (var pin in Pins)
        {
            if (!IsShown(pin)) continue;
            Selection.Add(pin);
            pin.SetSelected(true);
        }
        return Selection.Count;
    }

    /// <summary>关闭选中的（返回关掉几张）。</summary>
    public static int CloseSelected()
    {
        var targets = Selection.ToArray();
        ClearSelection();

        using (PinStacking.Batch())
        {
            foreach (var pin in targets) pin.Close();
        }

        PinStacking.Apply();
        return targets.Length;
    }

    /// <summary>批量锁定 / 解锁。</summary>
    public static void SetSelectedLocked(bool locked)
    {
        foreach (var pin in Selection.ToArray()) pin.SetLocked(locked);
    }

    /// <summary>批量缩略图 / 还原。</summary>
    public static void SetSelectedThumbnail(bool on)
    {
        foreach (var pin in Selection.ToArray()) pin.SetThumbnail(on);
    }

    // ============ 抖动隐藏同组其他贴图 ============

    /// <summary>
    /// 抖动命中那一下：同组其他贴图**都藏着**就全放出来，否则全藏起来
    /// （照上游 <c>toggleOtherWindowsForShake</c>）。
    /// </summary>
    /// <returns>
    /// <c>Changed</c> = 真的动了别的贴图（没有可切换的对象时是 false，界面据此决定要不要提示）；
    /// <c>Showed</c> = 这次是「放出来」还是「藏起来」。
    /// </returns>
    internal static (bool Changed, bool Showed) ToggleOthersForShake(CapturePinWindow source)
    {
        if (!source.IsVisible) return (false, false);

        // 只碰同一组、当前分组过滤下本来就该看得见、且不是实时贴图的那些
        var others = Pins.Where(pin =>
                !ReferenceEquals(pin, source) &&
                !pin.Live &&
                PinGroups.Same(pin.Group, source.Group) &&
                IsShown(pin))
            .ToArray();

        if (others.Length == 0) return (false, false);

        bool show = others.All(pin => !pin.IsVisible);
        foreach (var pin in others)
        {
            if (show)
            {
                if (!pin.IsVisible) { pin.Show(); pin.RaiseToTop(); }
            }
            else
            {
                if (pin.IsVisible) pin.Hide();
            }
        }

        // 藏起来再放出来不该打乱前后序（Show + 顶最前会让最后一张盖住全部）
        if (show) PinStacking.Apply(source.Group);

        return (true, show);
    }

    // ============ 拖动 / 成组移动 ============

    /// <summary>
    /// 拖动开始：记下这次的参与者与它们的落点。三种情况一条路径（<see cref="MoveBy"/> 只认快照）：
    /// 拖的那张在多选里 = 只动选中的那些；否则成组移动开着 = 看得见的全组；再否则只有自己。
    /// </summary>
    public static void BeginMove(CapturePinWindow source)
    {
        MoveBase.Clear();
        _moving = source;

        // 拖一张没选中的：用户显然在操作这一张，把选择收起来
        // （否则菜单里还写着「已选 N 张」，跟手上做的事对不上）
        if (Selection.Count > 0 && !Selection.Contains(source)) ClearSelection();

        CapturePinWindow[] participants = Selection.Count > 0 && Selection.Contains(source)
            ? Selection.Where(IsShown).ToArray()
            : GroupMove
                ? Pins.Where(IsShown).ToArray()
                : new[] { source };

        foreach (var pin in participants)
            MoveBase.Add((pin, new Rect(pin.Left, pin.Top, pin.Width, pin.Height)));
    }

    /// <summary>
    /// 拖到哪：位移先按整组夹一次（谁都不许被推出桌面），再按「起点 + 位移」写回。
    /// 松手前重复调用也只累一个位移，不会越拖越飘。
    /// </summary>
    public static void MoveBy(CapturePinWindow source, double dx, double dy)
    {
        if (!ReferenceEquals(_moving, source) || MoveBase.Count == 0) return;

        var boxes = new Rect[MoveBase.Count];
        for (int i = 0; i < boxes.Length; i++) boxes[i] = MoveBase[i].Box;

        Vector delta = PinMath.ConstrainGroupMove(new Vector(dx, dy), boxes, PinMath.VirtualDesktop,
            PinMath.KeepVisible);

        foreach (var (pin, box) in MoveBase)
        {
            pin.Left = box.Left + delta.X;
            pin.Top = box.Top + delta.Y;
        }
    }

    /// <summary>松手：丢掉快照（下一次拖动重新取起点）。顺手把落点写回贴图历史。</summary>
    public static void EndMove()
    {
        foreach (var (pin, _) in MoveBase) Sync(pin);
        if (_moving != null && MoveBase.Count == 0) Sync(_moving);

        _moving = null;
        MoveBase.Clear();
    }

    /// <summary>窗口关闭时摘掉自己（重复调用无害）：多选 / 拖动快照 / 空分组都跟着收敛。</summary>
    internal static void Forget(CapturePinWindow pin)
    {
        // 历史先记关闭时间与最后落点（关掉之后窗口属性仍然可读）
        if (pin.PinId.Length > 0)
            PinStore.MarkClosed(pin.PinId, pin.Left, pin.Top, pin.Width, pin.Height);

        PinStacking.Retiring(pin);
        Selection.Remove(pin);
        Pins.Remove(pin);
        MoveBase.RemoveAll(entry => ReferenceEquals(entry.Pin, pin));
        if (ReferenceEquals(_moving, pin)) EndMove();

        // 空了的分组自己消失（默认组永远在）
        for (int i = GroupList.Count - 1; i >= 1; i--)
            if (!Pins.Any(p => PinGroups.Same(p.Group, GroupList[i])))
                GroupList.RemoveAt(i);

        // 正看着的那个组被关空了：回到「显示全部」，剩下的贴图露出来
        if (ActiveGroup != null && PinGroups.Find(ActiveGroup, GroupList) == null)
        {
            ActiveGroup = null;
            foreach (var other in Pins)
                if (!other.IsVisible)
                {
                    other.Show();
                    other.RaiseToTop();
                }
        }
    }
}
