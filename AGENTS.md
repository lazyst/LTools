# AGENTS.md

LTools（原 CapsLock-Pro / CapsLock++）：Windows 桌面工具，把 CapsLock 重映射为 vim 式修饰键。C# / .NET 10 / WPF 单项目单解决方案，**无测试项目**。

## 构建

```bash
# 开发构建（Debug，免提权快速启动）
dotnet build -p:NoWin32Manifest=true

# Release 构建（带 requireAdministrator 清单，全局钩子需管理员）
dotnet build -c Release

# 发布独立部署（内嵌运行时，用户免装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

- 输出：`bin/Debug/net10.0-windows/LTools.exe`
- 全局钩子需管理员权限；Debug 加 `-p:NoWin32Manifest=true` 免提权调试
- 构建必须 **0 错 0 警**
- 无测试项目，不要找 `dotnet test`

## CI

- `build.yml`：push/PR 到 `master` → `dotnet build -c Release`
- `release.yml`：打 `v*` tag → 自包含发布 zip 上传到 GitHub Release

## 架构

单 STA 线程模型：WPF Dispatcher 泵送 Win32 消息，低级钩子回调在主 UI 线程派发（模型不变，勿改线程模型）。

- `App.xaml.cs` — 入口：单实例互斥体 → 托盘 → 装钩子 → 加载配置 → 看门狗
- `Hooks/` — `WH_KEYBOARD_LL` / `WH_MOUSE_LL`，纯 P/Invoke，框架无关
- `Native/` — Win32 调用、InputHelper、NativeClipboard、FolderPicker
- `Core/` — AppState(全局状态)、CapsLockStateMachine、ConfigLocator、TrayService、CrashLog
- `Features/` — 功能模块（TextEditor、MouseMode、MenuSystem、QuickNote 等），非 UI 逻辑
- `Views/` — WPF 窗体（XAML + code-behind）
- `Themes/Modern.xaml` — ResourceDictionary 配色与控件样式
- `Config/` — AppConfig(JSON 模型)、IniFile(仅旧 ini 迁移用)

## 钩子关键约束

修改 `Hooks/` 或在钩子回调路径上调用代码时必须遵守：

- 回调**不得阻塞 >300ms**（否则系统自动卸载钩子）
- 阻塞操作（SendInput / Sleep / 剪贴板）走后台线程
- 忽略 `LLKHF_INJECTED` 事件（防自注入递归）
- 吞掉某键的 keydown 必须同时吞掉配对 keyup（保持事件平衡，见 `AppState.SwallowedVks`）

## 配置

- `LTools.json`（gitignored）— 用户配置，由应用自身读写（设置面板/快捷键），不面向手改；首次保存才生成，升级解压不会覆盖
- `LTools.example.json`（已提交）— 默认模板，随包分发；`AppConfig.Load` 在用户配置缺失时回退读同目录示例（故发布包只带示例、不带实际配置，避免覆盖用户配置）
- `ConfigLocator.FindPath()`：dev（仓库根含 `LTools.csproj`）落仓库根，prod 落 exe 同级。首启自动迁移旧配置：`CapsLock++.json` 同目录改名 `LTools.json`；更旧的 `CapsLock++.ini` 读入后改名 `.migrated` 备份
- 配置格式为 JSON（`MenuGroups[10]`、`TerminalPaths`、`MouseModeSpeed`，见 `Config/AppConfig.cs`），**非旧的 INI 分节格式**——README §9 仍描述 INI schema，已过时，以代码为准

## WPF 约定

- `GlobalUsings.cs` 已把 `Button`/`Key`/`MessageBox` 等同名类型统一到 WPF 命名空间——不要引用 WinForms 类型
- 文件夹选择用 WPF 原生 `Microsoft.Win32.OpenFolderDialog`，不引用 WinForms
- 唯一第三方依赖：`H.NotifyIcon.Wpf`（托盘图标）

## 其他

- `速记/`（速记文件）和 `logs/` 运行时生成，已 gitignore
- `-p:EnableUia=true`：可选启用 UIAutomationClient COM 互操作（UIA/MSAA 功能）
- `app.manifest`：`requireAdministrator` + 声明 Win10/11 兼容
