# 交接文档

面向接手本项目的人（或未来的自己）。README 讲"怎么用"，这里讲"为什么这么做、改的时候会踩到什么"。

- 当前版本：**v1.12.0**（上游 0.4.1 适配，以及下载、主题、语言与视图模型改进）
- **上游 0.4.1**：配置、兼容迁移与驱动要求见 [0.4.1 适配记录](UPSTREAM-0.4.1.md)。旧版超分 On 在应用新版前要求主动选择显式模式；已有档位与各引擎倍率保留。
- 后续仅发布 Windows x64 自包含单文件绿色版，统一使用 `Portable` 发布配置；产物为 `publish/DLSSGManager.exe`。
- **v1.11.0 已适配上游 0.4.0**：完整包下载、目录化运行负载、单代理升级、新配置与恢复规则见 [0.4.0 适配记录](UPSTREAM-0.4.0.md)。
- **v1.12.0 重构内容**：
  - 下载链路：SourceForge 镜像源、并发测速排序、断点续传、离线 ZIP 导入、完整包/基础包选择
  - 主题系统：跟随 Windows 深浅色模式（"system" 选项 + WM_SETTINGCHANGE 监听）
  - 语言热切换：无闪烁的单帧更新（LocalizedText / Choice<T> / LocalizedString 架构）
  - MVVM 拆分：GameManagementViewModel / BatchViewModel / ProtectionViewModel + IBatchHost 接口
- 改进范围：见 [UI 落地清单](UI-IMPROVEMENTS.md)。
- 仓库：`github.com/BUNNY-19C/DLSSG-30s-manager`，打 tag 即发版

---

## 1. 这个项目是什么

