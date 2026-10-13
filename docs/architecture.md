# Architecture

当前调度与执行链路：

```text
Eligibility Core
    ↓
SchedulePlan
    ↓
Windows Task Scheduler
    ↓
Runner
    ↓
Claim / Evaluation
    ↓
Official Codex CLI
```

Core 根据本地状态和 eligibility policy 决定是否允许本地尝试，以及下一次计划尝试时间。SchedulePlan 只把 decision 转为零个或一个 Scheduler trigger。

**Windows Task Scheduler 不是授权者。** 它只在指定时间唤醒 Runner。Runner 启动后验证任务状态，再通过共享 mutex/claim 调用 Core 重新评估；Scheduler 的时间不会绕过 claim 或 eligibility。

## Component ownership

- **App**：WPF 界面、用户设置、Test Now 确认、托盘入口及 Scheduler 生命周期管理；展示 Core 提供的 decision。
- **Eligibility Core**：拥有本地尝试 eligibility、decision reason、confidence 和下一次计划尝试计算。
- **SchedulePlan / SchedulingCoordinator**：把 Core decision 映射为 Scheduler 可执行的 one-shot 计划，不解释 quota 或 usage。
- **Windows Task Scheduler**：按 SchedulePlan 唤醒 Runner，验证任务配置，并使用 `IgnoreNew` 处理重叠实例。
- **Runner**：验证运行条件，通过共享 claim/mutex 重新执行 eligibility 检查；只有 claim 成功后才调用 CLI。
- **Official Codex CLI**：使用用户现有的官方登录状态处理真实请求。

旧固定 slots 保留为诊断和迁移数据，不生成 Scheduler trigger，也不是当前调度权威。

## v0.1 limits

本地计划时间不是 Codex 服务端额度 reset 时间。应用外的 Codex 使用不可观察；v0.1 不调用私有 API、不预测 reset，也不判断 weekly quota。已观察 usage limit 且没有可信 reset 时间时，自动调度不创建 trigger。
