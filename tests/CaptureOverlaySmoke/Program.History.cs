using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 采集历史页（P7-f，照上游 <c>screenshothistorypagewidget</c>）：页头计数 / 来源多选 /
/// 日期范围 / 关键词 / 分页 / 勾选选择栏 / 批量贴回 / 卡片更多菜单 / 查看大图 / 删除与清空。
/// </summary>
/// <remarks>
/// 全程用真的 <c>CaptureCatalog</c>（真写 <c>captures/</c>）与真窗口：造 22 条记录（16 复制 +
/// 1 另存为 + 1 贴图 + 1 当前显示器 + 1 前台窗口 + 1 识别 + 1 录屏），然后一条条按用户路径点。
/// 段首把真实 <c>captures/</c> 改名让开、段尾原样搬回来；每页条数落盘指向临时目录，
/// 不碰仓库里真正的 <c>configs/ui.json</c>。
/// </remarks>
internal static partial class Program
{
    private static void HistoryPageSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;
        var windowType = asm.GetType("LTools.Views.CapturePro.CaptureHistoryWindow")!;
        var viewerType = asm.GetType("LTools.Views.CapturePro.CaptureViewerWindow")!;
        var managerType = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var uiStoreType = asm.GetType("LTools.Config.UiStore")!;

        // ---- 前置：让开真实 captures/，每页条数写临时目录 ----
        string capturesRoot = (string)catalogType.GetProperty("Root")!.GetValue(null)!;
        string backup = capturesRoot + ".smoke-backup";
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        if (Directory.Exists(capturesRoot)) Directory.Move(capturesRoot, backup);

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "history-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        configPathSet.Invoke(null, new object[] { configPath });

