## v1.10.0：游戏配置与构建管理

- **保存与应用分开**：状态卡区分未保存、已保存待应用和已应用。保存配置只写管理库，应用才修改游戏文件。
- **310.9 / 310.1 构建选择**：两套负载独立下载和保存；310.1 最高 4X、优化档位 0/1、Auto 预设。不支持的设置会提示，不会自动改写。
- **更新更明确**：分别显示本地和游戏已部署版本；下载不自动覆盖游戏，可勾选有更新的游戏后统一应用。
- **批量操作先勾选**：只处理当前可见的勾选项，逐项显示结果，可只重试失败项。隐藏的勾选项不会被普通批量操作处理。
- **保存失败可重试**：持续显示错误并保留内存记录；批量保存失败会停止后续项。关闭窗口时可等待操作收尾，下载可取消后退出。
- **界面整理**：游戏状态和主要动作置顶；名称搜索与状态筛选；高级设置、工具区、日志可折叠，日志折叠时保留最新结果摘要。
- **兼容提示**：荒野、绝区零、异环的目录与前置文件检查，可自动建议或手动选择。
- **构建切换**同步更新能匹配的备用代理，保留恢复记录；自定义备用代理无法匹配新构建时，提示先恢复再部署。

### 下载与升级

| 文件 | 用法 |
|---|---|
| `DLSSGManager-1.10.0-setup.exe` | 安装包，可选路径与语言，带卸载程序 |
| `DLSSGManager.exe` | 单文件绿色版，放到任意目录运行 |
| `SHA256SUMS.txt` | 两个程序文件的 SHA256 校验值 |

两个版本都包含 .NET 运行时。安装阶段不联网，Mod 文件在首次启动时自动获取，也可从顶部手动下载。升级保留原有游戏库与恢复记录；旧部署记录缺少配置快照时会显示待应用，原有恢复功能仍可使用。

310.9 保留原来的 `mod/` 目录，310.1 使用 `mod/variants/310.1/`。常规批量操作的范围从整个游戏库改为**可见且勾选的游戏**。

### 验证与使用注意

- 本地回归 480 项通过；界面冒烟覆盖保存重试、部署/恢复、搜索/筛选、批量失败重试与关闭流程；310.1 真实下载和验签通过。
- 游戏前置文件的存在检查不代表版本兼容或帧生成效果已验证，修改配置后需要重启游戏。
- 内核级反作弊可能拦截代理并带来账号风险。管理器会检测并提示，部署需要明确确认。
- 本工具负责部署与恢复；上游 Mod 文件单独下载，不包含在发行程序中。

## English

This release separates saving configuration from applying it to a game, adds independent 310.9 / 310.1 payload selection, and shows local and deployed versions separately. Batch operations process only visible checked games, report individual results, and can retry failed items. Save failures offer a persistent retry; closing waits for active work or cancels a download before exiting.

The interface adds search and status filters, collapsible tools, advanced settings and output, and guidance for Wilds, Zenless Zone Zero and NTE. Existing libraries and restore records are preserved; installations without a configuration snapshot show as pending application.

Choose the installer or portable executable. Both include .NET. Setup does not download the Mod; the manager fetches it on first start. Game compatibility and frame-generation results still require testing in the game.

**完整改动 / Full changelog:** https://github.com/BUNNY-19C/DLSSG-30s-manager/compare/v1.9.5...v1.10.0
