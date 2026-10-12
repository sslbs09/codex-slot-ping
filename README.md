# Codex Slot Ping

Windows 托盘工具，可在明确配置的时间通过官方 Codex CLI 发起极小的真实请求，并管理计划任务、执行状态、日志和 fail-closed 控制。真实请求可能计入 Codex 使用额度。

A small Windows tray utility for scheduled Codex CLI requests.

The project is in early development, with no public release yet.

## 当前状态

**Early development / pre-release。** 当前版本仍是固定四个本地时间 slot 的 baseline，尚未完成动态调度和最终验收。

已完成：

- WPF desktop shell、托盘和 single-instance
- 本地 Settings 与 Test Now 确认门
- Windows Task Scheduler baseline
- 结构化执行状态
- 简体中文界面和 English fallback

未完成：

- Scheduling Semantics v2
- 动态 usage-window 调度
- weekly quota 处理和手动 Codex 使用观察
- 最终人工验收与真实请求 E2E

## 当前工作方式

当前 baseline 使用四个明确的本地时间：07:00、12:01、17:01、22:01。每个 slot 独立判断 eligibility；错过的 slot 不补跑。Test Now 会先显示确认门。固定四 slot 只是当前实现，后续计划改为动态调度。

## 安全边界

- 不绕过 Codex 服务端限制，也不修改 Codex 配额。
- 不调用私有 API；真实请求由官方 Codex CLI 发出。
- 真实请求可能计入 Codex 使用额度。
- eligibility 或运行状态无法确认时，按 fail-closed 处理。
- Scheduler 由用户明确启用、停用或移除。

## 技术结构

- `CodexSlotPing.App`：WPF 界面、设置、托盘和计划任务管理。
- `CodexSlotPing.Core`：配置、slot eligibility、状态和可测试的 domain logic。
- `CodexSlotPing.Runner`：由 Windows Task Scheduler 启动，执行真实 Codex 请求。

## Development

Windows、WPF、.NET 10，依赖已安装并登录的官方 Codex CLI。当前项目测试结果：Core 105/105，Integration 57/57，共 162 项通过；self-contained win-x64 Release publish 已通过。

## 文档

- [架构说明](docs/architecture.md)
- [当前状态](docs/current-status.md)
- [Scheduling Semantics v2 设计意图](docs/scheduling-v2.md)

## Roadmap

- Scheduling Semantics v2
- 动态 5h usage-window
- 手动使用导致的窗口漂移处理
- weekly quota exhaustion 处理
- 最终人工验收与 live E2E
- packaging / release

## Status

尚无公开版本，也没有发布 GitHub Release。
