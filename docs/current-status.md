# Current Status

## Frozen release candidate

产品代码冻结基线：`11f5ab992a30fe6b9ee978bad9f159aa26301218` (`feat: schedule one-shot triggers from eligibility`)。

- M3-A Eligibility Core：complete
- M3-B1 State v2 migration + FormalSlotClaim integration：complete
- M3-B2 Test Now shared eligibility + UI decision display：complete
- M3-B2.1 External usage uncertainty semantics correction：complete
- M3-C Dynamic Scheduler Trigger：complete

## Current scheduling behavior

Core eligibility computes the next planned attempt from local state and policy. SchedulePlan turns the decision into a one-shot trigger. Windows Task Scheduler only wakes Runner; it is not authorization. Runner rechecks claim and eligibility before dispatch. Legacy fixed slots remain only for diagnostics and migration.

UI label: “下次计划尝试 / Next planned attempt”. It is a local-policy time, not “额度恢复时间 / quota reset time”.

## Recorded verification

- Core：152/152 PASS
- Integration：78/78 PASS
- Total：230/230 PASS
- self-contained win-x64 Release publish：PASS

These are existing release-candidate results. They were not rerun for the documentation sync.

## v0.1 limits

v0.1 does not observe external Codex usage, access private APIs, predict quota reset, or determine weekly quota. External usage is unknown and affects confidence/explanation. An observed usage limit without a trusted reset blocks automatic scheduling and yields no planned trigger.

Manual Windows UI/tray acceptance and live Codex request E2E are separate from the automated test totals and are not asserted by this status record.
