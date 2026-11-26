using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「重新加载与动作表格的行模型」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 重新加载 ============
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        ConfigStore.Reload();
        MacroSettings.Load(ConfigStore.LoadConfig());
        RefreshCreatedPages();
        ShowStatus("已重新加载", ok: true);
    }

    /// <summary>
    /// 动作级操作完成后要跟着变的东西：热键表「动作」列是取名字的快照，让下次切到那页重读一次。
    /// </summary>
    /// <remarks>
    /// 动作的落盘已经在 <see cref="ActionWrites"/> 里做完了（执行器那份清单 + 宿主写目录），
    /// 这里**不再**调 <c>ConfigStore.Save()</c>：宿主里它是被拒绝的（只读），执行器里是白写一遍。
    /// </remarks>
    private void ActionListChanged()
    {
        if (_pageHotkeys != null) _pageHotkeys.Invalidate();
        StatusText.Text = "已保存";
        StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void ShowStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
    }

    // —— 数据模型 ——

    /// <summary>动作列表展示模型。</summary>
    public sealed class ActionRow
    {
        public ActionRow(ActionDto a, string references = "未使用")
        {
            Id = a.Id;
            Name = a.Name;
            TypeLabel = MacroAction.MacroIdOf(a) != null ? "宏"
                : SimpleActions.KindOf(a) != null ? "简单动作" : "组合动作";
            Glyph = IconCatalog.GetGlyph(a.Icon);
            Summary = ProgramSummary(a) + ParamSuffix(a);
            References = references;
        }
        public string Id { get; }
        public string Name { get; }
        public string References { get; }
        public string TypeLabel { get; }
        public string Glyph { get; }
        public string Summary { get; }

        /// <summary>
        /// 「要填参数」的标记：面板 / 悬浮的悬停提示已经写了（<see cref="BindingRef.Detail"/>），
        /// 动作管理这一行也得看得出——否则只有打开编辑器变量页才知道。
        /// </summary>
        private static string ParamSuffix(ActionDto a)
        {
            int count = ActionParams.InputsOf(a.Program).Count;
            return count == 0 ? "" : $" · 参数 {count}";
        }

        /// <summary>动作体：简单动作显示预设名（打开文件…），宏动作显示时长与事件数，其余显示步骤总数。</summary>
        private static string ProgramSummary(ActionDto a)
        {
            if (MacroAction.SummaryOf(a) is { } macro) return macro;
            if (SimpleActions.KindOf(a) is { } simple) return simple.Name;
            int count = a.Program?.Steps.Sum(s => s.SelfAndDescendants().Count()) ?? 0;
            return count == 0 ? "空动作" : $"{count} 步";
        }
    }

    /// <summary>终端路径表的一行：覆盖值 / 实际路径变了要通知界面刷新。</summary>
    private sealed class PathRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _override;
        private string _path = "";

        public PathRow(string key, string label)
        {
            Key = key;
            Label = label;
            _override = TerminalLauncher.GetOverride(key) ?? "";
        }

        public string Key { get; }

        public string Label { get; }

        /// <summary>配置里的覆盖（界面直接编辑它；空 = 自动探测）。</summary>
        public string Override
        {
            get => _override;
            set
            {
                if (_override == value) return;
                _override = value;
                Raise(nameof(Override));
            }
        }

        /// <summary>当前生效的路径：覆盖优先，否则自动探测。</summary>
        public string Path
        {
            get => _path;
            private set
            {
                if (_path == value) return;
                _path = value;
                Raise(nameof(Path));
            }
        }

        public void Refresh()
        {
            string? live = TerminalLauncher.Resolve(Key);
            string? over = TerminalLauncher.GetOverride(Key);
            bool overBroken = over != null && !string.Equals(over, live, StringComparison.OrdinalIgnoreCase);

            Path = live == null
                ? (overBroken ? "（未检测到；覆盖的路径不存在）" : "（未检测到）")
                : overBroken ? $"{live}（覆盖的路径不存在，已回退）" : live;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
