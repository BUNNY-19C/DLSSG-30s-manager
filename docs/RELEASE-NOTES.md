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
