# LTools UI 层迁移计划：WinForms → WPF

> **分支**：`csharp-refactor`　**目标框架**：.NET 8 (`net8.0-windows`)　**当前**：`UseWindowsForms=true`
>
> 状态：**Phase 0~6 完成（整体重写落地）** — 决策已锁定：托盘 `H.NotifyIcon.Wpf`；整体重写；允许必要的轻量库。提交：`c3404bf`（WPF 整体重写）、`53df8cf`（关闭重入崩溃修复）。全部 6 个窗口经 RenderTargetBitmap 截图核验忠实还原原版浅色主题。Phase 7（NativeAOT/收尾）待后续。

---

## 1. 目标与范围

### 1.1 目标
把 C# 重构版的 **UI 层** 从 WinForms（`System.Windows.Forms`）迁移到 WPF（`System.Windows.Controls`/XAML），解决当前 WinForms 原生控件在高 DPI 下文字裁剪、布局溢出、美化困难等问题，获得矢量缩放、正交样式系统、现代化外观。

### 1.2 在范围内（迁移到 WPF）
- 全部 6 个窗体（见 §2.1）→ WPF `Window` + XAML
- `UiTheme`（配色/按钮样式）→ WPF `ResourceDictionary`（`Style`/`ControlTemplate`）
- 主消息循环：`System.Windows.Application`（WPF Dispatcher）替代 `System.Windows.Forms.Application.Run(ApplicationContext)`
- `System.Windows.Forms.Timer` 看门狗 → `DispatcherTimer`
- 气球提示（`NotifyIcon.ShowBalloonTip`）→ WPF 等价物（见 §3.1）

### 1.3 不在范围内（保持不变）
- 低级键盘/鼠标钩子（`Hooks/KeyboardHook.cs`、`Hooks/MouseHook.cs`）—— 纯 P/Invoke，框架无关
- 原生调用（`Native/Win32.cs`、`Native/InputHelper.cs`、`NativeClipboard`）
- 核心状态机（`Core/AppState.cs`、`Core/CapsLockStateMachine.cs`）
- 功能逻辑（`Features/*` 中非 UI 部分：`CommandString`、`IniFile`、`ClipboardIndependent`、`TextEditor`、`SymbolJump`、`MouseMode`、`MiscKeys`、`QuickSearch`、`Volume`、`WindowPin`）
- INI 读写、菜单数据模型、速记保存逻辑
- `app.manifest`（`requireAdministrator`）、单实例互斥体

> **关键**：钩子回调与功能逻辑与 UI 解耦良好，迁移主要触及「窗体类 + 主循环 + 托盘」三层。

---

## 2. 现状勘察

### 2.1 UI 面清单（6 个 Form 子类）

| 窗体 | 文件 | 行数(含逻辑) | 触发方式 | 模态? | 备注 |
|---|---|---|---|---|---|
| `QuickNoteForm` | `Features/QuickNote.cs` | 634 | `QuickNote.Toggle()`（CapsLock+N / 托盘） | 非模态 | 编辑/查看双模式；保存写文件；最大 |
| `ConfigHelperForm` | `Features/ConfigHelper.cs` | 543 | `ConfigHelper.Toggle()`（CapsLock+\ / 托盘） | 非模态 | 2 标签页；菜单组/项 ListView；速记目标 ListView；INI 加载/保存 |
| `MenuPopup` | `Features/MenuSystem.cs` | 297(共) | `MenuSystem.Show(i)`（CapsLock+1~0） | 非模态 | 鼠标光标处弹出；悬停激活项；序号按钮 |
| `HelpForm` | `Features/HelpPanel.cs` | 256(共) | `HelpPanel.Toggle()`（CapsLock+\` / 托盘双击） | 非模态 | 热键速查表只读文本 |
| `MenuItemEditDialog` | `Features/MenuItemEditDialog.cs` | 101 | `ConfigHelperForm` 内 | **模态** | 名称 + 命令 + 终端选择 + 保持窗口 |
| `InputDialog` | `Features/InputDialog.cs` | ~40 | `ConfigHelperForm`/`MenuSystem` 内 | **模态** | 通用单行输入（替代 AHK InputBox） |

### 2.2 线程模型（迁移关键约束）

```
主线程 (STA) = UI 线程
  ├─ Application.Run(TrayAppContext)   ← WinForms 消息循环（将被 WPF Dispatcher 替代）
  ├─ KeyboardHook.Install()            ← 钩子在本线程安装，回调由消息循环泵送
  └─ MouseHook.Install()
       │
       HookCallback(nCode, wParam, lParam)   ← 在主 UI 线程执行
         ├─ CapsLockStateMachine.TryHandle(...)
         ├─ MouseMode.OnKey(...)
         ├─ HelpPanel.Toggle()        ← 在 UI 线程 Show 窗体
         ├─ MenuSystem.Dispatch(vk)    ← 在 UI 线程 Show MenuPopup
         ├─ TextEditor.TryHandle(vk)  ← 注入/Sleep/剪贴板（按 handoff 走后台线程）
         └─ MiscKeys.TryHandle(vk)    ← 速记/设置/快速搜索
