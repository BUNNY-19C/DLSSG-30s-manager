# 上游 0.4.1 适配

核对日期：2026-10-27。依据：[官方 Release 0.4.1](https://github.com/sdli1995/dlssg_for_sm86/releases/tag/0.4.1)、该标签的 INI、README 与安装说明；发布提交短标识为 `44f4006`。管理器版本为 1.12.0。

## 适配内容

- 固定下载 0.4.1 标签与 `dlssg-release-0.4.1-x64.zip`；运行目录布局不变，保持现有签名验证及通过校验后安装的流程。
- 实际官方 ZIP 和标签中的代理由 `CN=DLSSG Throwaway Test` 签名，证书指纹 `D963A96D623B85F67B7B323BEB353CEE90225D40`，有效期至 2026-11-26；上游 README 仍写旧证书。增加该已核实的精确指纹，保留旧证书兼容；签名完整性、NVIDIA 信任校验和未知镜像签名拒绝规则继续生效，不导入系统信任证书。
- XeSS 改为独立的 `XeSSMaxGeneratedFrames`（1–3，即 2X/3X/4X）。缺少该字段的旧配置保持原来的 2X；DLSS 倍率不受引擎切换影响。
- 光流新增 `CoarseAt4K`；无 HUD 输入由布尔开关改为 `HudlessMode` 的 Auto/1/0。旧 true 表示“有就用”，迁移为 Auto；false 迁移为 0。
- 超分由布尔开关改为 `SuperResolutionMode`，支持 Off、M-INT8、L-INT8、M-FP16、L-FP16。旧 true 仍读取为旧 On，应用 0.4.1 时要求明确选择；不猜测用户想要 M/L 中哪种画质。旧 0.4.0 负载仍可使用 On/Off。
- 新游戏默认优化档位 1、预设 A。已有明确存储的档位不变；旧 Auto 在 0.4.1 写成 A，旧负载仍保留 Auto 语义。
- 持久化与应用快照继续读取旧布尔字段，写入新格式时省略旧字段。复制配置与状态判断使用新字段。
- 0.4.1 的倍率是必须满足的请求，XeSS 不再固定写 1；Runtime Mode 与 SuperResolution Mode 继续按节分别处理。
- 说明 NVIDIA R580+ 硬性驱动要求、RGBA16F 不回退 DLSS、旧游戏插件可能拒绝启用，以及超分显式模式可能影响所有画质档的行为。
- 日志入口优先选择 `dlssg_sm86/logs/dlssg_sm86.log`，兼容旧 JSONL；逐帧计时另写 `frame_timing.txt`。

## 验证边界

使用必要的 Release/Portable 构建和实际生产代码的配置渲染、旧字段读取核对；不新增测试项目或冒烟门禁。完整包导入验证与界面核对结果随最终交付记录。真实游戏内帧生成与性能仍需对应游戏和驱动环境验证。