给 RTX 30 系（SM86）装 DLSS 帧生成的管理器。上游 [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 提供代理 DLL（内嵌 DLSSG 运行时 + SM86 后端），本项目负责：

- **找游戏**（扫描 Steam 库 / 任意目录）
- **按游戏配置**（Enabled / Optimized 档位 / Preset / 倍率 / 日志级别 → 写进 `dlssg_sm86.ini`）
- **部署与恢复**（代理 DLL + INI 放入渲染目录；恢复时只删能证明是本项目的文件）
- **下载上游负载**（多源、证书 pin、进度条、暂停/取消）

**不做的事**：不修改上游 DLL、不随管理器分发上游二进制、不引入第三方（如 pandaligx/RTX-FG-Manager）的文件。绿色版不含 mod，首次启动自动获取。

---

## 2. 五分钟上手

```bash
# 构建（需要 .NET 8 SDK）
dotnet build src/DLSSGManager/DLSSGManager.csproj -c Debug

# 下载 Mod 文件，使用程序自身的下载入口
dotnet run --project src/DLSSGManager -c Debug -- --fetch --silent

# 构建免安装绿色版（自带 .NET）
dotnet publish src/DLSSGManager/DLSSGManager.csproj -c Release -p:PublishProfile=Portable

```

**发版流程**：`git tag v1.9.4 && git push origin main --tags` → GitHub Actions（`.github/workflows/release.yml`）自动构建绿色版与 SHA256SUMS 并挂到 Release。工作流会把 tag 号通过 `-p:Version` 注入程序集，所以界面里的版本徽章自动正确。

**本地开发要点**：`DLSSGMANAGER_HOME` 环境变量可把数据目录重定向到指定路径，与用户默认库（`%APPDATA%\DLSSGManager`）分开保存。

---

## 3. 代码地图

```
src/DLSSGManager/
├─ DeploymentService.cs 1087 部署/恢复/接管/状态判定——唯一写游戏目录的地方
├─ MainWindow.xaml      1055 界面布局（顶栏/信息条/侧边栏/卡片详情/进度条/日志条）
├─ ModFetcher.cs        1280 下载：多源链+SourceForge、并发测速、暂停/跳过门、断点续传、完整/基础包、离线导入
├─ MainWindow.xaml.cs   710  窗口装配、主题/语言切换、游戏列表筛选、视图模型绑定
├─ Gpu.cs               548  显卡探测、架构判定、注册表显示名读写、INF 间接字符串解析
├─ Strings.zh/en.cs     534  两张字符串表（必须逐键对齐）
├─ MainWindow.Actions.cs 432 部署/恢复单个游戏/扫描/路径选择/打开与启动处理器
├─ SourcePickerDialog   420  下载源选择：包类型、测速结果、拖拽导入、进度面板（暂停/跳过/取消）
├─ Models.cs            343  GameEntry / GameProfile / DeploymentInfo / AppData
├─ ModSource.cs         315  本地负载：入口名、0.4.0 运行目录、配置约束、导入自定义代理
├─ Store.cs             309  library.json 读写与归一化、INI 模板按节渲染
├─ Detection.cs         283  游戏发现：双标记扫描、渲染目录解析、主程序挑选
├─ AntiCheat.cs         281  内核级反作弊识别、隔离副本查找
├─ Shell.cs             231  打开目录/文件、启动游戏、提权重启
├─ ModSourceLocator.cs  191  负载目录解析（仓库检出 / 便携 / 用户目录）
├─ MainWindow.Management.cs 200  保存状态（未保存/待应用/已应用）与关闭流程
├─ Localization.cs      220  Loc.T、LocalizedText 延迟本地化消息、Choice<T> / LocalizedString
├─ ViewModels/
│  ├─ GameManagementViewModel.cs  180 单游戏配置状态、保存/应用按钮、构建警告
│  ├─ BatchViewModel.cs            160 批量操作：勾选集、部署/恢复命令、结果面板
│  └─ ProtectionViewModel.cs       90  反作弊横幅、部署工具提示、批量通知
├─ MainWindow.Batch.cs  40   IBatchHost 实现（批量操作宿主接口）
├─ MainWindow.Protection.cs 100  添加目录时立即提示内核级反作弊
├─ Theme.cs / ThemeKeys.cs   主题系统：跟随 Windows ("system" 偏好 + 注册表读取)
├─ App.xaml / App.xaml.cs    启动、--fetch 命令行下载、LocalizedString 数据模板
├─ ManagementState.cs   100  配置快照（见 4.10）、运行库构建约束
├─ GpuNameDialog        显卡显示名编辑
├─ 其余工具：Native、OutputLog、Converters、Palette、Version、TrExtension
└─ Themes/Dark.xaml + Light.xaml   两套主题字典（键必须完全一致）

mod/                          下载得到的上游负载，不入库
test/DLSSGManager.Tests/      xUnit 测试项目（INI 渲染、配置快照、负载识别、部署往返）
```

**分层原则**：模型与领域层（Models / Gpu / Detection / DeploymentService / ModFetcher / AntiCheat / Store）**不得引用 WPF 类型**，界面依赖集中在窗口、展示层与视图模型。视图模型（ViewModels/）持有界面状态但不直接操作控件，通过接口（如 IBatchHost）回调窗口。

---

## 4. 关键决策与"为什么"

改代码前先读这几条，都是踩过坑才写下来的：

### 4.1 界面

- **颜色一律 `DynamicResource`**。`StaticResource` 在元素创建时解析一次，切换主题后界面停在旧配置。
- **两套主题字典键必须一致**，切换主题时才能正确解析全部资源。
- **列表状态色需要显式重发**：`ThemeBrushConverter` 只在源属性通知时重算，切主题要调 `GameEntry.RaiseThemeColors()`。
- **对话框有自己的资源作用域**，主窗口的隐式样式传不进去，按钮/输入/滚动条样式要在每个 Window 里重声明。
- **主题跟随系统**（v1.12.0 新增）：`Theme.ApplyPreference("system")` 通过注册表读取 Windows 深浅色模式，MainWindow 在 `OnSourceInitialized` 中添加 `WM_SETTINGCHANGE` 消息钩子，监听 "ImmersiveColorSet" 变更并调用 `AfterThemeSwitch()`。
- **语言热切换无闪烁**（v1.12.0 重构）：
  - `LocalizedText` 持有 key+args，读时渲染为当前语言；`GameEntry.StatusMessage` 与 `DeploymentService.GameCheck.Detail` 现为 `LocalizedText?`，不再是固定字符串。
  - `Choice<T>` 泛型（替代 `Choice` / `TextChoice` record），下拉项通过 `INotifyPropertyChanged` 通知文本变更，无需重建列表。
  - `LocalizedString` 用于 ComboBoxItem.Content（WPF 会复制 Content 导致绑定失效），`App.xaml` 定义隐式 DataTemplate 渲染它。
  - 语言切换时所有文本在一帧内更新完毕（`Loc.SetLanguage` 发出通知 → 绑定与 `Choice<T>.Text` 重新求值），游戏详情不需要重新检查。

### 4.2 线程纪律（不要再退回 ContinueWith）

早期版本用 `Task.Run(...).ContinueWith(t => ... t.Result ...)`，两个真实缺陷：faulted 时异常被静默吞掉，且 `_busy` 标志卡死导致所有按钮失效直到重启。

**现在的形态**：所有耗时操作都是 `async void` 处理器 + `try/catch/finally`，`_busy` 在 finally 释放；重活 `await Task.Run(...)`，结果回 UI 线程应用。新增操作请照 `MainWindow.Actions.cs` 的形状写。全局兜底：`TaskScheduler.UnobservedTaskException` 落日志。

**`DeploymentService.Evaluate`（后台）与 `Apply`（UI）是分开的**——`Evaluate` 会做哈希与 WinVerifyTrust（每个候选入口一次 ~30MB DLL），绝不能在 UI 线程调用。

状态检查在没有部署记录时自动识别手工安装，并通过 `GameCheck.Adoption` 返回待接管记录；`Apply` 在 UI 线程登记，调用方随后保存库。单个检查、启动/刷新全部、扫描和添加目录共用此流程。接管不改写游戏文件、不生成原始备份，也不覆盖已有部署记录和备份；界面不再提供手动接管按钮。

### 4.3 部署所有权判定（数据安全的底线）

恢复时"删除"必须能证明文件是本项目的，判据三层：

1. 记录里的 SHA-256（`DeploymentInfo.Files`）
2. 已知社区构建哈希（`ModFetcher.KnownCommunityBuildHashes`）
3. 项目 Authenticode 签名（`IsProjectSigned`，走 WinVerifyTrust + 证书主题匹配）

**备份必须结转**：重新部署会重建 `DeploymentInfo`，若不把 `prev.Backups` 带过来，第一次部署置换的外部文件（别人的 INI）就永远回不来了。

**代理入口集合**：`OwnedEntryNames(prev)` = 已知入口名 ∪ 记录 `Files`（排除 INI）——自定义命名的代理只活在记录里，漏了它恢复就删不干净。

### 4.4 代理数量与清理

当前 0.4.x 使用一个代理和完整运行目录，按 `ModSource.RequiresSingleProxy` 决定是否清理多余入口。仅清理能确认归属的文件，并保留原始备份与逐文件记录。旧版 0.3.x 的待机代理规则只适用于对应旧负载，不能作为新版安装建议。

### 4.5 游戏发现（Detection）

双标记扫描：`nvngx_dlssg.dll`（游戏自带 DLSS-G 负载）**或** `dlssg_sm86.ini`（本项目独有文件名，识别手装副本）。只看前者会漏掉不自带 DLSS-G、但已手工部署本项目的游戏。

`GenericFolderNames` 用于跳过无意义的目录名（Win64/Binaries/Client/…）取游戏名；上游标记命中后向上找 3 层找主程序。

### 4.6 下载安全模型（ModFetcher）

威胁模型是"镜像不可信"：

1. 仅 HTTPS + **主机白名单**，且解析结果必须全为公网地址
2. **代理感知**：检测到系统代理时跳过本机 DNS 校验——否则 DNS 污染/hosts 加速的用户会被全部源拒绝（issue #1 的真实原因）
3. 重定向逐跳复检、响应体积上限、归档条目不得逃逸目标目录
4. 载荷校验：所有 DLL 必须带项目签名；**镜像源**还必须匹配证书 pin（`85BA6676…`；GitHub 官方端点不匹配只记警告，容忍上游换证书）
5. **校验通过前不写 mod 目录**——所以取消/失败都不会破坏已有负载

**v1.12.0 下载架构重构**：
- **多源支持**：GitHub Release 资产重定向 + SourceForge 镜像（7 个源）；自动添加 `dl_source=zc` 标签用于上游流量分析。
- **并发测速**：`ProbeSourcesAsync` 并发 GET 前 16KB，按延迟与吞吐量排序（HEAD 测不出真实速度）。
- **暂停/跳过/取消**：`DownloadGate` 协调暂停门（await `PausedEvent`）+ 每源跳过标志（`_skipCurrent`）+ 整体取消令牌。
- **断点续传**：Range 请求 + 停顿检测（5 秒无进度自动重试当前块）+ RangeNotSatisfiable 处理。
- **完整包 / 基础包**：`PayloadPackage.Full` 含超分内核（~90MB），`Base` 仅帧生成（~46MB）；用户可在 `SourcePickerDialog` 选择。
- **离线导入**：`ImportArchiveAsync` 支持拖拽 .zip 或浏览文件，解压至临时目录并按完整包/基础包验证签名，匹配后移入 `mod/`。
- **安全解压**：条目数上限 500、单条目 200MB、总大小 300MB，自动展平单目录包装层（archive-wrapper flattening）。
- **TransferAsync 辅助方法**：统一 Range 请求、进度回调、速度计算（`IProgress<FetchProgress>`），供下载与测速共用。

**SourcePickerDialog 新对话框**（替代主窗口嵌入式面板）：
- 包类型选择（完整/基础）+ 构建目录选择（310.9 / 310.1）
- 速度测试结果 + Retest 按钮
- 源列表显示实时速度（fastest / available / unavailable）
- 拖拽区 + Browse 按钮用于离线 ZIP 导入
- 嵌入式进度面板（Pause / Skip / Stop 按钮）
- 安装成功后 `Installed` 标志返回主窗口刷新

### 4.7 INI 模板与 schema 演进

`IniTemplate.Render` **只覆写模板里已存在的键**。上游换 schema 时（0.2.x 的 Router/KernelImage → 0.3.x 的 Enabled/Optimized/Preset），旧键自然不写、新键自然生效，不需要分支。`IsLegacySchema`（检测 `Router=`）决定界面显示哪组控件。

**按节写入**：`[Runtime] Mode` 保留上游模板值，`[SuperResolution] Mode` 写显式超分模式；`Optimized` / `Preset` 写 `[DLSSG]`，`FlowResolution` / `UseHudless` 写 `[XeSSFG]`，`FrameTiming` 写 `[Logging]`。节外同名键不被误写。

### 4.8 MVVM 视图模型拆分（v1.12.0）

引入 CommunityToolkit.Mvvm 8.4.2（`ObservableObject` / `RelayCommand`），三个独立视图模型替代 MainWindow 内联状态：

- **GameManagementViewModel**：单游戏配置状态（未保存/待应用/已应用）、保存/应用按钮启用逻辑、构建选择警告（310.1 限制检查）。
- **BatchViewModel**：批量操作（勾选集合、可见项计数、部署/恢复命令）、结果面板（成功/失败计数、逐项日志）、失败重试。
- **ProtectionViewModel**：反作弊检测状态（横幅显示）、部署按钮工具提示（内核级反作弊警告）、批量操作提示。

**IBatchHost 接口**：主窗口实现 `IEnumerable<GameEntry> VisibleGames { get; }` / `void RefreshGameView()` / `void ShowResult(bool, string)` / `void LogDetails(IEnumerable<string>)`，BatchViewModel 通过接口回调而不直接操作控件。MainWindow.Batch.cs 显式实现该接口（避免公开暴露内部方法）。

**好处**：
1. 单游戏详情与批量操作的状态逻辑分离，MainWindow.xaml.cs 不再包含按钮启用计算。
2. 视图模型可单独测试（模拟 `IBatchHost` 即可验证命令逻辑）。
3. 命令绑定替代事件处理器（`Command="{Binding Batch.DeployCommand}"` 而非 `Click="DeployAll_Click"`）。

配置面板只显示当前 schema 的键——显示另一组是死控件。

### 4.9 版本标记

0.3.0 起上游 INI 不再有 `; Native x.y.z.` 横幅，所以版本号靠两级：INI 横幅 → README 标题 → 下载时探测到的版本写进 `.manager-version` 标记文件（`--fetch` 路径也必须探测，否则徽章停在旧版本——修过一次）。

### 4.10 配置快照（"待应用"提示）

部署时把 `RenderDir` / `PreferredProxy` / `Profile` 序列化成 JSON 存进 `DeploymentInfo.AppliedConfiguration`，`ConfigurationState.NeedsApply` 拿它和当前配置比较。

- 比较前先把旧快照**按当前类型读回再序列化**：新增的 `GameProfile` 字段在旧快照里不存在，读回时取默认值，所以加设置不会把所有已部署游戏都标成待应用。
- 由此带来的约束：**新字段的默认值必须等于上游出厂默认**——旧部署的 INI 从没写过它，实际生效的就是上游默认。改了某个字段的含义或取值范围时，旧快照会被当成"未变"，需要单独处理。
- 没有快照的旧记录（v1.11.0 之前部署）一律显示待应用，不臆测其配置。

---

## 5. 上游更新了怎么办（适配手册）

上游更新时按这个清单逐项核对，**不要凭版本号猜测**：

```bash
# 1. 版本 + 提交历史
curl -s "https://api.github.com/repos/sdli1995/dlssg_for_sm86/commits?per_page=3" | python -c "..."
# 2. README 标题（版本探测依赖的正则）
curl -s "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/README.md" | head -2
# 3. 文件树（比对本项目 Payload 表的 SourcePath）
curl -s "https://api.github.com/repos/sdli1995/dlssg_for_sm86/git/trees/main?recursive=1" | python -c "..."
# 4. INI 键（schema 是否变化）
curl -s "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/dlssg_sm86.ini" | grep -E "^[A-Za-z]+="
# 5. 证书指纹（下载一个 DLL 验签）
curl -sL -o /tmp/v.dll "https://gh-proxy.com/https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/version.dll"
powershell -Command "(Get-AuthenticodeSignature 'C:\tmp\v.dll').SignerCertificate.Thumbprint"
```

对照点：

| 项目 | 代码位置 | 变了要改 |
|---|---|---|
| 下载路径 | `ModFetcher.Payload` | 上游挪目录（如 0.3.0 的 `altnative/`→`alternatives/`）只改这张表 |
| 入口名 | `ModSource.ProxyCandidates` / `KnownProxyNames` | 新增/删除代理名 |
| 证书 pin | `ModFetcher.PinnedCertThumbprint` | 上游换证书（镜像源会直接拒绝） |
| 版本探测 | `ModSource.ReadVersionFromText` / `ModFetcher.ReadVersionFromReadme` | 横幅/标题格式变化 |
| INI 键 | `IniTemplate.Values` | 新键需要界面控件 |
| 默认值 | `GameProfile` 字段默认值 | 上游调默认（0.3.3 把倍率默认从 5 调到 3） |

**历史经验**：0.3.4 修了 0.3.3 在 RTX 30 上的驱动崩溃、0.3.5 修了帧生成重建后的花屏——这类纯运行时修复**不需要改管理器代码**，只需让用户更新负载。判断标准是：布局/schema/入口/证书都没变，就只更新负载。

---

## 6. 开发环境陷阱

1. **Mimosa hook 拒绝 Bash 写 .cs**：所有 C#/XAML 改动必须用 Write/Edit 工具（hook 会在提交前做安全扫描）。
2. **.NET 8 SDK 会把 commit hash 拼进 `InformationalVersion`**（`1.9.3+abc1234`），界面只取 `+` 前的部分。
3. **`ModSource` 是构造时快照**：导入自定义代理后必须重建实例才能看到新入口。
4. **`Stopwatch.GetTimestamp` 与 `TimeSpan` 不能混算**，用 `Stopwatch.GetElapsedTime()`。
5. **本地代理（Clash）抖动会导致 push 失败**——`git push` 报 443 连接失败时，提交和 tag 是安全的，稍后重试即可（Git 配置里 http.proxy 指向 127.0.0.1:7897）。

---

## 7. 已知限制 / 待办

按价值排序：

1. **310.1 构建变体现已支持（当前工作区）**：DLL 下载路径加 `310.1/`，INI 仍取上游根目录；本地使用 `mod/variants/310.1` 与默认根构建分开。`ManagementState.cs` 定义构建限制与配置快照，`MainWindow.Management.cs` 负责保存状态与关闭流程，`MainWindow.Batch.cs` 负责 IBatchHost 实现。旧记录没有应用快照时显示待应用，不臆测其配置。
2. **构造审查的 Optional 项**（未做，风险低）：
   - `ExtractArchive` 限制了条目数（500）、单条目大小（200MB）、总解压量（300MB），防止 zip 炸弹（v1.12.0 已实现）。
   - 保存失败与关闭保护已落地：Save 返回结果；UI 保留内存记录和重试提示，批量保存失败停止后续项；Closing 等待操作完成或取消下载后退出。此流程不覆盖强制结束进程或断电。
3. **反作弊规则表**与 GPU 设备 ID 区间是经验数据，遇到误判按实际游戏样本增补。
4. **测试覆盖**：`test/DLSSGManager.Tests` 包含 7 个最小测试用例（INI 按节渲染、配置快照、310.1 构建规则、负载识别、部署还原往返、下载源策略），但缺少 UI 自动化测试与集成测试。主窗口拆分为 5 个分部类（xaml.cs / Actions / Management / Batch / Protection），视图模型引入后可单独测试命令逻辑，但尚未编写 ViewModel 单元测试。

---

## 8. 协作约定

- **issue 回复是给外部用户看的**：大白话讲原因、给可操作步骤、版本号只提最新；实现细节留在 commit message 与代码注释里。
- **README 一切从简**，游戏特有事项集中在页首的注意事项块。
- **不引入第三方二进制**；用户手装的文件只做识别与接管，不打包、不改写内容。
- 提交信息写清"为什么"（多数缺陷是语义变化引发的，只写"改了什么"后人会改回去）。
- 改动上游适配时，先跑第 5 节的核对清单，再决定改代码还是只更新负载。