```

**约束**（来自历史 handoff，迁移后必须保持）：
1. 钩子回调 **不得阻塞 >300ms**（否则系统自动卸载钩子）。
2. 注入/Sleep/剪贴板等阻塞操作走后台线程，不占 UI 线程。
3. 钩子忽略 `LLKHF_INJECTED`（防自注入递归）。
4. 注入选中键与复制键之间 `Sleep >= 50ms`；同键连续注入外部应用 `Sleep(20)`。

**迁移影响**：WPF 的 `Application` / `Dispatcher` 同样在主 STA 线程泵送 Win32 消息，低级钩子回调**仍在本线程派发**——所以「钩子回调里直接 `window.Show()`」的模型在 WPF 下成立，无需改线程模型。✅

### 2.3 UI 与逻辑的耦合度评估

| 窗体 | 耦合度 | 可分离性 |
|---|---|---|
| `InputDialog` | 低 | 纯输入，逻辑零；最易迁 |
| `HelpForm` | 低 | 只显示一段只读文本；`BuildHelpText()` 已是静态方法 |
| `MenuItemEditDialog` | 低 | 表单→`MenuEntry` 对象；逻辑薄 |
| `MenuPopup` | 中 | 悬停激活 + 光标定位 + 按键序号；需 WPF 弹出定位 |
| `QuickNoteForm` | 中高 | 编辑/查看双模；保存逻辑（`## title`/`==target==`）混在事件里，但已较独立 |
| `ConfigHelperForm` | 高 | INI 全量加载/保存、ListView 双向同步、命令字符串构造、排序——逻辑重，迁移时需把数据操作抽到 ViewModel/服务 |

> **结论**：`InputDialog`/`HelpForm`/`MenuItemEditDialog` 可直接重写；`MenuPopup`/`QuickNoteForm` 中等；`ConfigHelperForm` 最重，建议顺带把数据层抽成 `ConfigService`（不绑 UI）。

### 2.4 托盘图标现状
- `TrayAppContext`（`ApplicationContext` 子类）持 `NotifyIcon` + `ContextMenuStrip`。
- `AppState.TrayIcon` 暴露给功能模块显示气球（`ShowBalloonTip`）。
- 看门狗 `System.Windows.Forms.Timer`（2s）。

---

## 3. 关键技术决策点（待用户确认）

### 3.1 托盘图标方案（WPF 无内置 NotifyIcon）

| 方案 | 新依赖? | 说明 | 推荐度 |
|---|---|---|---|
| **A. 保留 WinForms `NotifyIcon`** | 否（继续引用 WinForms） | WPF 主循环 + 仅托盘用 WinForms NotifyIcon。NotifyIcon 不依赖 `Application.Run`，在 WPF Dispatcher 线程可工作（创建隐藏消息窗口接收通知）。`ContextMenuStrip` 右键菜单仍用 WinForms。 | ⭐⭐⭐ 最省事、零依赖；但项目仍带 WinForms 引用 |
| **B. 引入 `H.NotifyIcon.Wpf` NuGet** | 是（+1 轻量库） | 纯 WPF 托盘，原生 `TaskbarIcon`，支持 WPF 上下文菜单。社区主流方案。 | ⭐⭐⭐ 干净，但要破「无第三方依赖」原则 |
| **C. P/Invoke `Shell_NotifyIconW` 自实现** | 否 | 完全自控，但要自己处理 `NIN_BALLOONCLICK`/右键菜单/图标资源，代码量大。 | ⭐ 不推荐（重复造轮子） |

### 3.2 迁移策略

| 策略 | 说明 | 风险 |
|---|---|---|
| **增量迁移（推荐）** | `csproj` 同时 `UseWPF=true` + `UseWindowsForms=true`（混合模式合法），从最简单窗体起逐个重写，每窗体一个 commit，期间程序可运行可测。 | 中途 WinForms/WPF 窗体并存，但可逐步验证。 |
| **整体重写（big-bang）** | 一次性把 6 个窗体全换 WPF，删 WinForms。 | 高：一次改动大，难定位回归；期间不可运行。 |

