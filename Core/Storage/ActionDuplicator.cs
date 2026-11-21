using System;
using System.Collections.Generic;
using System.IO;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Storage;

/// <summary>
/// 「复制为副本」：把一个动作克隆成新动作（新 Id、新名字、<c>files/</c> 一并带走）。
/// </summary>
/// <remarks>
/// <para>
/// 只负责造出副本并把素材搬过去，**不**注册进清单、也不写盘——那是 <c>ConfigStore.DuplicateAction</c> 的事。
/// 单独拆出来是为了能在自测里用临时目录验证「素材确实跟着走」。
/// </para>
/// <para>
/// 素材必须复制：不复制的话副本里的 <c>files/x.png</c>（模板图）与脚本全部断链，
/// 副本看着像好的、一跑就报「找不到文件」。内容相同的素材由
/// <see cref="ActionStore.CopyFilesFrom"/> 按 SHA-256 走硬链接，不会真的占两份空间。
/// </para>
/// </remarks>
internal static class ActionDuplicator
{
    /// <summary>克隆一个动作；源不存在返回 null。</summary>
    /// <param name="anchorPath">数据根锚点（正常运行时就是配置路径），素材从 / 往 <c>actions/&lt;id&gt;/files/</c> 搬。</param>
    /// <param name="id">源动作 Id。</param>
    /// <param name="takenNames">现有动作名，用来避开重名（见 <see cref="CopyNaming"/>）。</param>
    public static ActionDto? Duplicate(string anchorPath, string id, IEnumerable<string>? takenNames = null)
    {
        var src = ActionRegistry.FindById(id);
        if (src == null) return null;

        // 用 Clone 深拷贝全部字段，避免新增字段时这里漏抄导致复制出的动作丢配置。
        var copy = src.Clone();
        copy.Id = ActionRegistry.NextId();
        copy.Name = CopyNaming.Next(src.Name, takenNames);
        if (string.IsNullOrEmpty(copy.Icon)) copy.Icon = IconCatalog.Default;

        string source = ActionStore.FilesDir(anchorPath, id);
        if (Directory.Exists(source))
            ActionStore.CopyFilesFrom(anchorPath, copy.Id, source);

        // 宏动作：副本要有自己的那份录制。共用同一个 macroId 时，在任一份上「重新录制」
        // 会把另一份也悄悄改掉（宏就是那个动作的全部内容），所以这里把宏也复制一份。
        if (MacroAction.MacroIdOf(copy) is { } macroId && MacroStore.Load(macroId) is { } macro)
        {
            try
            {
                macro.Id = MacroStore.NewId();
                macro.Name = copy.Name;
                MacroStore.Save(macro);
                copy.Program = MacroAction.ProgramFor(macro.Id);
            }
            catch (Exception ex)
            {
                // 复制宏失败就别把副本挂到一个半成品上：保持共用原宏（行为同「复制引用」），只记一条日志
                AppLog.Warn("ActionDuplicator", $"复制宏 {macroId} 失败，副本继续共用原宏：{ex.Message}");
            }
        }

        return copy;
    }
}