        Window? window = null;
        try
        {
            catalogType.GetMethod("Clear")!.Invoke(null, null);

            var pixels = new byte[400 * 300 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x30;       // B
                pixels[i + 1] = 0x60;   // G
                pixels[i + 2] = 0xE0;   // R
                pixels[i + 3] = 0xFF;   // A
            }
            var image = BitmapSource.Create(400, 300, 96, 96, PixelFormats.Bgra32, null, pixels, 400 * 4);
            image.Freeze();

            object Make(string kind, string source)
            {
                var entry = catalogType.GetMethod("Create")!
                    .Invoke(null, new object?[] { image.PixelWidth, image.PixelHeight, kind, source, null, null, null, null })!;
                catalogType.GetMethod("SaveAssets")!.Invoke(null, new object[] { entry, image });
                return entry;
            }

            // 16 复制 + 1 另存为 + 1 贴图 + 1 当前显示器 + 1 前台窗口 = 20 张图
            for (int i = 0; i < 16; i++) Make("shot", "复制");
            Make("shot", "另存为");
            Make("shot", "贴图");
            Make("shot", "当前显示器");
            Make("shot", "前台窗口");

            // 识别条目：文字回填后才算 done
            var recognize = Make("recognize", "识别");
            string recognizeId = (string)recognize.GetType().GetProperty("Id")!.GetValue(recognize)!;
            catalogType.GetMethod("SetText")!
                .Invoke(null, new object[] { recognizeId, "第一行文字\n第二行" });

            // 录屏：先放一个假成品文件，再让目录搬进 captures/records
            string tempVideo = Path.Combine(_outDir, "history-record.mp4");
            File.WriteAllBytes(tempVideo, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
            var record = catalogType.GetMethod("CreateRecord")!.Invoke(null, new object[]
            {
                image.PixelWidth, image.PixelHeight, "录屏", "mp4", 65_000, tempVideo, image,
            });
            Check(record != null, "录屏成品搬进 captures/records 并记一条");

            // ---- 打开：页头计数 / 第一页 ----
            window = (Window)Activator.CreateInstance(windowType)!;
            window.Show();
            Pump(window);

            InvokeWith(window, "SelectPageSize", 20);
            InvokeWith(window, "Reload");
            Pump(window);

            var cards = (ItemsControl)Field(window, "CardList");
            var countText = (TextBlock)Field(window, "CountText");
            var rangeText = (TextBlock)Field(window, "PageRangeText");
            var emptyPanel = (StackPanel)Field(window, "EmptyPanel");
            var pageHost = (StackPanel)Field(window, "PageHost");
            var prevBtn = (Button)Field(window, "PrevBtn");
            var nextBtn = (Button)Field(window, "NextBtn");
            var selectionBar = (Border)Field(window, "SelectionBar");
            var selectionText = (TextBlock)Field(window, "SelectionText");
            var selectPageBtn = (Button)Field(window, "SelectPageBtn");
            var deselectBtn = (Button)Field(window, "DeselectBtn");
            var batchBtn = (Button)Field(window, "BatchBtn");
            var deleteSelectedBtn = (Button)Field(window, "DeleteSelectedBtn");

            Check(cards.Items.Count == 20, $"第一页 20 条（实际 {cards.Items.Count}）");
            Check(countText.Text.Contains("共 22 条"), $"页头报总数（{countText.Text}）");
            Check(countText.Text.Contains("200") && countText.Text.Contains("30"),
                "页头写明保留策略（200 条 / 30 天）");
            Check(rangeText.Text == "1-20 / 共 22 条", $"底栏区间（{rangeText.Text}）");
            Check(emptyPanel.Visibility == Visibility.Collapsed, "有记录时不显示空态");
            Check(!prevBtn.IsEnabled && nextBtn.IsEnabled, "第一页「上一页」置灰、「下一页」可点");
            Check(pageHost.Children.OfType<Button>().Count() == 2, "两页就列两个页码");
            Check(selectionBar.Visibility == Visibility.Collapsed, "没勾选时不显示选择栏");

            // 卡片文案：最新的录屏 / 紧跟的识别
            var recordRow = cards.Items[0];
            Check((bool)recordRow.GetType().GetProperty("IsRecord")!.GetValue(recordRow)!,
                "最新一条是录屏");
            string recordSummary = (string)recordRow.GetType().GetProperty("SummaryText")!.GetValue(recordRow)!;
            Check(recordSummary.Contains("01:05") && recordSummary.Contains("MP4"),
                $"录屏卡片摘要写时长 + 格式（{recordSummary}）");
            Check((bool)recordRow.GetType().GetProperty("HasThumb")!.GetValue(recordRow)!,
                "录屏有封面帧时卡片显示缩略图");

            var recognizeRow = cards.Items[1];
            Check((string)recognizeRow.GetType().GetProperty("SourceLabel")!.GetValue(recognizeRow)! == "识别",
                "识别条目的来源徽标是「识别」");
            Check((string)recognizeRow.GetType().GetProperty("TextExcerpt")!.GetValue(recognizeRow)! ==
                  "第一行文字 第二行", "识别文字压成一行显示在卡片上");

            // 页头 / 翻页的图标按钮（清空 / 刷新 / 打开目录 / 清空日期 / 上一页 / 下一页）：
            // 字形必须落在 TextBlock 的**局部值**上——全局隐式 TextBlock 样式把 FontFamily 钉成 UiFont，
            // 只写在 Button 上会被那条样式盖掉，字形变成空方框（用户报过「右边按钮显示不出来」）。
            var clearBtn = (Button)Field(window, "ClearAllBtn");
            clearBtn.UpdateLayout();
            var clearGlyph = FindChild<TextBlock>(clearBtn);
            Check(clearGlyph != null && clearGlyph.FontFamily.Source == "Segoe MDL2 Assets",
                $"页头图标按钮的字形用 MDL2 字体渲染（{clearGlyph?.FontFamily.Source}）");
            prevBtn.UpdateLayout();
            var prevGlyph = FindChild<TextBlock>(prevBtn);
            Check(prevGlyph != null && prevGlyph.FontFamily.Source == "Segoe MDL2 Assets",
                $"翻页箭头同样是 MDL2 字形（{prevGlyph?.FontFamily.Source}）");

            Save(Render(window), Path.Combine(_outDir, "history-page.png"));

            // ---- 翻页 ----
            InvokeWith(window, "NextPage_Click", null, null);
            Pump(window);
            Check(rangeText.Text == "21-22 / 共 22 条", $"第 2 页区间（{rangeText.Text}）");
            Check(cards.Items.Count == 2, $"第 2 页剩 2 条（实际 {cards.Items.Count}）");
            Check(prevBtn.IsEnabled && !nextBtn.IsEnabled, "最后一页「下一页」置灰");

            InvokeWith(window, "PrevPage_Click", null, null);
            Pump(window);
            Check(rangeText.Text == "1-20 / 共 22 条", "上一页回到第 1 页");

            // ---- 搜索（150ms 防抖后自己刷新） ----
            var search = (TextBox)Field(window, "SearchBox");
            search.Text = "第一行文字";
            Wait(window, 320);
            Check(cards.Items.Count == 1, $"搜索识别文字命中一条（实际 {cards.Items.Count}）");
            Check(countText.Text.Contains("筛选出 1 条"), $"页头写筛选结果（{countText.Text}）");

            search.Text = "录屏";
            Wait(window, 320);
            Check(cards.Items.Count == 1 && rangeText.Text == "1-1 / 共 1 条",
                $"搜索「录屏」按类型命中（{rangeText.Text}）");

            search.Text = "";
            Wait(window, 320);
            Check(cards.Items.Count == 20, "清空搜索恢复第一页 20 条");

            // ---- 来源多选 ----
            var sourceHost = (StackPanel)Field(window, "SourceListHost");
            var sourceChecks = sourceHost.Children.OfType<CheckBox>().ToList();
            Check(sourceChecks.Count == 8, $"来源筛选列 5 个上游来源 + 识别 / 录屏 / 其它（实际 {sourceChecks.Count}）");

            sourceChecks.First(c => (string)c.Tag! == "clipboard").IsChecked = true;
            Pump(window);
            Check(cards.Items.Count == 16, $"只筛剪贴板 → 16 条（实际 {cards.Items.Count}）");
            Check(((TextBlock)Field(window, "SourceFilterText")).Text == "复制到剪贴板",
                "来源按钮写明当前筛选");

            sourceChecks.First(c => (string)c.Tag! == "record").IsChecked = true;
            Pump(window);
            Check(cards.Items.Count == 17, $"再勾上录屏 → 17 条（实际 {cards.Items.Count}）");
            Check(((TextBlock)Field(window, "SourceFilterText")).Text.StartsWith("已选 2 项"),
                "多选时按钮写「已选 2 项」");

            sourceHost.Children.OfType<Button>().First()
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(cards.Items.Count == 20 && ((TextBlock)Field(window, "SourceFilterText")).Text == "全部来源",
                "「全部来源」清掉多选");

            // ---- 日期范围（含两端，筛空走第二档空态） ----
            var dateStart = (DatePicker)Field(window, "DateStart");
            var dateEnd = (DatePicker)Field(window, "DateEnd");
            var clearDate = (Button)Field(window, "ClearDateBtn");
            Check(clearDate.Visibility == Visibility.Collapsed, "没筛日期时不显示清空日期按钮");

            dateStart.SelectedDate = DateTime.Today;
            dateEnd.SelectedDate = DateTime.Today;
            Pump(window);
            Check(cards.Items.Count == 20, "起始日 = 结束日 = 今天：今天的记录都在");

            dateStart.SelectedDate = DateTime.Today.AddDays(-2);
            dateEnd.SelectedDate = DateTime.Today.AddDays(-1);
            Pump(window);
            Check(cards.Items.Count == 0 && emptyPanel.Visibility == Visibility.Visible,
                "日期范围落在昨天以前 → 筛空");
            Check(((TextBlock)Field(window, "EmptyTitle")).Text == "没有符合条件的截图",
                "第二档空态文案是「没有符合条件的截图」");
            Check(clearDate.Visibility == Visibility.Visible, "筛了日期就出现清空日期按钮");

            InvokeWith(window, "ClearDate_Click", null, null);
            Pump(window);
            Check(cards.Items.Count == 20 && clearDate.Visibility == Visibility.Collapsed,
                "清掉日期恢复，清空按钮跟着收起");

            // ---- 勾选：单条 / 全选本页 / 取消选择 ----
            var rowChecks = FindAll<CheckBox>(cards);
            Check(rowChecks.Count == 20, $"每张卡片一个勾选框（实际 {rowChecks.Count}）");

            rowChecks[0].IsChecked = true;
            Pump(window);
            Check(selectionBar.Visibility == Visibility.Visible, "勾一条时选择栏出现");
            Check(selectionText.Text == "已选 1 项", $"选择栏报已选条数（{selectionText.Text}）");
            Check(deleteSelectedBtn.IsEnabled && batchBtn.IsEnabled && deselectBtn.IsEnabled,
                "勾选后删除 / 批量 / 取消选择都可用");

            InvokeWith(window, "SelectPage_Click", null, null);
            Pump(window);
            Check(selectionText.Text == "已选 20 项", $"全选本页（{selectionText.Text}）");
            Check(!selectPageBtn.IsEnabled, "整页都选上后「全选本页」置灰");

            InvokeWith(window, "Deselect_Click", null, null);
            Pump(window);
            Check(selectionBar.Visibility == Visibility.Collapsed, "取消选择后选择栏收起");

            // ---- 批量操作菜单 + 贴到屏幕 ----
            rowChecks = FindAll<CheckBox>(cards);
            rowChecks[2].IsChecked = true;
            rowChecks[3].IsChecked = true;
            Pump(window);
            Check(selectionText.Text == "已选 2 项", "跨行勾选两张截图");

            var batchMenu = (ContextMenu)InvokeValue(window, "BuildBatchMenu");
            var batchItems = batchMenu.Items.OfType<MenuItem>().ToList();
            Check(batchItems.Count == 2, $"批量菜单两项（实际 {batchItems.Count}）");
            Check(((string)batchItems[0].Header!).Contains("拼接图片（2 张）"),
                $"批量拼接写明张数（{batchItems[0].Header}）");
            Check(((string)batchItems[1].Header!).Contains("贴到屏幕（2 张）"),
                $"批量贴回写明张数（{batchItems[1].Header}）");
            Check(batchItems.All(i => i.IsEnabled), "选中的都是图片 → 两项都可用");

            ClickMenuItem(batchMenu, (string)batchItems[1].Header!);
            Pump(window);
            int pinned = (int)managerType.GetProperty("Count")!.GetValue(null)!;
            Check(pinned == 2, $"批量贴回后屏幕上有两张（实际 {pinned}）");
            managerType.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Pump(window);

            InvokeWith(window, "Deselect_Click", null, null);
            Pump(window);

            // ---- 卡片「更多」菜单（上游七项 + 本地那几项） ----
            var all = (System.Collections.IList)catalogType.GetMethod("Load")!.Invoke(null, null)!;
            object? shotEntry = null;
            object? recognizeEntry = null;
            object? recordEntry = null;
            foreach (var item in all)
            {
                string kind = (string)item.GetType().GetProperty("Kind")!.GetValue(item)!;
                string source = (string)item.GetType().GetProperty("Source")!.GetValue(item)!;
                if (kind == "shot" && source == "复制" && shotEntry == null) shotEntry = item;
                if (kind == "recognize") recognizeEntry = item;
                if (kind == "record") recordEntry = item;
            }
            Check(shotEntry != null, "目录里能找到「复制到剪贴板」那条截图");
            Check(recognizeEntry != null && recordEntry != null, "目录里能找到识别 / 录屏条目");

            var shotMenu = (ContextMenu)InvokeValue(window, "BuildCardMenu", shotEntry!);
            var shotHeaders = shotMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
            Check(string.Join("|", shotHeaders) == string.Join("|", new[]
            {
                "贴到屏幕", "拼接图片", "切图…", "图片编辑", "重新编辑截图…", "复制图片路径", "在资源管理器中显示",
                "另存为…", "删除",
            }), $"截图卡片菜单顺序照上游（{string.Join(" / ", shotHeaders)}）");

            var recordMenu = (ContextMenu)InvokeValue(window, "BuildCardMenu", recordEntry!);
            var recordHeaders = recordMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
            Check(!recordMenu.Items.OfType<MenuItem>().First(i => (string)i.Header! == "贴到屏幕").IsEnabled,
                "录屏不能「贴到屏幕」");
            Check(!recordMenu.Items.OfType<MenuItem>().First(i => (string)i.Header! == "拼接图片").IsEnabled,
                "录屏不能「拼接图片」");
            Check(recordHeaders.Contains("播放") && recordHeaders.Contains("裁剪 / 倍速…"),
                "录屏卡片有「播放」「裁剪 / 倍速…」");
            Check(!recordHeaders.Contains("复制识别文字"), "录屏没有「复制识别文字」");

            var recognizeMenu = (ContextMenu)InvokeValue(window, "BuildCardMenu", recognizeEntry!);
            Check(recognizeMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header! == "复制识别文字"),
                "识别条目多一项「复制识别文字」");

            // ---- 查看大图 ----
            var show = viewerType.GetMethod("Show", Stat)!;
            show.Invoke(null, new object?[] { null, shotEntry, 0 });
            Pump(window);

            var viewer = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType().Name == "CaptureViewerWindow");
            Check(viewer != null, "点预览打开查看大图窗口");

