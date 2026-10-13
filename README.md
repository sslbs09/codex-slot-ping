# Codex Slot Ping

Codex Slot Ping 是一个 Windows 托盘工具和官方 Codex CLI 调度辅助程序。它根据本地观察与调度策略安排请求尝试，并通过官方 Codex CLI 发起真实请求；请求可能计入 Codex 使用额度。

当前发布版本是 [v0.1.0](https://github.com/sslbs09/codex-slot-ping/releases/tag/v0.1.0)。对应源代码见 [v0.1.0 tag](https://github.com/sslbs09/codex-slot-ping/tree/v0.1.0)。

## Features

- **Core-owned eligibility**：Core 根据本地持久化的观察和调度策略决定是否允许一次尝试。
- **Dynamic one-shot scheduling**：Core 为下次计划尝试生成零个或一个 `SchedulePlan`；调度不再依赖固定周期 slots。
- **Shared Test Now path**：Test Now 与正式调度共享 eligibility 和 claim 路径。用户确认的单次 Test Now 不会解除自动调度阻断。
- **Windows Task Scheduler integration**：Task Scheduler 仅在计划时间唤醒 Runner，不负责授权请求。

## How it works

`Core decision → SchedulePlan → Windows Task Scheduler → Runner → Claim / eligibility re-check → official Codex CLI`

Core 计算 eligibility 并生成计划；Task Scheduler 按计划唤醒 Runner。Runner 检查任务状态，通过共享 claim 再次校验 eligibility；只有 claim 成功后才调用官方 Codex CLI。

## Limitations

- 不使用私有 API。
- 不预测 Codex quota reset。
- 不读取此应用之外的 Codex 使用情况。
- 不判断 weekly quota 是否耗尽。
- 观察到 usage limit 且没有可信 reset 时，自动调度阻断，不创建下一次 trigger。
- “下次计划尝试”是本地策略计算结果，不代表额度恢复时间。

## Installation

1. 下载 [v0.1.0 Windows x64 Release ZIP](https://github.com/sslbs09/codex-slot-ping/releases/download/v0.1.0/CodexSlotPing-v0.1.0-win-x64.zip) 并解压。
2. 确保本机已安装并登录官方 Codex CLI。
3. 运行 `CodexSlotPing.App.exe`，配置并启用计划任务。

## Development

- Windows、WPF、.NET 10。
- 运行测试：`dotnet test CodexSlotPing.sln --no-restore`
- v0.1.0 验证：Core 152/152、Integration 78/78，共 230 tests passed。

## Documentation

- [Architecture](https://github.com/sslbs09/codex-slot-ping/blob/v0.1.0/docs/architecture.md)
- [Current status](https://github.com/sslbs09/codex-slot-ping/blob/v0.1.0/docs/current-status.md)
- [Scheduling Semantics v2](https://github.com/sslbs09/codex-slot-ping/blob/v0.1.0/docs/scheduling-v2.md)
