using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Share;
using LTools.Features;
using LTools.Features.Units.Screen;

namespace LTools.Views;

/// <summary>ShareWindow 的「其它」部分。</summary>
public partial class ShareWindow : Window
{
    // ============ 其它 ============

    private void CopyUrlRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string url } && url.Length > 0)
        {
            Copy(url);
            ToastService.Show("地址已复制：" + url, null, 2200, ToastKind.success);
        }
    }

    /// <summary>点二维码 = 换下一条地址（多网卡时）。</summary>
    private void Qr_Click(object sender, MouseButtonEventArgs e)
    {
        if (UrlList.ItemsSource is not IEnumerable<ShareUrlRow> rows) return;

        int count = rows.Count();
        if (count < 2) return;

        _qrIndex = (_qrIndex + 1) % count;
        Refresh();
    }

    /// <summary>点某条地址 = 用它画二维码。</summary>
    private void UrlRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int index }) return;
        if (index == _qrIndex) return;

        _qrIndex = index;
        Refresh();
    }

    /// <summary>打开目录本身（进入共享目录；`/select` 只会停在它的上一级并选中它）。</summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = _store.Root;
            if (!Directory.Exists(path))
            {
                ConfirmDialog.Info(this, "目录不存在", "已经找不到这个目录了：" + path);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "打不开位置", ex.Message);
        }
    }

}
