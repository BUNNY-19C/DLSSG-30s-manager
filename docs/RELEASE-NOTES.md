## v1.13.0：更安全的部署与恢复、命令行版

这一版重写了管理器的核心，界面和使用方式基本不变。重点是：在任何情况下都不误删、不误改你游戏目录里的文件。

- **修复：冒名文件可能被当成本项目的文件。** 旧版只看签名证书的名字里有没有 "DLSSG"，别人自签同名证书的 DLL 会被直接覆盖（不备份）或在恢复时被删除。现在按文件内容（SHA-256）和证书指纹判定。
- **修复：游戏库文件损坏时，部署记录会被清空。** 旧版读不了 `library.json` 时会用空库覆盖它，之后就无法恢复已部署的游戏。现在会把损坏的文件另存、自动载入上一份备份（`library.json.bak`），并弹窗说明。
- **下载按版本清单逐文件校验。** 每个文件的大小和哈希都必须与 0.4.1 发布版完全一致，GitHub 官方源和镜像一视同仁，配置模板也在校验范围内。
- **Mod 按版本存放。** 新版本下载到 `mod\versions\<版本>\`，完整校验后才切换过去；下载失败或取消不会影响正在用的版本，上一版会保留以便回退。原来的 Mod 文件会在下次更新时自动整理进去。
- **只在需要时提权。** 游戏目录需要管理员权限时，会询问是否只为这一次操作提权，不再需要以管理员身份重启整个管理器。
- **断电、强退后自动收尾。** 部署或恢复进行到一半被中断，下次启动时会自动撤销未完成的部署、补完未完成的恢复；文件已写完但部署记录还没来得及保存的，也会从操作日志里找回。
- **防止多个进程同时改写游戏库。** 界面版和命令行版同时运行时，一方的部署或恢复会独占游戏库直到记录保存；拿着过期游戏库的另一方不会覆盖较新的记录，而是提示重新读取。
- **恢复更可靠。** 被改动过的备份不会被放回；手动安装的社区版代理在单代理部署前会先备份，恢复时放回。
- **下载更稳。** 瞬时网络错误自动重试；使用系统代理（如 Clash）时连接校验仍然有效。
- **新增命令行版 `dlssgm.exe`。** 可脚本化地列出、添加、检查、部署、恢复游戏，下载、导入、核对 Mod 文件，与界面版共用同一游戏库。
- **运行时升级到 .NET 10**（.NET 8 已于 2026-11-10 停止支持）。绿色版仍是自包含单文件，无需另装 .NET。

### 升级说明

直接替换 `DLSSGManager.exe` 即可，游戏库、恢复备份和已部署的游戏都继续有效。首次下载或导入 Mod 时，原 `mod\` 目录中的载荷文件会被整理进 `mod\versions\`，你放在那里的其他文件保持不动。

### English

The core was rewritten so the manager never removes or overwrites files it cannot prove it wrote. Ownership is now decided by file hash and pinned certificate thumbprint (previously by certificate name, which anyone can forge). An unreadable game library is set aside and the previous backup is loaded instead of being replaced by an empty one. Downloads are verified file-by-file against the 0.4.1 release manifest from every source, and each release installs into its own `mod\versions\<version>` folder, switched in only when complete. Administrator rights are requested for a single operation instead of restarting the whole manager elevated. Interrupted deployments are rolled back and interrupted restores completed on the next start; a record written to disk but not yet saved to the library is recovered from the journal. The library is locked for the duration of a deploy or restore, and a process holding an outdated copy cannot overwrite a newer one. A command-line tool, `dlssgm.exe`, shares the same library. The runtime moves to .NET 10; the portable build remains a single self-contained executable.

**完整改动 / Full changelog:** https://github.com/BUNNY-19C/DLSSG-30s-manager/compare/v1.12.0...v1.13.0

---

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
