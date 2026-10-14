# Scheduling Semantics v2

**Status: current behavior for v0.2.0.** This page records implemented scheduling behavior; it is not a pending design proposal.

## Eligibility and local attempts

Eligibility Core owns local-attempt decisions and their explanation. It uses persisted attempt/success observations, unresolved claims, clock high-water protection, and explicit usage-limit observations. External manual Codex usage is unknown: that lowers confidence and is explained in the UI, but does not by itself permanently block local scheduling.

The displayed **Next planned attempt / 下次计划尝试** is calculated by local policy. It is not a statement that Codex service quota will reset or become available at that time.

An observed usage limit without a trusted reset time blocks automatic scheduling and has no next trigger. The application does not infer or predict a reset time. Test Now uses the same Core evaluator and claim; explicit confirmation permits one attempt but does not clear the automatic block.

## Dynamic Scheduler trigger

Core eligibility decision → SchedulePlan → Windows Task Scheduler → Runner → shared claim and re-evaluation → official Codex CLI.

An eligible decision plans a one-shot trigger. A future local eligibility time plans one one-shot trigger for that time. A blocked/unknown decision with no trusted future time produces no trigger. After wake-up—including a delayed wake after sleep—Runner re-evaluates current state; a missed trigger is not execution authorization.

Legacy fixed slots remain in configuration/state for diagnostics and migration only. They do not generate triggers and are not the scheduler authority.

## Pause and manual Test Now

Pause blocks automatic scheduling while leaving automation enabled. Resume recalculates and writes a fresh plan. Test Now remains an explicit manual request during Pause; it may count toward Codex usage.

If persisting Pause fails, temporary fail-closed protection applies only while the current App/Windows session remains active. It does not survive an App or Windows restart.

## Known limitations

- Observing Codex usage made outside this application
- Complete execution history
- Task Runtime or Provider framework behavior
- Private Codex API access
- Quota-reset prediction
- Weekly quota detection or determination
