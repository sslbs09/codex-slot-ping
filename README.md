# Codex Slot Ping

**Current release: v0.2.0**

Windows 托盘工具，通过本机已安装并登录的官方 Codex CLI 发起计划请求。真实请求可能计入 Codex 使用额度。

## v0.2.0 当前行为

- App-owned Read Model 汇总状态；Main、Settings 和 Tray 使用一致的状态与说明。
- Eligibility explainability 展示自动尝试会执行或被阻止的原因。
- Tray 提供调度状态摘要和 Pause / Resume 入口。
- Pause 暂停自动调度但保留启用状态；Disabled 表示自动化未启用。恢复时重新计算计划。
- Test Now 在 Pause 期间仍是用户显式发起的手动路径，可能产生真实 Codex 请求。
- Core 根据本地状态和调度策略计算**下次计划尝试**（Next planned attempt）。这是计划时间，不是 Codex 额度恢复时间。
- Windows Task Scheduler 只在计划时间唤醒 Runner；Runner 通过共享 claim/mutex 重新执行 eligibility 检查，只有 claim 成功才会调用官方 Codex CLI。
- 旧固定 slots 仅用于诊断和迁移，不再生成 Scheduler trigger。

如果保存 Pause 状态失败，当前会话会临时阻止自动尝试；App 或 Windows 重启后，该保护不再保留，需要重新确认暂停状态。

## 架构

Eligibility Core → SchedulePlan → Windows Task Scheduler → Runner → Claim/Evaluation → Codex CLI

Scheduler 只负责唤醒 Runner；Scheduler trigger 本身不是执行许可。详情见[架构说明](docs/architecture.md)。

## 已知限制

- 应用外手动使用 Codex 不可观测；本地计划时间不代表额度恢复时间。
- 应用没有完整的 execution history。
- 保存 Pause 状态失败时的临时保护不跨 App 或 Windows 重启。
- 本应用不是 Task Runtime 或 Provider framework。

## 验证

发布收口验证：Core 157/157、Integration 104/104；自包含 win-x64 发布包和元数据核对结果见 [v0.2.0 release notes](docs/releases/v0.2.0.md)。

## 文档

- [当前状态](docs/current-status.md)
- [架构说明](docs/architecture.md)
- [Scheduling Semantics v2](docs/scheduling-v2.md)
- [v0.2.0 Release Notes](docs/releases/v0.2.0.md)
- [原始 v0.1 设计快照](docs/superpowers/specs/2026-10-11-codex-slot-ping-v0.1-design.md)：历史资料，不代表当前调度行为。
