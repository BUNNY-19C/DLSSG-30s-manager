## v1.11.0：适配上游 0.4.0

- **完整支持 0.4.0 负载布局**：优先下载官方完整包，部署代理、INI 与 `dlssg_sm86/` 运行目录；升级时清理已确认归属的多余代理，逐文件记录并支持恢复。
- **新增配置**：DLSS / XeSS 引擎、FG Lite 预设 C、XeSS 光流精度与无 HUD 输入、INT8 超分和逐帧 GPU 计时。新配置默认优化档位 2，保留已有游戏的档位。
- **下载与版本显示修复**：修复下载更新时游戏列表出现白色背景的问题；分别显示 Mod 发布版本与运行库构建，明确下载目标，避免下载后版本看似未变。
- **自动接管手工安装**：检查状态、启动刷新、扫描与添加游戏时，自动登记能识别的手工安装，移除手动接管按钮。已有管理记录与备份继续保留。
- **仅提供绿色版**：移除安装包、测试项目与冒烟门禁，统一使用自包含单文件发布配置。

### 下载与升级

| 文件 | 用法 |
|---|---|
| `DLSSGManager.exe` | Windows x64 免安装绿色版，自带 .NET，放入可写目录运行 |
| `SHA256SUMS.txt` | 程序文件的 SHA256 校验值 |

Mod 文件单独下载，不包含在发行程序中。下载更新只更新本地负载；选择游戏并点击「应用到游戏」后，重启游戏使其生效。原有游戏库与恢复记录继续使用。

上游 0.4.0 使用一个代理与完整运行目录，不要沿用旧版双代理方案。310.1 已停止下载，现有本地文件仍可使用与恢复；INT8 超分需要完整包中的 SR 文件。

### English

This release supports the upstream 0.4.0 payload layout, full-package downloads and per-file deployment/restoration of the runtime directory. It adds DLSS/XeSS selection, preset C, XeSS options, INT8 super resolution and per-frame GPU timing. New profiles default to optimization tier 2; existing tiers are preserved.

It fixes the white game-list background during downloads and clarifies release versus runtime versions. Recognized manual installations are adopted automatically during status checks, with the manual adoption button removed. Existing library entries and backups are preserved.

Only the self-contained Windows x64 portable executable is provided. Installer packaging, test projects and smoke gates have been removed. Mod files are downloaded separately; apply updated files to each game and restart it. Upstream 0.4.0 requires one proxy and its runtime directory. Existing local 310.1 payloads remain usable and restorable, but are no longer downloaded.

**完整改动 / Full changelog:** https://github.com/BUNNY-19C/DLSSG-30s-manager/compare/v1.10.0...v1.11.0
