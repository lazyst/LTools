using System;
using System.Collections.Generic;

namespace LTools.Core.Actions;

/// <summary>
/// 枚举参数值的中文显示名。
/// </summary>
/// <remarks>
/// <para>
/// 参数里存的一律是英文键（<c>trim</c> / <c>foreground</c> / <c>read</c> …）——配置、
/// 单元实现都按它工作。界面只负责把它翻译给用户看：
/// 参数对话框的下拉、步骤行摘要都走这里，别在各处再写一份 switch。
/// </para>
/// <para>
/// 同一个键在不同单元里含义不同时（比如 <c>add</c> 在列表里是「追加一项」、在计算里是「加法」），
/// 由该参数自己的 <see cref="InputSpec.Labels"/> 覆盖，不要为了迁就一个单元去改全局表。
/// </para>
/// </remarks>
internal static class OptionLabels
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 运行命令：终端 ——
        ["direct"] = "直接运行",
        ["pwsh7"] = "PowerShell 7",
        ["pwsh5"] = "Windows PowerShell",
        ["cmd"] = "命令提示符",
        ["gitbash"] = "Git Bash",
        ["wslbash"] = "WSL Bash",
        ["wt"] = "Windows 终端",

        // —— 进程 ——
        ["exists"] = "是否存在",
        ["list"] = "列出",
        ["kill"] = "结束进程",

        // —— 列表 / 文本：共有 ——
        ["count"] = "统计数量",
        ["get"] = "取指定项",
        ["join"] = "拼成文本",
        ["split"] = "拆成列表",
        ["indexOf"] = "查找位置",
        ["contains"] = "包含",
        ["sort"] = "排序",
        ["unique"] = "去重",
        ["reverse"] = "反转",
        ["add"] = "追加一项",
        ["removeAt"] = "删除指定项",

        // —— 文本处理 ——
        ["trim"] = "去掉首尾空白",
        ["upper"] = "转大写",
        ["lower"] = "转小写",
        ["length"] = "取长度",
        ["substring"] = "截取",
        ["replace"] = "替换",
        ["startsWith"] = "开头是",
        ["endsWith"] = "结尾是",

        // —— 打开 / 选择 / 文件操作 ——
        ["file"] = "文件",
        ["folder"] = "文件夹",
        ["url"] = "网址",
        ["openFile"] = "要打开的文件",
        ["saveFile"] = "保存位置",
        ["copy"] = "复制",
        ["move"] = "移动",
        ["delete"] = "删除",
        ["mkdir"] = "新建文件夹",
        ["rename"] = "重命名",
        ["show"] = "在资源管理器中定位",
        ["open"] = "用默认程序打开",

        // —— 列出文件夹 ——
        ["files"] = "只文件",
        ["folders"] = "只文件夹",
        ["both"] = "文件和文件夹",

        // —— 编码 ——
        ["utf8"] = "UTF-8",
        ["utf8bom"] = "UTF-8（带 BOM）",
        ["unicode"] = "UTF-16",
        ["gbk"] = "GBK（中文）",
        ["auto"] = "自动",

        // —— 鼠标 / 点击 ——
        ["move"] = "移动到",
        ["click"] = "单击",
        ["doubleClick"] = "双击",
        ["rightClick"] = "右键单击",
        ["middleClick"] = "中键单击",
        ["wheelUp"] = "向上滚动",
        ["wheelDown"] = "向下滚动",

        // —— 屏幕区域 ——
        ["interactive"] = "手动框选",
        ["foreground"] = "前台窗口",
        ["screen"] = "整个屏幕",
        ["rect"] = "固定区域",

        // —— 图片来源（文字识别）——
        ["image"] = "图片",

        // —— 识别引擎 ——
        ["windows"] = "Windows 自带",
        ["v6tiny"] = "PP-OCRv6 快速",
        ["v6small"] = "PP-OCRv6 高精度",

        // —— 图片格式（image.transform）——
        ["png"] = "PNG（保留透明）",
        ["jpg"] = "JPG（有损、体积小）",
        ["bmp"] = "BMP（无压缩）",
        ["webp"] = "WebP（有损、更小）",

        // —— 剪贴板 / 发送文本 ——
        ["read"] = "读取剪贴板",
        ["write"] = "写入剪贴板",
        ["type"] = "逐字输入",
        ["paste"] = "粘贴",

        // —— 剪贴板格式（text.clipboard）——
        ["text"] = "纯文本",
        ["html"] = "HTML",
        ["rtf"] = "RTF",

        // —— 系统文件夹（file.folderPath）——
        ["desktop"] = "桌面",
        ["downloads"] = "下载",
        ["documents"] = "文档",
        ["pictures"] = "图片",
        ["music"] = "音乐",
        ["videos"] = "视频",
        ["temp"] = "临时目录",
        ["appdata"] = "漫游数据（AppData）",
        ["localAppData"] = "本地数据（LocalAppData）",
        ["programFiles"] = "程序目录",
        ["programFilesX86"] = "程序目录（32 位）",
        ["startup"] = "启动目录",
        ["userProfile"] = "用户目录",
        ["dataRoot"] = "LTools 数据目录",

        // —— 提示弹窗（ui.toast）——
        ["default"] = "跟随设置",
        ["bottomRight"] = "右下",
        ["bottomLeft"] = "左下",
        ["bottomCenter"] = "底部居中",
        ["topRight"] = "右上",
        ["topLeft"] = "左上",
        ["topCenter"] = "顶部居中",

        // —— 窗口操作 ——
        ["activate"] = "切换到前台",
        ["maximize"] = "最大化",
        ["minimize"] = "最小化",
        ["restore"] = "还原",
        ["resize"] = "调整到指定大小",

        // —— 条件比较（flow.if / flow.while）——
        ["equals"] = "等于",
        ["notEquals"] = "不等于",
        ["notContains"] = "不包含",
        ["regex"] = "匹配正则",
        ["isEmpty"] = "为空",
        ["isNotEmpty"] = "不为空",
        ["greater"] = "大于",
        ["less"] = "小于",
        ["greaterOrEqual"] = "大于等于",
        ["lessOrEqual"] = "小于等于",
        ["isTrue"] = "为真",
        ["isFalse"] = "为假",
        ["found"] = "已找到",
        ["notFound"] = "未找到",
        ["active"] = "已激活",
        ["notActive"] = "未激活",
    };

    /// <summary>键对应的中文名；没有登记就原样返回（不吞掉未知值）。</summary>
    public static string Of(string? value)
    {
        string key = (value ?? "").Trim();
        if (key.Length == 0) return "";
        return Map.TryGetValue(key, out string? label) ? label : key;
    }

    /// <summary>按参数的私有覆盖优先取中文名。</summary>
    public static string For(InputSpec input, string? value)
    {
        string key = (value ?? "").Trim();
        if (key.Length == 0) return "";

        if (input.Labels != null && input.Labels.TryGetValue(key, out string? custom))
            return custom;

        return Of(key);
    }
}
