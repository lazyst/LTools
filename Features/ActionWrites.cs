using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 动作写入的**唯一 UI 入口**：新建 / 覆盖 / 删除 / 复制。
/// </summary>
/// <remarks>
/// <para>
/// 内容属主是执行器。宿主里的面板与编辑器只表达意图（<see cref="ActionEditBridge"/>），
/// 执行器改内存、落盘（<c>action.saveAll</c> → 宿主写目录）并回一份权威副本；执行器自己的
/// 设置页 / 动作池 / 面板走本地 <see cref="ConfigStore"/>。两条分支只在这里分，别在调用点各判一次
/// （同 <c>ProgramRunner</c>）。
/// </para>
/// <para>
/// **别在 UI 代码里直接调** <c>ConfigStore.AddAction</c> / <c>UpdateAction</c> / <c>RemoveAction</c> /
/// <c>DuplicateAction</c>：宿主里它们是只读的（<c>Save</c> 会被拒绝），改了也落不到执行器那份清单。
/// </para>
/// <para>
/// 宿主拿到答复后把动作**镜像**进自己的 <see cref="ActionRegistry"/>（执行器可能补了 Id / 默认图标），
/// 紧接着的界面刷新才看得到它。删除不镜像引用清理——面板 / 快捷键 / 触发 / 悬浮的回收只在执行器那份
/// 内存里做，宿主靠执行器推 <c>actions.changed</c> 整份重读。
/// </para>
/// </remarks>
internal static class ActionWrites
{
    /// <summary>新建一个动作（Id 为空时执行器补），返回是否成功。</summary>
    public static bool Add(ActionDto dto, out string? error)
    {
        error = null;

        if (!AppRole.IsUiHost)
        {
            if (!ConfigStore.AddAction(dto))
            {
                error = "动作名不能为空";
                return false;
            }
            ConfigStore.Save();
            return true;
        }

        if (!ActionEditBridge.Add(dto, out ActionDto? saved, out error)) return false;

        // 执行器补过的 Id / 默认图标要带回本地对象：调用方还要拿 Id 挂进面板。
        if (saved != null)
        {
            dto.Id = saved.Id;
            dto.Icon = saved.Icon;
            ActionRegistry.Register(dto);
        }
        return true;
    }

    /// <summary>覆盖同 Id 的动作（<c>dto.Id</c> 必填）。</summary>
    public static bool Update(ActionDto dto, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(dto.Id))
        {
            error = "缺少动作 Id";
            return false;
        }

        if (!AppRole.IsUiHost)
        {
            ConfigStore.UpdateAction(dto);
            ConfigStore.Save();
            return true;
        }

        if (!ActionEditBridge.Update(dto, out error)) return false;
        ActionRegistry.Register(dto);
        return true;
    }

    /// <summary>删除动作（引用清理由执行器做，宿主随后整份重读）。</summary>
    public static bool Delete(string id, out string? error)
    {
        error = null;

        if (!AppRole.IsUiHost)
        {
            if (!ConfigStore.RemoveAction(id))
            {
                error = "动作本来就不在了";
                return false;
            }
            return true;
        }

        if (!ActionEditBridge.Delete(id, out error)) return false;
        ActionRegistry.Remove(id);
        return true;
    }

    /// <summary>复制一份动作（新 Id + 「副本」命名 + 模板图一起走）；失败返回 null。</summary>
    public static ActionDto? Duplicate(string id, out string? error)
    {
        error = null;

        if (!AppRole.IsUiHost)
        {
            ActionDto? copy = ConfigStore.DuplicateAction(id);
            if (copy == null)
            {
                error = "原动作已经不存在了";
                return null;
            }
            ConfigStore.Save();
            return copy;
        }

        ActionDto? remote = ActionEditBridge.Duplicate(id, out error);
        if (remote == null)
        {
            error ??= "原动作已经不存在了";
            return null;
        }

        ActionRegistry.Register(remote);
        return remote;
    }
}
