# Scheduling Semantics v2

**Status: implemented for the v0.1 release candidate through M3-A, M3-B1, M3-B2, M3-B2.1, and M3-C.** This page records the current behavior; it is no longer a pending design proposal.

## Eligibility and local attempts

Eligibility Core owns local-attempt decisions and their explanation. It uses persisted attempt/success observations, unresolved claims, clock high-water protection, and explicit usage-limit observations. External manual Codex usage is unknown: that lowers confidence and is explained in the UI, but does not by itself permanently block local scheduling.

The displayed **Next planned attempt / 下次计划尝试** is calculated by local policy. It is not a statement that Codex service quota will reset or become available at that time.

An observed usage limit without a trusted reset time blocks automatic scheduling and has no next trigger. The application does not infer or predict a reset time. Test Now uses the same Core evaluator and claim; explicit confirmation permits one attempt but does not clear the automatic block.

## Dynamic Scheduler trigger

Core eligibility decision → SchedulePlan → Windows Task Scheduler → Runner → shared claim and re-evaluation → official Codex CLI.

An eligible decision plans a one-shot trigger. A future local eligibility time plans one one-shot trigger for that time. A blocked/unknown decision with no trusted future time produces no trigger. After wake-up—including a delayed wake after sleep—Runner re-evaluates current state; a missed trigger is not execution authorization.

Legacy fixed slots remain in configuration/state for diagnostics and migration only. They do not generate triggers and are not the scheduler authority.

## Explicitly outside v0.1

- Observing Codex usage made outside this application
- Private Codex API access
- Quota-reset prediction
- Weekly quota detection or determination
