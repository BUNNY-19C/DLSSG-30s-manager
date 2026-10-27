## v1.12.0：适配上游 0.4.1

- 移除已过时的游戏适配提示、自动名称匹配和前置文件检查，清理对应的文档与翻译。旧游戏库中的提示选择字段仍可读取并忽略。

- 下载与离线导入支持 0.4.1，继续使用完整包 / 基础包与原有签名校验、逐文件部署和恢复流程。
- XeSS 新增 2X / 3X / 4X 独立倍率、CoarseAt4K 和 Auto / 严格 / 关闭三态无 HUD 输入；切换引擎保留 DLSS 倍率。
- 超分改为 Off、M-INT8、L-INT8、M-FP16、L-FP16。旧 On 保留原值，在应用新版前提示选择明确模式；旧无 HUD 偏好迁移为 Auto。
- 新游戏使用优化档位 1、预设 A；已有档位和配置继续保留。旧 Auto 在 0.4.1 按 A 写入，旧部署快照兼容布尔配置字段。
- 显示 NVIDIA R580+ 驱动要求，以及不支持的倍率或输入格式会拒绝启用的说明。
- “查看日志”优先打开新版统一的 dlssg_sm86.log，仍支持旧版 JSONL 日志。

### 升级说明

更新本地 Mod 后，对目标游戏执行应用并重启游戏。下载不会自动改写游戏目录。原有游戏库、文件归属记录与恢复备份继续使用；旧版超分开启的游戏需先选择明确的新模式。

### English

Supports upstream 0.4.1: independent XeSS 2X / 3X / 4X, CoarseAt4K, tri-state HUD-less input and explicit SR modes. New profiles default to tier 1 and preset A while existing settings are retained. Legacy SR On requires an explicit new mode before applying; legacy HUD-less preference becomes Auto. The UI explains the R580+ driver requirement and prefers the unified log.

---

## v1.11.0：上游 0.4.0 适配、下载与界面改进

- **上游 0.4.0**：支持完整运行目录、DLSS / XeSS、FG Lite 预设 C、INT8 超分和逐帧 GPU 计时；状态检查自动接管已识别的手工安装。

- **下载窗口补全**：可选择完整包或基础包，查看各源测速结果，自动优先使用最快的源；支持暂停、继续、跳过当前源和取消。
- **离线 ZIP 导入**：可拖入或选择上游发布的 ZIP，沿用文件完整性与签名校验，通过后更新本地负载。
- **下载恢复**：连接中断时尝试按字节续传，服务器不支持续传时重新下载；无数据超时后切换源。
- **界面状态整理**：批量操作、游戏配置状态与反作弊展示拆入 ViewModel。主题支持跟随 Windows；切换语言时更新状态说明及下拉框当前值。
- **下拉框翻译修复**：主题、一致性档位、倍率与日志等级共用直接绑定的文本模板，切换语言后立即刷新当前选中项，保留原有配置值，无需再次选择。
- **配置兼容**：旧部署快照按当前配置类型补齐默认字段，减少新增选项导致的错误“待应用”提示。
- **运行库选择**：移除旧的 310.1 选项，仅提供当前 310.9.16；旧游戏配置转为选用当前构建，保留原部署记录以供恢复，应用后才更新游戏文件。
- **绿色版构建**：仅提供 Windows x64 自包含单文件程序，移除重新加入的测试项目及发布测试门禁；手动构建使用项目内的版本号。

### 下载与升级

下载 `DLSSGManager.exe`，放入可写目录直接运行，无需安装 .NET。Mod 文件单独获取；下载或导入完成后，在游戏详情点击「应用到游戏」并重启游戏。已有游戏库与恢复记录继续使用。

完整包包含 INT8 超分文件；基础包仅包含插帧与 XeSS。取消或关闭下载窗口会等待当前操作结束，未经校验的负载不会发布到本地 Mod 目录。

### English

The download window now offers full/base packages, source speed results and automatic source ordering, plus pause, resume, skip and cancellation. Upstream ZIP packages can be imported by drag and drop or file selection, using the same payload and signature checks. Interrupted transfers attempt ranged resume, with a restart when the server does not support it.

Batch operations, game configuration state and anti-cheat presentation have moved into view models. The interface follows the Windows app theme when selected, and language changes refresh status details and selected combo labels. Older deployment snapshots normalize missing settings to their current defaults.

Only the self-contained Windows x64 portable executable is provided. Reintroduced test projects and release test gates have been removed. Existing libraries and restore records are preserved. Downloading or importing a Mod does not apply it to games automatically.

**完整改动 / Full changelog:** https://github.com/BUNNY-19C/DLSSG-30s-manager/compare/v1.10.0...v1.11.0