### 3.3 XAML 风格

- **XAML + code-behind**（推荐）：每个 `Window` 一份 `.xaml` + `.xaml.cs`，逻辑简单的用 code-behind，`ConfigHelper` 这类重的引入轻量 `ViewModel`（不一定上完整 MVVM 框架，避免新依赖）。
- 是否启用 `Microsoft.Xaml.Behaviors.Wpf` 等行为库？建议**不引入**，保持零额外依赖。

### 3.4 主题/样式

- 新建 `Themes/Modern.xaml`（`ResourceDictionary`）：配色画刷（`AccentBrush`/`SurfaceBrush`/`DangerBrush`…）、`Button` 的 `Style`（默认 + `Primary`/`Danger` 的 `BasedOn` 派生）、`TextBox`/`ListBox`/`ListView` 样式、圆角（`ControlTemplate` + `CornerRadius`）。
- 对应原 `UiTheme.cs` 退役；`UiTheme.EnableRounded`(DWM) 不再需要（WPF 自绘圆角）。
- DPI：WPF 矢量 + PerMonitorV2 自动缩放，彻底解决文字裁剪。

### 3.5 模态对话框返回值

WinForms `ShowDialog()` 返回 `DialogResult`；WPF 模态用 `window.ShowDialog()` + 自定义属性（如 `DialogResult`/`ResultValue`）。`InputDialog.Show`、`MenuItemEditDialog` 需调整签名（去掉 `IWin32Window owner`，改传 WPF `Window` owner 或 null）。

---

## 4. 分阶段迁移计划

> 每个 Phase = 一个或多个 commit，每 commit 必须 `dotnet build` 0 错 0 警，并通过对应冒烟验证。混合模式期间程序始终可运行。

### Phase 0 — 项目与基础设施
- `csproj`：加 `<UseWPF>true</UseWPF>`（保留 `<UseWindowsForms>true</UseWindowsForms>` 直至 Phase 6）。
- 新增 `Themes/Modern.xaml`（`ResourceDictionary`），迁移 `UiTheme` 配色为画刷 + 基础 `Button`/`TextBox` 样式。
- 新增 `App.xaml` + `App.xaml.cs`：WPF `Application` 子类，`OnStartup` 里安装钩子、建托盘、初始化 `MenuSystem`/`QuickNote`/`ConfigHelper`（替代 `TrayAppContext` 构造逻辑）。
- `Program.cs`：`Main` 改为启动 WPF `App`（不再 `Application.Run(TrayAppContext)`）；保留单实例互斥体、`ApplicationConfiguration.Initialize()`。
- 托盘按 §3.1 选定方案实现（Phase 0 即落地）。
- **冒烟**：托盘图标出现、右键菜单可弹、双击/菜单项触发 `Toggle`（此时窗体仍是 WinForms，验证混合模式不崩）。

### Phase 1 — 迁移 `InputDialog`（最简单，模态）
- 新建 `Views/InputDialog.xaml` + `.cs`：标签 + TextBox + 确定/取消，`ShowDialog()` 返回 `(bool, string)`。
- 替换 `ConfigHelper`/`MenuSystem` 中 `InputDialog.Show(owner,...)` 调用（owner 改 WPF `Window?`）。
- 退役 `Features/InputDialog.cs`（WinForms 版）。
- **冒烟**：`ConfigHelper` 内调用InputDialog 的路径正常返回值。

