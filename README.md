# LTools

> ✏️ **v2.2.0 起应用改名 LTools**（原 CapsLock++ / CapsLock-Pro），详见 CHANGELOG
> 📋 **版本历史**：查看 [CHANGELOG.md](CHANGELOG.md) 了解详细更新记录
> 📦 **v2.0.0**：C# / .NET 8 / WPF 重构版。AHK 原版已归档至 [`ahk-legacy`](https://github.com/lazyst/LTools/tree/ahk-legacy) 分支。

## 0. 前言

本项目受 [Capslock+](https://capslox.com/capslock-plus/) 启发，并借鉴了 vim 的很多键位。主要自用于 Win11 24H2，其他系统未测试。

v2.0.0 起由原 AutoHotkey v2 脚本重构为 **C# / .NET 8 / WPF** 桌面程序，功能行为与 AHK 版本一致，性能与可维护性更好。重构细节见 [docs/WPF_MIGRATION_PLAN.md](docs/WPF_MIGRATION_PLAN.md)。

## 1. 构建与运行

### 依赖

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（Windows）
- Windows 10/11（托盘、全局钩子、WPF）

### 构建

```bash
# 开发构建（无清单，调试）
dotnet build

# 发布构建（带 requireAdministrator 清单，双击即提权）
dotnet build -c Release
# 或发布单文件
dotnet publish -c Release -r win-x64 --self-contained false
```

输出位于 `bin/Debug/net10.0-windows/LTools.exe`（或 `Release`）。双击即提权运行（`app.manifest` 已配置 `requireAdministrator`）。

> 全局键盘/鼠标钩子需管理员权限，故发布版带提权清单。开发调试时可用 `-p:NoWin32Manifest=true` 免提权快速启动。

## 2. 基本功能

### 2.1 CapsLock 重映射

- **CapsLock 单击（≤0.3s）**：发送 Esc 键
- **CapsLock 长按（≥0.3s）**：犹豫操作，不触发 Esc

### 2.2 CapsLock 状态管理

- 自动维持 CapsLock 关闭状态
- **Ctrl+CapsLock**：手动切换 CapsLock 状态

### 2.3 工具临时禁用

- **启用 → 禁用：CapsLock+Esc**（无论短按长按）
  - 禁用后所有 CapsLock 增强功能暂时失效，CapsLock 恢复原生大小写切换
- **禁用 → 启用：长按 CapsLock+Esc（≥300ms）**
- 禁用状态下短按 CapsLock（<300ms）：正常切换大小写

### 2.4 控制与调试

- **Ctrl+Alt+I**：显示调试信息

## 3. 实用小功能

### 3.1 文件重命名

- **CapsLock+左键**：重命名文件，无需选中再按快捷键或右键

### 3.2 放大镜

- **CapsLock+Tab**：放大镜
- **Ctrl+Alt+滚轮**：放大缩小

### 3.3 快速搜索

- **CapsLock+Q**：搜索选中的文本内容
  - 网址 → 直接打开
  - 磁盘绝对路径 → 打开该路径
  - 普通文本 → Bing 搜索
  - 无选中 → 不执行

## 4. 文本编辑增强

### 4.1 光标移动

- **CapsLock+A**：向左移动一个单词
- **CapsLock+G**：向右移动一个单词
- **CapsLock+S**：向左移动一个字符
- **CapsLock+F**：向右移动一个字符
- **CapsLock+E**：向上移动一行
- **CapsLock+D**：向下移动一行
- **CapsLock+W**：移动到行首
- **CapsLock+R**：移动到行尾
- **CapsLock+Alt+A**：移动到文件开头
- **CapsLock+Alt+G**：移动到文件末尾

### 4.2 文本选择

- **CapsLock+H**：向左选择一个单词
- **CapsLock+;**：向右选择一个单词
- **CapsLock+J**：向左选择一个字符
- **CapsLock+L**：向右选择一个字符
- **CapsLock+I**：向上选择一行
- **CapsLock+K**：向下选择一行
- **CapsLock+U**：选择到行首
- **CapsLock+O**：选择到行尾
- **CapsLock+Alt+H**：选择到文件开头
- **CapsLock+Alt+;**：选择到文件末尾

### 4.3 删除操作

- **CapsLock+<**：向左删除一个字符
- **CapsLock+>**：向右删除一个字符
- **CapsLock+M**：删除到行首
- **CapsLock+?**：删除到行尾
- **CapsLock+Alt+M**：删除到文件开头
- **CapsLock+Alt+?**：删除到文件末尾
- **CapsLock+Backspace**：删除整行

### 4.4 特殊操作

- **CapsLock+Z**：撤销
- **CapsLock+X**：剪切（独立剪切板）
- **CapsLock+C**：复制（独立剪切板）
- **CapsLock+V**：黏贴（独立剪切板，多格式备份/恢复）
- **CapsLock+B**：任务视图（Win+Tab）
- **CapsLock+Y**：重做
- **CapsLock+Enter**：在当前行末尾插入换行
- **CapsLock+RShift**：在当前行上方插入空行

### 4.5 括号输入快捷键

- **CapsLock+[**：输入 `{`
- **CapsLock+]**：输入 `}`
- **CapsLock+9**：输入 `(`（当第 9 组菜单为空时）
- **CapsLock+0**：输入 `)`（当第 10 组菜单为空时）
- **CapsLock+'**：输入 `'`

> 这些快捷键设计用于减少编程时频繁在 CapsLock 和 Shift 键之间切换

### 4.6 符号定位

- **CapsLock+P**：定位到对应的配对符号位置（再次长按 CapsLock 或 Esc 取消）
  - 英文标点：`()`、`[]`、`{}`、`<>`
  - 中文标点：`「」`、`『』`、`【】`、`《》`、`〈〉`、`（）`、`［］`、`｛｝`、`〔〕`、`〖〗`、`〘〙`、`〚〛`、`""`、`''`、`‹›`、`«»`

## 5. 窗口管理增强

### 5.1 窗口置顶

- **CapsLock+右键**：置顶/取消置顶光标所在窗口

### 5.2 音量调节

- **屏幕底部滚轮**：鼠标在屏幕底部边缘（5px 范围内）时，滚轮调节系统音量
  - 向上增大、向下减小（每次 ±2），无需按 CapsLock
  - 跟随工具全局启用/禁用（CapsLock+Esc）

## 6. 鼠标模式

通过 **CapsLock+空格** 进入鼠标模式，可用字母键完全控制鼠标。

### 6.1 进入/退出

- **CapsLock+空格**：进入/退出鼠标模式
- **Esc**：退出鼠标模式

### 6.2 移动（按住可持续移动，多键同按可斜向）

- **e**：向上　**d**：向下　**s**：向左　**f**：向右

### 6.3 速度调节

- **q**：提高速度（范围 1-20）
- **a**：降低速度（范围 1-20）
- 速度自动保存到配置文件，重启后保持

### 6.4 点击与滚轮

- **w**：左键点击　**r**：右键点击
- **k**：向上滚动　**j**：向下滚动　**h**：向左滚动　**l**：向右滚动

## 7. 速记功能

### 7.1 基本操作

- **CapsLock+N**：打开速记窗口
  - 快速记录笔记、想法或任何文本内容
  - 保存时自动添加时间戳记录创建时间
  - 保存后不关闭窗口，自动加载保存的文件内容进入编辑模式

### 7.2 查看已记速记

- 左列列表显示速记文件（标题、修改时间），支持搜索过滤（标题优先匹配，不命中才扫正文，300ms 防抖）
- **单击**列表项即载入内容到右列编辑区
- **右键**列表项（或 **Ctrl+Del**）删除选中的速记，需确认；删除的是列表条目本身，只有恰好是当前编辑中的那条才会清空编辑区

### 7.3 保存

- 默认保存：有标题（首行）以标题为文件名；无标题以时间戳为文件名
- 保存位置：程序目录下的 `速记/` 文件夹，按分类子目录组织
- **Ctrl+S**：保存当前速记

## 8. 快捷菜单系统

### 8.1 基本操作

- **CapsLock+数字键（1-0）**：显示对应的快捷菜单组
  - 每组可自定义名称和菜单项
  - 为空的菜单组（如 9、0）会发送对应的括号字符 `(` 或 `)`
  - 现代 UI：圆角窗口、阴影、淡入淡出

### 8.2 菜单操作

- 点击按钮执行对应操作
- 按数字键（1-9, 0）执行对应序号操作
- Esc 或"关闭"按钮或点击菜单外区域关闭

### 8.3 菜单项命令

菜单项命令使用**纯字符串格式**，设置中提供终端选择辅助拼接：

- 终端选择：PowerShell 7/5、CMD、Git Bash、WSL Bash 或直接运行
- 保持窗口：自动用 `wt` 包装并添加 `-NoExit`/`/k`
- 命令输入框只填原始命令，完整命令实时预览

### 8.4 帮助面板

- **CapsLock+` **：打开帮助面板（热键速查表）
  - 按分类展示所有热键及功能
  - 再次按 `CapsLock+`` 或 Esc 或点击外部关闭

## 9. 自定义指南

所有配置在程序目录下的 `LTools.json` 文件中。可用设置（**CapsLock+\**）图形化编辑，保存后自动生效。

### 9.1 速记功能配置

- **[noteTargets]**：定义速记的预设保存目标
  - `noteX1 = "关键字"`、`noteX2 = "目标文件路径"`
  - 关键字用于速记最后一行 `==关键字==` 引用

### 9.2 快捷菜单系统配置

- **[MenuGroupsEnable]**：控制 10 个菜单组是否启用，`enableGroupX = true/false`
- **[MenuGroupNum]**：指定实际启用的菜单组数量
- **[MenuGroupCount]**：指定每个菜单组的项目数量，`countX = 数量`
- **[MenuGroupName]**：定义每组标题名称，`nameX = "组名称"`
- **[MenuGroupsXItems]**（X=1-10）：定义每组内具体项目
  - `nameY = "项目显示名称"`
  - `actionY = "执行命令"`（如 `notepad.exe`、`wt pwsh -NoExit -c ipconfig /all`）

### 9.3 设置

**CapsLock+\** 打开设置（内置 WPF 窗口）：

- **重新加载**：从 `LTools.json` 重新载入配置
- **保存配置**：保存修改回 INI 并自动应用
- 速记路径页：编辑速记关键词与保存路径
- 菜单配置页：管理 10 个菜单组及菜单项（组名、启用/禁用、项增删、双击编辑名称与命令）
- 工作目录可通过"选择..."按钮弹出现代文件夹选择器（带快速访问导航栏）选取
