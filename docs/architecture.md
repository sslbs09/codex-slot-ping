# Architecture

Codex Slot Ping 由 App、Core、Runner、Windows Task Scheduler 和官方 Codex CLI 组成。

- **App** 提供 WPF 交互、设置和托盘入口。App 通过 Windows Task Scheduler 管理本项目的任务，并读取任务状态。
- **Core** 保存可测试的 domain logic，包括配置验证、slot eligibility、执行状态和调度契约。
- **Windows Task Scheduler** 按配置启动 Runner。App 关闭后，已启用的任务仍可按计划启动。
- **Runner** 在启动后检查运行条件和当前 slot，记录 claim，再调用官方 Codex CLI 发出真实请求。
- **Codex CLI** 使用用户已有的官方登录状态处理请求。

项目通过官方 CLI 发出真实请求，不使用私有 API，也不读取、复制或修改 Codex 认证文件。
