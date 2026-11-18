using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗的 LaTeX 页：从当前识别文字里整理出公式源码（本地只给源码，不做渲染）。
/// </summary>
/// <remarks>
/// <para>
/// 上游 LaTeX 模式 = 视觉模型吐源码 + 内置 MicroTeX 渲染预览（<c>screenshotlatexrenderer</c>）。
/// 本地没有 MicroTeX 等价引擎，也没有公式视觉模型，所以**只做源码**：
/// 按 TeX 特征挑公式行（<see cref="LatexSource"/>），一行都不像公式时退回整段识别文字并在页脚说明。
/// 这是明确的平台差异，写进 <c>docs/CAPTURE_PARITY.md</c> §5；等有可配端点再补渲染。
/// </para>
/// <para>
/// 源码是只读文本框：能划选、能只复制挑中的几行（与「辅助工具」的卡片同一口径），
/// 复制按钮在没有选区时复制整份。
/// </para>
/// </remarks>
public partial class CaptureResultWindow
{
    /// <summary>当前页脚说明（冒烟读它断言「挑到公式 / 退回原文」两种文案）。</summary>
    internal string LatexInfoText => LatexInfo.Text;

    /// <summary>按**当前的识别文字**重建源码页（文字页改过字，切过来就是改后的）。</summary>
    internal void RefreshLatex()
    {
        if (LatexView == null || LatexInfo == null) return;

        IReadOnlyList<string> lines = _model?.Lines ?? _layer.Select(line => line.Text).ToList();
        (string text, bool formulaOnly, int count) = LatexSource.Build(lines);

        if (LatexView.Text != text) LatexView.Text = text;
        LatexInfo.Text = LatexSource.Describe(formulaOnly, count);
    }

    /// <summary>只复制选区（没选就整份），与上游 <c>mimeDataForCopy</c> 的 LaTeX 分支一致。</summary>
    private void CopyLatex()
    {
        string text = LatexView.SelectionLength > 0 ? LatexView.SelectedText : LatexView.Text;
        Copy(text, "LaTeX 源码");
    }

    private void LatexView_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只刷新底栏（选区 / 探针会改它），别反过来再跑一次 UpdateTabs
        if (_tab == "latex") RefreshStatus();
    }

    private void LatexView_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_tab == "latex") RefreshStatus();
    }
}
