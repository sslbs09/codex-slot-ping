# Codex Slot Ping

Windows 托盘工具，通过已安装并登录的官方 Codex CLI 发起计划中的真实请求。请求可能计入 Codex 使用额度。

## v0.1 当前行为

- Core eligibility 根据本地状态和调度策略计算**下次计划尝试**（Next planned attempt）。这表示本地计划时间，不表示 Codex 额度恢复时间。
- Core 将 decision 转成 SchedulePlan；Windows Task Scheduler 只在计划时间唤醒 Runner，不负责授权请求。
- Runner 启动后检查任务状态，并通过共享 claim/mutex 重新执行 eligibility 检查。只有 claim 成功才会调用官方 Codex CLI。
- 旧固定 slots 只为诊断和迁移保留，不再生成 Scheduler trigger。
- Test Now 使用共享 eligibility 和 claim。遇到没有可信 reset 时间的已观察 usage limit 时，需要用户明确确认一次尝试；确认不会解除自动调度阻断。

界面文案使用“下次计划尝试 / Next planned attempt”，不表示“额度恢复时间 / quota reset time”。计划依据本地调度策略计算，无法观察此应用之外的 Codex 使用。

## 架构

Eligibility Core → SchedulePlan → Windows Task Scheduler → Runner → Claim/Evaluation → Codex CLI

Scheduler 只负责唤醒 Runner；Scheduler trigger 本身不是执行许可。详情见[架构说明](docs/architecture.md)。

## v0.1 限制

v0.1 不会：

- 观察此应用之外的 Codex 使用
- 访问私有 API
- 预测额度 reset 时间
- 判断 weekly quota 是否耗尽

如果观察到 usage limit 且没有可信 reset 时间，自动调度保持阻断，不安排下一次自动 trigger。

## 验证记录

Release candidate 的既有自动化测试记录：Core 152/152、Integration 78/78、总计 230/230 PASS。该结果来自先前验证；本次文档同步没有重新运行测试。self-contained win-x64 Release publish 也有先前的 PASS 记录。

## 文档

- [当前状态](docs/current-status.md)
- [架构说明](docs/architecture.md)
- [Scheduling Semantics v2](docs/scheduling-v2.md)
- [原始 v0.1 设计快照](docs/superpowers/specs/2026-10-11-codex-slot-ping-v0.1-design.md)：历史资料，不代表当前调度行为。
