using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Core.Running;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「动作运行时」（通用页）：档位 + 两个上限。见 <c>docs/RUN_INTEROP.md</c> §2.3。</summary>
public partial class ConfigHelperWindow
{
    /// <summary>把配置里的档位 / 上限回填到控件（回填期间不触发保存）。只读 settings 域。</summary>
    private void PopulateActionRun()
    {
        _loadingActionRun = true;

        if (ActionRunMaxCombo.Items.Count == 0)
        {
            for (int i = 1; i <= RunLimits.MaxConcurrentCeiling; i++)
                ActionRunMaxCombo.Items.Add(i.ToString());
            for (int i = 0; i <= RunLimits.MaxQueuedCeiling; i++)
                ActionRunQueueCombo.Items.Add(i.ToString());
        }

        ActionRunConfig run = (SettingsStore.Read(ConfigPath).ActionRun ?? new ActionRunFile())
            .ToConfig();

        ActionRunModeCombo.SelectedIndex = (int)run.Mode;
        ActionRunMaxCombo.SelectedIndex = run.MaxConcurrent - 1;
        ActionRunQueueCombo.SelectedIndex = run.MaxQueued;
        ApplyActionRunEnabled(run.Mode);

        _loadingActionRun = false;
    }

    private void ActionRun_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingActionRun || _initializing) return;
        if (ActionRunModeCombo.SelectedIndex < 0) return;

        var mode = (ActionRunMode)ActionRunModeCombo.SelectedIndex;
        int max = ActionRunMaxCombo.SelectedIndex >= 0
            ? ActionRunMaxCombo.SelectedIndex + 1
            : RunLimits.DefaultMaxConcurrent;
        int queue = ActionRunQueueCombo.SelectedIndex >= 0
            ? ActionRunQueueCombo.SelectedIndex
            : RunLimits.DefaultMaxQueued;

        ActionRunConfig config = new ActionRunConfig(mode, max, queue).Normalized();

        // 只写 settings 域；运行期缓存立刻换一份（钩子读这份，宿主 / 执行器各自刷新）。
        SettingsStore.Modify(ConfigPath, s => s.ActionRun = ActionRunFile.From(config));
        AppState.ActionRun = config;

        ApplyActionRunEnabled(mode);
    }

    /// <summary>两个数字只在「并发运行」档可用（其余档按 1 / 0 生效，见 <see cref="ActionRunConfig"/>）。</summary>
    private void ApplyActionRunEnabled(ActionRunMode mode)
    {
        bool concurrent = mode == ActionRunMode.concurrent;
        ActionRunMaxCombo.IsEnabled = concurrent;
        ActionRunQueueCombo.IsEnabled = concurrent;
    }
}
