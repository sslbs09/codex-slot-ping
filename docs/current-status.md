# Current Status

## 基线

产品代码基线：`7ba0abf11ca9c865b1b7f3adc1bd91e2e1ba17ab`（`feat: localize desktop shell and expose settings shortcuts`）。本次文档提交会在此基线上追加，不改变产品功能。

## 已实现

- Windows WPF / .NET 10，self-contained win-x64
- 托盘、single-instance 和 Settings
- 固定四个本地时间 slot 的 scheduler baseline
- Test Now 真实请求确认门
- 简体中文 shell 与 English fallback
- 结构化执行状态

## 验证记录

- Core tests：105/105 PASS
- Integration tests：57/57 PASS
- 合计：162/162 PASS
- self-contained win-x64 Release publish：PASS

## 尚未完成

- Scheduling Semantics v2
- 动态 5h usage-window
- 手动 Codex 使用观察和窗口漂移处理
- weekly quota exhaustion handling

## 验收和安装状态

人工验收：本地化 build 的最后一轮完整验收尚未完成。
真实请求：E2E 尚未执行。
Scheduler：尚未安装到当前机器。
发布：尚未创建 GitHub Release，也没有公开成品版本。