            if (viewer != null)
            {
                Check(((Image)Field(viewer, "Picture")).Source != null, "查看器显示成品图");
                Check(((Border)Field(viewer, "TextPanel")).Visibility == Visibility.Collapsed,
                    "截图没有识别文字面板");
                Check(((TextBlock)Field(viewer, "InfoText")).Text.Contains("复制到剪贴板"),
                    "查看器信息行写来源");

                InvokeWith(viewer, "Actual_Click", null, null);
                Pump(viewer);
                Check(((TextBlock)Field(viewer, "ZoomText")).Text == "100%", "点 100% 缩放到 1:1");

                show.Invoke(null, new object?[] { null, recognizeEntry, 0 });
                Pump(viewer);
                Check(((Border)Field(viewer, "TextPanel")).Visibility == Visibility.Visible,
                    "识别条目显示文字面板");
                Check(((TextBox)Field(viewer, "TextBody")).Text.Contains("第一行文字"),
                    "识别文字回填到查看器");

                show.Invoke(null, new object?[] { null, recordEntry, 0 });
                Pump(viewer);
                Check(((Image)Field(viewer, "Picture")).Source == null, "录屏没有静态图");
                Check(((StackPanel)Field(viewer, "NoImagePanel")).Visibility == Visibility.Visible,
                    "录屏显示「没有静态画面」说明");
                Check(((Button)Field(viewer, "NoImagePlayBtn")).Visibility == Visibility.Visible,
                    "录屏给「播放录屏」按钮");
                Check(((TextBlock)Field(viewer, "InfoText")).Text.Contains("MP4"),
                    "查看器信息行写录屏格式");

                viewer.Close();
                Pump(window);
            }