### Phase 2 — 迁移 `HelpForm`（只读文本，简单）
- 新建 `Views/HelpPanel.xaml`：标题栏 + 滚动文本（`FlowDocument` 或 `ItemsControl`）。
- `HelpPanel.Toggle()` 改为 WPF `Window.Show()/Close()`；`BuildHelpText()` 复用。
- 退役 `HelpForm`。
- **冒烟**：CapsLock+\` 弹出/关闭、文本完整。

### Phase 3 — 迁移 `MenuItemEditDialog`（模态表单）
- 新建 `Views/MenuItemEditDialog.xaml`：名称 TextBox + 命令 TextBox + 终端 ComboBox + 保持窗口 CheckBox + 确定/取消。
- 返回 `MenuEntry` 或 null。
- `ConfigHelperForm` 调用点改 WPF。
- **冒烟**：添加/编辑菜单项返回正确数据。

### Phase 4 — 迁移 `MenuPopup`（非模态，光标处弹出 + 悬停激活）
- 新建 `Views/MenuPopup.xaml`：`ItemsControl` 渲染菜单项（序号 + 名称）；`Window` 设 `AllowsTransparency`/`WindowStyle=None`/`ShowInTaskbar=false`；`Left/Top` 设为光标坐标（复用 `Win32.GetCursorPos`）。
- 悬停激活（`MouseEnter` → 触发该项命令）、序号按键激活、Esc/失焦关闭。
- `MenuSystem.Show(i)` 改 `new MenuPopup(...).Show()`。
- **冒烟**：CapsLock+1~0 弹出、悬停/序号激活、Esc 关闭、定位准确。
- **注意**：失焦关闭（`Deactivated` 事件）需谨慎，避免与子菜单/对话框互抢焦点。

### Phase 5 — 迁移 `QuickNoteForm`（编辑/查看双模 + 保存逻辑）
- 新建 `Views/QuickNote.xaml`：`Grid` 上层编辑区（`TextBox`）+ 查看区（`ListView`/`ListBox`）切换；底部按钮行（`StackPanel`/`WrapPanel`）；状态提示 `TextBlock`。
- 保存逻辑（`## title` 解析、`==target==` 路由、时间戳追加、`CleanFileNameFromTitle`）抽到 `Features/NoteService.cs`（UI 无关），窗体只调服务。
- `QuickNote.Toggle()` 改 WPF；`Initialize`/`ReloadTargets` 不变。
- **冒烟**：查看/编辑切换、新建保存（标题/正文/目标路由正确）、删除、搜索过滤、物理 CapsLock+N。

### Phase 6 — 迁移 `ConfigHelperForm`（最重：标签页 + 双 ListView + INI 双向）
- 新建 `Views/ConfigHelper.xaml`：`TabControl` 两页；菜单组/菜单项 `ListView`（`GridView`）；底部操作行。
- 数据层抽 `Features/ConfigService.cs`：`LoadFromIni()`/`Save()`/组项增删改排序/命令字符串构造——纯逻辑，WPF 与未来可能的测试复用。
- 窗体用数据绑定或直接事件调 `ConfigService`；`MenuItemEditDialog`（Phase 3 已 WPF）作模态子窗。
- 保存后仍调 `MenuSystem.Load` + `QuickNote.ReloadTargets`。
- **冒烟**：组/项增删改排序、保存写回 INI、菜单与速记目标重载生效。

### Phase 7 — 收尾
- 若 §3.1 选 A（WinForms 托盘）：保留 `UseWindowsForms`，仅托盘用；否则删 `<UseWindowsForms>` 与所有 WinForms 残留。
- 删 `UiTheme.cs`、旧 WinForms 窗体文件、`TrayAppContext.cs`（逻辑已进 `App.xaml.cs`）。
- 全量 `dotnet build` 0 错 0 警；物理测全部 CapsLock+ 组合。
- 更新 handoff。

---

## 5. 各窗口迁移要点速查

| 窗体 | WPF 关键点 | 陷阱 |
|---|---|---|
| InputDialog | `ShowDialog()` + `DialogResult` 自定义 | owner 类型从 `IWin32Window`→`Window` |
| HelpForm | `FlowDocument`/`ScrollViewer` | 文本量大需虚拟化或分段 |
| MenuItemEditDialog | ComboBox 绑定终端列表 | 返回值改属性 |
| MenuPopup | `AllowsTransparency=True`+`WindowStyle=None` 光标定位 | 悬停激活与失焦关闭冲突；`Topmost` 必要 |
| QuickNoteForm | 编辑/查看 `Visibility` 切换 | 保存逻辑必须抽服务，别留在 code-behind |
| ConfigHelperForm | `TabControl`+`ListView`(GridView) | ListView 复选框改 `CheckBox` 列或 `IsChecked` 绑定；INI 保存逐行编辑式保留注释 |

---

## 6. 主题样式策略（替代 UiTheme）

`Themes/Modern.xaml` 草案：

