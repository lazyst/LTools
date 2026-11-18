using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 缩略图条（照上游 <c>ImageStitchingThumbnail</c> 那一排）：一格一张，拖到另一格上 =
/// **交换**（不是排序）；右键不必开菜单，悬停就有替换 / 删除两个小钮。
/// </summary>
/// <remarks>
/// 幽灵跟随鼠标、命中判定都在窗口这一层做——缩略图卡片只抛手势事件，
/// 覆盖整窗的 <see cref="DragLayer"/> 才拖得出去。
/// </remarks>
public partial class ImageCollageWindow
{
    /// <summary>正在拖的缩略图（0 = 没在拖）。Esc 取消时按它找到卡片收回捕获。</summary>
    private long _dragSource;

    private FrameworkElement? _dragGhost;

    /// <summary>按文档顺序对齐缩略图（换了顺序 / 增删就重建；只改内容就原地更新）。</summary>
    private void SyncThumbnails()
    {
        var items = _doc.Items;
        bool rebuild = _cards.Count != items.Count;
        if (!rebuild)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (_cards[i].ItemId == items[i].Id) continue;
                rebuild = true;
                break;
            }
        }

        if (rebuild)
        {
            CancelThumbnailDrag();
            ThumbHost.Children.Clear();
            _cards.Clear();

            foreach (var item in items)
            {
                var card = new CollageThumbnail(item.Id, this);
                card.SelectRequested += id => Preview.CenterItem(id);
                card.ReplaceRequested += PickReplacement;
                card.RemoveRequested += id => _doc.Remove(id);
                card.DragStarted += OnThumbnailDragStarted;
                card.DragMoved += OnThumbnailDragMoved;
                card.DragEnded += OnThumbnailDragEnded;
                _cards.Add(card);
                ThumbHost.Children.Add(card);
            }
        }

        for (int i = 0; i < items.Count && i < _cards.Count; i++)
            _cards[i].Update(items[i], i + 1, _doc.SelectedId == items[i].Id);
    }

    /// <summary>缩略图的「替换图像」：只选一张，换掉这一张的像素（保留位置 / 顺序）。</summary>
    private void PickReplacement(long id)
    {
        if (_loading || _exporting || _pdfExporting || _closing) return;
        if (_doc.Find(id) == null) return;

        // 按钮点击还在处理中，等这一拍过去再弹文件框（避免捕获 / 焦点打架）。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_loading || _exporting || _pdfExporting || _closing) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "替换图像",
                Filter = ImageFilter(),
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true) return;
            ReplaceFile(id, dialog.FileName);
        }));
    }

    // ============ 拖拽交换 ============

    private void OnThumbnailDragStarted(long id)
    {
        if (_loading || _exporting || _pdfExporting || _closing) return;

        var item = _doc.Find(id);
        if (item == null) return;

        _dragSource = id;
        var body = new Image
        {
            Source = item.Preview,
            Stretch = Stretch.Uniform,
            Width = 80,
            Height = 64,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(body, BitmapScalingMode.HighQuality);

        _dragGhost = DragFx.CreateCard(body, 80, 64);
        _dragGhost.Opacity = 0.92;
        DragLayer.Children.Add(_dragGhost);
    }

    private void OnThumbnailDragMoved(long id, Point screen)
    {
        if (_dragSource == 0 || _dragGhost == null) return;

        Point local = DragLayer.PointFromScreen(screen);
        Canvas.SetLeft(_dragGhost, local.X - _dragGhost.Width / 2);
        Canvas.SetTop(_dragGhost, local.Y - _dragGhost.Height / 2);

        long target = ThumbnailTargetAt(screen);
        foreach (var card in _cards) card.IsTarget = target != 0 && card.ItemId == target;
    }

    private void OnThumbnailDragEnded(long id, Point screen)
    {
        long source = _dragSource;
        long target = source == 0 ? 0 : ThumbnailTargetAt(screen);
        EndThumbnailDrag();

        if (source == 0 || target == 0) return;
        _doc.Swap(source, target);
    }

    /// <summary>Esc / 重排 / 关窗时把拖动收干净（先收捕获，再做别的）。</summary>
    private void CancelThumbnailDrag()
    {
        if (_dragSource != 0)
        {
            foreach (var card in _cards)
            {
                if (card.ItemId == _dragSource) card.CancelDrag();
            }
        }

        EndThumbnailDrag();
    }

    private void EndThumbnailDrag()
    {
        foreach (var card in _cards) card.IsTarget = false;

        if (_dragGhost != null)
        {
            DragLayer.Children.Remove(_dragGhost);
            _dragGhost = null;
        }

        _dragSource = 0;
    }

    private long ThumbnailTargetAt(Point screen)
    {
        foreach (var card in _cards)
        {
            if (card.ItemId == _dragSource) continue;
            if (card.ContainsScreen(screen)) return card.ItemId;
        }

        return 0;
    }
}
