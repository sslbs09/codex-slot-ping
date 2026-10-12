# Scheduling Semantics v2

**Design intent only.** 本文记录待解决的问题，不决定最终架构，也不代表功能已经实现。

当前固定时间为 07:00、12:01、17:01、22:01。它们只是 scheduler baseline，无法表达真实使用窗口的变化。

后续设计需要处理：

1. 5h usage-window 可能随有效使用时间漂移。
2. 用户可能在工具之外手动使用 Codex。
3. `attemptedAt` 与 `successful/effectiveUsageAt` 必须区分，不能默认一次尝试就产生有效使用。
4. `nextEligibleAt` 需要根据可用状态动态推导。
5. weekly quota 耗尽时应暂停或 fail closed；具体状态和恢复策略待定。
6. 只有 reset time 的来源可信时，才能据此安排下一次请求。
7. 如果无法可靠观察手动使用，应明确标记状态为 uncertain，不能伪装成精确时间。
8. UI 应能解释为什么下一次会在该时间触发。

具体状态模型、数据来源和最终调度算法仍待设计。

Status:
Design pending.