            // ---- 每页条数落 ui.json ----
            var pageSizeBox = (ComboBox)Field(window, "PageSizeBox");
            pageSizeBox.SelectedIndex = 0;   // 10 条
            Pump(window);
            Check(cards.Items.Count == 10, $"切到每页 10 条（实际 {cards.Items.Count}）");
            Check(rangeText.Text == "1-10 / 共 22 条", $"分页跟着变（{rangeText.Text}）");

            object uiFile = uiStoreType.GetMethod("Read", Stat, null, new[] { typeof(string) }, null)!
                .Invoke(null, new object[] { configPath })!;
            Check((int)uiFile.GetType().GetProperty("CaptureHistoryPageSize")!.GetValue(uiFile)! == 10,
                "每页条数写进 ui.json 的 CaptureHistoryPageSize");

            // ---- 删除选中（真确认框） ----
            InvokeWith(window, "SelectPage_Click", null, null);
            Pump(window);

            bool confirmed = false;
            var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
                (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                    if (dialog == null) return;
                    confirmed = true;
                    ((Button)Field(dialog, "OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, window.Dispatcher);
            closer.Start();
            InvokeWith(window, "DeleteSelected_Click", null, null);
            closer.Stop();
            Pump(window);

            Check(confirmed, "删除选中先弹确认框");
            Check(countText.Text.Contains("共 12 条"), $"删掉 10 条后剩 12 条（{countText.Text}）");
            Check(cards.Items.Count == 10, $"第一页重新填满 10 条（实际 {cards.Items.Count}）");
            Check(selectionBar.Visibility == Visibility.Collapsed, "删完勾选清空、选择栏收起");

            // ---- 清空（真确认框）→ 空态 ----
            bool cleared = false;
            var clearer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
                (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                    if (dialog == null) return;
                    cleared = true;
                    ((Button)Field(dialog, "OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, window.Dispatcher);
            clearer.Start();
            InvokeWith(window, "ClearAll_Click", null, null);
            clearer.Stop();
            Pump(window);

            Check(cleared, "清空历史先弹确认框");
            Check(cards.Items.Count == 0, "清空后列表为空");
            Check(emptyPanel.Visibility == Visibility.Visible, "空态回来");
            Check(((TextBlock)Field(window, "EmptyTitle")).Text == "还没有截图历史",
                "第一档空态文案是「还没有截图历史」");
            Check(countText.Text.Contains("共 0 条"), $"页头归零（{countText.Text}）");
            Check(rangeText.Text == "还没有记录", $"底栏写「还没有记录」（{rangeText.Text}）");
            Check(!((Button)Field(window, "ClearAllBtn")).IsEnabled, "没有记录时「清空」置灰");

            Save(Render(window), Path.Combine(_outDir, "history-page-empty.png"));

            window.Close();
            Pump(window);
            window = null;

            var remaining = (System.Collections.ICollection)catalogType.GetMethod("Load")!
                .Invoke(null, null)!;
            Check(remaining.Count == 0, "catalog.json 也清空了");
        }
        finally
        {
            try { window?.Close(); } catch { /* 已经关掉 */ }

            configPathSet.Invoke(null, new object[] { oldConfigPath });

            // 把真实 captures/ 原样搬回来（段里可能已经把它清空 / 重建）
            if (Directory.Exists(capturesRoot)) Directory.Delete(capturesRoot, true);
            if (Directory.Exists(backup)) Directory.Move(backup, capturesRoot);
        }
    }

    /// <summary>抽消息等一小会儿（让 150ms 的搜索防抖 / 计时器真的跑起来）。</summary>
    private static void Wait(Window window, int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, window.Dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        Pump(window);
    }
}