```xml
<ResourceDictionary>
  <SolidColorBrush x:Key="BgBrush" Color="#F6F7F9"/>
  <SolidColorBrush x:Key="SurfaceBrush" Color="White"/>
  <SolidColorBrush x:Key="BorderBrush" Color="#C9CDD6"/>
  <SolidColorBrush x:Key="TextBrush" Color="#1E1F2D"/>
  <SolidColorBrush x:Key="AccentBrush" Color="#2563EB"/>
  <SolidColorBrush x:Key="DangerBrush" Color="#DC2626"/>
  <!-- Button 基础样式：圆角 + 悬停 -->
  <Style TargetType="Button">
    <Setter Property="Padding" Value="16,7"/>
    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
    <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}"
                  BorderBrush="{StaticResource BorderBrush}"
                  BorderThickness="1" CornerRadius="6"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="PrimaryBtn" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
    <Setter Property="Foreground" Value="White"/>
  </Style>
  <Style x:Key="DangerBtn" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="{StaticResource DangerBrush}"/>
    <Setter Property="Foreground" Value="White"/>
  </Style>
</ResourceDictionary>
```

- 文字永不裁剪（WPF 按内容自适应尺寸 + 矢量缩放）。
- 圆角自绘，不依赖 DWM `DwmSetWindowAttribute`。

---

## 7. 验证策略

### 7.1 每阶段构建门槛
- `dotnet build` **0 错 0 警**（保持既有标准）。
- 冒烟：对应窗体能从托盘/热键弹出，核心交互可用。

### 7.2 自动截图核验（替代 WinForms DrawToBitmap）
WPF 窗体可用 `RenderTargetBitmap` 自绘到文件（不依赖 z 序），复用「让窗体自绘到 PNG → `describe_image` 逐项核验」的验证套路：
```csharp
var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
bmp.Render(w);
PngBitmapEncoder enc = new(); enc.Frames.Add(BitmapFrame.Create(bmp));
File.WriteAllWrite(path, enc.ToArray());
```
临时 `--smoke=xxx` 入口（交付前移除）+ 上面的自绘，做每阶段视觉回归。

### 7.3 物理测试清单（每个 Phase 后）
- [ ] CapsLock+\`（帮助）、CapsLock+N（速记）、CapsLock+\（配置）、CapsLock+1~0（菜单）、CapsLock+Q（搜索）
- [ ] 托盘双击 + 右键菜单项
- [ ] 单实例锁、退出无残留
- [ ] 钩子不阻塞（连续操作无「钩子被卸载」症状）

---

## 8. 风险与回退

| 风险 | 缓解 |
|---|---|
| WPF Dispatcher + WinForms NotifyIcon 混用出现焦点/事件怪象 | Phase 0 即验证托盘；若怪象难解，回退到 §3.1 方案 B（H.NotifyIcon.Wpf） |
| 钩子回调在 WPF 线程 Show 窗体时阻塞 >300ms | 阻塞操作本来就走后台线程（handoff 约束）；每阶段用 §7.3 物理测验证不卸钩子 |
| `MenuPopup` 失焦关闭与子对话框互抢焦点 | 用 `Deactivated` + 短延迟，或显式「有模态子窗时不自动关」标志 |
| `ConfigHelper` INI 逐行编辑式保存需保留注释格式 | 把现有 `IniFile` 逻辑原样搬进 `ConfigService`，不改算法 |
| 混合模式期间 WPF/WinForms 窗体并存焦点混乱 | 增量迁移顺序：先小后大；每阶段确保旧窗体仍可单独弹出 |
| 回退 | 全程在 `csharp-refactor` 分支按 Phase 提交；任一 Phase 出问题可 `git revert` 或回退到 Phase 0 |

---

## 9. 交付物
- `docs/WPF_MIGRATION_PLAN.md`（本文件）
- `Themes/Modern.xaml` + 各 `Views/*.xaml`
- `App.xaml`/`App.xaml.cs`（替代 `TrayAppContext`）
- 数据服务：`Features/NoteService.cs`、`Features/ConfigService.cs`（UI 无关）
- 移除：`UiTheme.cs`、旧 WinForms 窗体、`TrayAppContext.cs`（逻辑并入 App）
- 更新 handoff

---

## 10. 已确认决策（执行依据）
1. **托盘方案**：✅ B — 引入 `H.NotifyIcon.Wpf` NuGet。
2. **迁移策略**：✅ 整体重写（一次性 6 窗体全换 WPF，期间旧 WinForms 窗体一并删除）。
3. **第三方库**：✅ 允许必要的轻量库 —— `H.NotifyIcon.Wpf`（托盘）、`CommunityToolkit.Mvvm`（ViewModel 基类/源生成器，运行时零负担）。
4. **本会话范围**：✅ 全部 Phase 0~7。

> 策略从「增量混合」调整为「整体重写」后，Phase 0 一次到位（基础设施 + 托盘），随后逐窗体重写并**立即删除对应 WinForms 文件**，不再保留混合模式。
