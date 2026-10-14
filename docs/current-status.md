# Current Status: v0.2.0

## Release state

Release source: the validated `feat/v0.2-observability` line. It includes the App-owned Read Model, eligibility explanations, a Tray status summary, and Pause / Resume for automatic scheduling.

## Current scheduling behavior

Core eligibility computes the next planned attempt from local state and policy. SchedulePlan turns the decision into a one-shot trigger. Windows Task Scheduler only wakes Runner; it is not authorization. Runner rechecks claim and eligibility before dispatch. Legacy fixed slots remain only for diagnostics and migration.

Main, Settings, and Tray present status through the App-owned Read Model. Pause blocks automatic scheduling while retaining the enabled state; Disabled means automation is off. Resume calculates a fresh plan. Test Now remains a user-initiated manual path during Pause.

UI label: “下次计划尝试 / Next planned attempt”. It is a local-policy time, not “额度恢复时间 / quota reset time”.

## Known limitations

- Codex usage initiated outside this app is not observable.
- The app does not provide a complete execution history.
- If saving Pause fails, temporary protection lasts only for the current running app/Windows session; it does not survive an App or Windows restart.
- This is not a Task Runtime or Provider framework.

## Release verification

Core 157/157 and Integration 104/104 passed in Release configuration. The final self-contained win-x64 artifact, version metadata, and checksum were verified from the release commit. Manual Windows UI acceptance is recorded separately from automated test totals.
