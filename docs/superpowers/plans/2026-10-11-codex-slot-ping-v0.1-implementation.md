# Codex Slot Ping v0.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the frozen v0.1 Windows x64 portable tray utility that runs one Codex CLI prompt at each of four strict daily slots with durable at-most-once dispatch.

**Architecture:** Keep exactly three product projects: WPF App, platform-neutral Core, and a short-lived Runner started directly by Task Scheduler. Core owns shared rules and I/O; App owns presentation and user actions; Runner is the only formal-slot dispatcher. Inject only the clock, time zone, storage, mutex, process launcher, environment detector, and scheduler gateway needed by tests.

**Tech Stack:** C# on .NET 10; WPF; Windows Task Scheduler COM; xUnit for automated tests; self-contained Windows x64 publishing.

**Spec:** `docs/superpowers/specs/2026-10-11-codex-slot-ping-v0.1-design.md`

## Global Constraints

- Target Windows x64; distribute a self-contained portable ZIP; users do not install .NET separately.
- Product projects are `CodexSlotPing.App`, `CodexSlotPing.Core`, and `CodexSlotPing.Runner`. Core has no WPF dependency.
- Store user data under `%LOCALAPPDATA%\CodexSlotPing`; keep portable binaries separate from config, state, and logs.
- Accept exactly four unique 24-hour `HH:mm` slots, each at least six minutes apart, including midnight wrap.
- `strictSlotWindowMinutes` is fixed at 3; admission is the half-open interval `slot start <= admission time < slot start + 4 minutes`.
- The timezone default is `SYSTEM`. An explicit Windows timezone ID must equal the current Windows timezone ID for install and dispatch; mismatch fails closed.
- Missed slots are never replayed. Persist `CLAIMED` before process start. Every claimed identity is terminal for automatic scheduling, including `PRELAUNCH_FAILED`.
- Use one named per-user state mutex and same-directory temp-write, flush, and atomic replace for config and state.
- The owned Task Scheduler task is named `Codex Slot Ping` and has the exact ownership marker `Codex Slot Ping; Owner=CodexSlotPing; Schema=1`.
- Register four local daily triggers with `StartWhenAvailable=false`, `MultipleInstancesPolicy=IgnoreNew`, `ExecutionTimeLimit=PT2M`, `RestartCount=0`, `RunLevel=LeastPrivilege`, and an interactive-user principal. `WakeToRun` defaults to false.
- Task Scheduler launches the absolute Runner executable directly, with its directory as working directory. Do not use PowerShell, cmd.exe, a script wrapper, a daemon, or an elevated task.
- Launch the existing directly executable Codex CLI through `ProcessStartInfo` and `ArgumentList`, no shell; use the read-only sandbox, no interactive approval, ephemeral JSONL mode, stdin prompt, and a 100-second child limit.
- Keep ChatGPT sign-in in the existing Codex CLI context. Do not store, copy, show, or transmit credential values; do not switch to an API key, alternate provider, or fallback.
- Test Now is a confirmed real request with a separate ID. It may count toward usage and never claims or changes a formal slot.
- Structured logs omit the full prompt, response, and secrets. Raw logs default off, require a notice, and expire after 14 days.
- Do not edit the user's Codex skills. Do not add a generic scheduler, provider, plugin, MVVM, dependency-injection, or event-bus framework.
- Do not ship an installer, MSI, Winget package, code signing, or automatic updates.
- v0.2/v0.3 candidates in the spec remain outside this implementation.

## Review Focus

- Two Runner processes race for one eligible slot — Task 10 test `TwoRunnerProcesses_OnlyOneClaimsAndLaunches`.
- Portable folder moves after task registration — Task 9 test `MovedRunnerPath_ShowsNeedsRepair_AndRepairUpdatesOwnedAction`.
- Windows timezone changes after configuration — Task 9 test `ChangedWindowsTimeZone_FailsClosedUntilConfirmedUpdate`.
- Codex emits stderr warnings while JSONL confirms success — Task 7 test `StderrSkillYamlWarnings_DoNotOverrideJsonlSuccess`.
- State write is interrupted or the state document is corrupt — Task 4 test `AtomicReplaceFailure_PreservesPriorStateAndBlocksDispatch`.

## Planned File Layout

- `CodexSlotPing.sln` and `Directory.Build.props` hold the solution and shared compiler settings.
- `src/CodexSlotPing.Core/` contains configuration, time/slots, state/persistence, concurrency, Codex CLI, logging, environment, and Task Scheduler contracts/implementation.
- `src/CodexSlotPing.Runner/` contains argument parsing, formal-slot orchestration, and the process entry point.
- `src/CodexSlotPing.App/` contains WPF windows, App controller, tray integration, and single-instance activation.
- `tests/CodexSlotPing.Core.Tests/` contains deterministic unit and file-fault tests.
- `tests/CodexSlotPing.IntegrationTests/` contains Runner/App orchestration tests and Windows-only Task Scheduler COM tests.
- `tests/CodexSlotPing.FakeCodex/` is one small console fixture that acts as a direct child executable. It avoids a shell, a production test switch, and live account usage; it is the only extra support project.
- `docs/implementation/codex-cli-contract.md` records sanitized CLI compatibility evidence; it never records local auth data or machine-specific executable paths.
- `.github/workflows/windows.yml` builds/tests on Windows and produces tagged release assets.

## Shared Core Names

Use these names and meanings throughout the plan; do not rename them between tasks.

- `SlotDefinition(int Position, TimeOnly LocalTime)`; positions are 1 through 4 in displayed chronological order.
- `SlotId(string WindowsTimeZoneId, DateOnly LocalDate, TimeOnly LocalTime)`; trigger array order is not identity.
- `FormalSlotStatus` values: `PENDING`, `MISSED`, `CLAIMED`, `CONFIRMED_SUCCESS`, `ATTEMPTED_UNCONFIRMED`, `PRELAUNCH_FAILED`.
- `SlotAdmissionResult(SlotAdmissionKind Kind, FormalSlotStatus? FormalStatus, SlotId? SlotId, int? Position, AdmissionBlockReason? BlockReason)`. `SlotAdmissionKind` is `Eligible`, `Pending`, `Missed`, or `Blocked`.
- `CodexInvocationResult` carries `Classification`, `ProcessStarted`, nullable `ExitCode`, `ElapsedMilliseconds`, token usage when present, warning categories, and an optional raw-log path; it never carries prompt/response text.
- `EnvironmentStatus` carries direct-executable discovery, absolute path, CLI version, explicit ChatGPT-login result, supported reasoning values, and diagnostic codes; it contains no credential values.
- `SchedulerStatus` carries `State`, existence/ownership/enabled flags, registered and expected Runner paths, and configured/current Windows timezone IDs. `State` is `OFF`, `ON`, `CONFLICT`, `NEEDS_REPAIR`, `TIMEZONE_CHANGED`, or `ERROR`.
- `TestRunResult` carries a distinct test run ID, `CodexInvocationResult`, and completion time; it contains no formal `SlotId`.
- `AppConfig` mirrors schema version 1 with camel-case JSON fields: `schemaVersion`, `enabled`, `slots`, `timeZone`, `model`, `reasoningEffort`, `prompt`, `wakeToRun`, `strictSlotWindowMinutes`, and `captureRawLogs`.
- `IClock.UtcNow` returns one UTC instant per Runner admission attempt. `ITimeZoneProvider` supplies the current Windows timezone ID and resolves a Windows timezone ID to `TimeZoneInfo`.

---

### Task 1: Solution Skeleton and Codex CLI Contract Capture

**Files:**

- Create: `CodexSlotPing.sln`
- Create: `Directory.Build.props`
- Create: `src/CodexSlotPing.Core/CodexSlotPing.Core.csproj`
- Create: `src/CodexSlotPing.Runner/CodexSlotPing.Runner.csproj`
- Create: `src/CodexSlotPing.App/CodexSlotPing.App.csproj`
- Create: `src/CodexSlotPing.App/App.xaml`
- Create: `src/CodexSlotPing.App/App.xaml.cs`
- Create: `src/CodexSlotPing.App/MainWindow.xaml`
- Create: `src/CodexSlotPing.App/MainWindow.xaml.cs`
- Create: `tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj`
- Create: `tests/CodexSlotPing.IntegrationTests/CodexSlotPing.IntegrationTests.csproj`
- Create: `tests/CodexSlotPing.FakeCodex/CodexSlotPing.FakeCodex.csproj`
- Create: `tests/CodexSlotPing.FakeCodex/Program.cs`
- Create: `docs/implementation/codex-cli-contract.md`

**Interfaces:**

- No product behavior is introduced in this task. FakeCodex accepts a fixture mode and emits deterministic stdout/stderr/exit behavior for later tests.

- [ ] **Step 1: Recheck the invocation evidence before scaffolding.** Read the installed command's version and help, official non-interactive/command/config documentation, and private prototype files `E:\automation\luna-slot-ping\setup.ps1` and `common.ps1` as read-only references. Do not run a real prompt, modify the prototype, or inspect/copy authentication data. Record version, direct-executable resolution, supported options, stdin behavior, and differences from the frozen spec in `docs/implementation/codex-cli-contract.md`.

  Planning baseline to recheck at implementation time: the command shim resolves to npm Codex CLI 0.153.4 and its associated native executable exists; a separate 0.162.0-alpha.17.2 executable is also present. The prototype uses the native binary and `ArgumentList`, but passes prompt as an argument and uses `on-request` approval. Frozen spec values—stdin prompt, 100-second timeout, and no interactive approval—govern the product. Do not silently select the separate alpha binary.

  References: [Codex non-interactive mode](https://developers.openai.com/codex/non-interactive-mode), [Codex CLI command reference](https://learn.chatgpt.com/docs/developer-commands?surface=cli), [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

- [ ] **Step 2: Create the six-project solution skeleton.** Use .NET 10; target Windows for App, Runner, and Windows-specific tests; keep Core free of WPF references. Add only test packages needed by xUnit. Keep FakeCodex a plain console fixture, not a product project.

- [ ] **Step 3: Build the empty solution.** Run: `dotnet build CodexSlotPing.sln -c Release`. Expected: all six projects compile; the solution contains exactly three product projects, two test projects, and the one named FakeCodex fixture.

- [ ] **Step 4: Commit this task.** `git add CodexSlotPing.sln Directory.Build.props src tests docs/implementation/codex-cli-contract.md` then commit as `chore: scaffold Codex Slot Ping solution`.

---

### Task 2: Configuration Model, Defaults, Time Services, and Validation

**Files:**

- Create: `src/CodexSlotPing.Core/Configuration/AppConfig.cs`
- Create: `src/CodexSlotPing.Core/Configuration/AppConfigDefaults.cs`
- Create: `src/CodexSlotPing.Core/Configuration/ConfigValidator.cs`
- Create: `src/CodexSlotPing.Core/Configuration/ConfigValidationResult.cs`
- Create: `src/CodexSlotPing.Core/Time/IClock.cs`
- Create: `src/CodexSlotPing.Core/Time/SystemClock.cs`
- Create: `src/CodexSlotPing.Core/Time/ITimeZoneProvider.cs`
- Create: `src/CodexSlotPing.Core/Time/WindowsTimeZoneProvider.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Configuration/ConfigValidatorTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Time/WindowsTimeZoneProviderTests.cs`

**Interfaces:**

- Consumes: Task 1 project references and supported reasoning-value evidence recorded in `docs/implementation/codex-cli-contract.md`.
- Produces: `ConfigValidator.Validate(AppConfig candidate, string currentWindowsTimeZoneId, IReadOnlySet<string> supportedReasoningEfforts) -> ConfigValidationResult`; `IClock.UtcNow`; `ITimeZoneProvider.CurrentWindowsTimeZoneId` and `Resolve(string windowsTimeZoneId) -> TimeZoneInfo`. The App calls the same Core validator; there is no second UI rule set.

- [ ] **Step 1: Write config tests first.** Assert exact defaults: slots `07:00`, `12:01`, `17:01`, `22:01`; timezone `SYSTEM`; model `gpt-5.6-luna`; reasoning `low`; prompt `Hi.`; `wakeToRun=false`; `strictSlotWindowMinutes=3`; `captureRawLogs=false`. Cover exactly four slots, valid `HH:mm`, duplicates, under-six-minute gaps including midnight wrap, a valid nonempty model string without an account-availability claim, reasoning accepted only from the supplied CLI set, prompt trimming and 200 Unicode scalar limit (including astral characters), newline acceptance, unsupported strict-window rejection, default Windows timezone resolution, and explicit timezone match/mismatch.

- [ ] **Step 2: Run the focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~ConfigValidatorTests`. Expected: FAIL because model/defaults/validator/time services are not implemented.

- [ ] **Step 3: Implement the model, time services, defaults, and validator.** Parse slot strings as 24-hour times, check every adjacent gap after chronological sort and across midnight, treat `SYSTEM` as the current Windows timezone ID, reject an explicit ID mismatch, and preserve model strings as selection requests. Do not make the three-minute window editable.

- [ ] **Step 4: Rerun focused tests.** Expected: PASS for all defaults, boundaries, and invalid-input cases.

- [ ] **Step 5: Commit this task.** `git add src/CodexSlotPing.Core/Configuration src/CodexSlotPing.Core/Time tests/CodexSlotPing.Core.Tests/Configuration tests/CodexSlotPing.Core.Tests/Time`; commit as `feat: validate Codex Slot Ping configuration`.

---

### Task 3: Slot Identity, Admission, DST, and Clock Rollback

**Files:**

- Create: `src/CodexSlotPing.Core/Slots/SlotDefinition.cs`
- Create: `src/CodexSlotPing.Core/Slots/SlotId.cs`
- Create: `src/CodexSlotPing.Core/Slots/FormalSlotStatus.cs`
- Create: `src/CodexSlotPing.Core/Slots/SlotAdmissionResult.cs`
- Create: `src/CodexSlotPing.Core/Slots/SlotAdmissionEvaluator.cs`
- Create: `src/CodexSlotPing.Core/Slots/ClockRollbackGuard.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Slots/SlotAdmissionEvaluatorTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Slots/ClockRollbackGuardTests.cs`

**Interfaces:**

- Consumes: `AppConfig` and `ITimeZoneProvider` from Task 2.
- Produces: `SlotAdmissionEvaluator.Evaluate(AppConfig config, DateTimeOffset nowUtc, string currentWindowsTimeZoneId) -> SlotAdmissionResult`; `ClockRollbackGuard.Evaluate(DateTimeOffset nowUtc, DateTimeOffset? highestObservedUtc) -> ClockRollbackResult`.
- `ClockRollbackResult` is `(bool IsBlocked, DateTimeOffset? NewHighestObservedUtc)`. `FormalSlotStatus` is the one enum used by admission and persisted records.

- [ ] **Step 1: Write deterministic tests first.** Cover before-start `PENDING`, at-start eligible, start plus 3:59.999 eligible, exact start plus 4:00 `MISSED`, no early dispatch, no expired catch-up, stable `SlotId` independent of trigger order, yesterday's late slot retaining yesterday's identity after midnight.

- [ ] **Step 2: Add timezone tests.** Cover `SYSTEM` resolving to current Windows ID, explicit timezone mismatch returning blocked, spring-forward nonexistent local time as `MISSED`, and fall-back ambiguous local time admitting only its first UTC occurrence.

- [ ] **Step 3: Add rollback tests.** An observed backward move is blocked until current UTC reaches the persisted high-water mark; monotonic time advances the mark.

- [ ] **Step 4: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~Slots`. Expected: FAIL before implementation.

- [ ] **Step 5: Implement the pure calculations.** Use Windows timezone rules and UTC instants; for an ambiguous slot use its first UTC occurrence only. Represent admission as `[start, start + 4 minutes)`. Do not read the wall clock inside the evaluator.

- [ ] **Step 6: Rerun focused tests.** Expected: PASS with fixed UTC instants and no sleeps.

- [ ] **Step 7: Commit this task.** `git add src/CodexSlotPing.Core/Slots tests/CodexSlotPing.Core.Tests/Slots`; commit as `feat: calculate strict Codex slots`.

---

### Task 4: Atomic Config/State Persistence and Formal State Machine

**Files:**

- Create: `src/CodexSlotPing.Core/Persistence/IAtomicFileOperations.cs`
- Create: `src/CodexSlotPing.Core/Persistence/AtomicJsonFile.cs`
- Create: `src/CodexSlotPing.Core/Persistence/UserDataPaths.cs`
- Create: `src/CodexSlotPing.Core/Configuration/IConfigStore.cs`
- Create: `src/CodexSlotPing.Core/Configuration/JsonConfigStore.cs`
- Create: `src/CodexSlotPing.Core/State/StateDocument.cs`
- Create: `src/CodexSlotPing.Core/State/FormalSlotRecord.cs`
- Create: `src/CodexSlotPing.Core/State/TestRunRecord.cs`
- Create: `src/CodexSlotPing.Core/Slots/NextSlotCalculator.cs`
- Create: `src/CodexSlotPing.Core/State/StateTransitions.cs`
- Create: `src/CodexSlotPing.Core/State/IStateStore.cs`
- Create: `src/CodexSlotPing.Core/State/JsonStateStore.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Persistence/AtomicJsonFileTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/State/StateTransitionsTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Slots/NextSlotCalculatorTests.cs`

**Interfaces:**

- Produces: `StateDocument(int SchemaVersion, DateTimeOffset? HighestObservedUtc, IReadOnlyList<FormalSlotRecord> FormalSlots, TestRunRecord? LastTest)`; `FormalSlotRecord(SlotId SlotId, int Position, FormalSlotStatus Status, DateTimeOffset UpdatedAtUtc, string? DiagnosticCategory)`.
- `TestRunRecord` stores only test ID, completion time, model/reasoning, result category, elapsed time, and exit code; no prompt/response.
- Produces: `NextSlotCalculator.GetNext(AppConfig config, StateDocument state, DateTimeOffset nowUtc, string currentWindowsTimeZoneId) -> SlotScheduleEntry?`; `SlotScheduleEntry(SlotId SlotId, int Position, DateTimeOffset StartUtc)`.
- Produces: `IConfigStore.LoadOrCreateAsync(CancellationToken)` / `SaveAsync(AppConfig, CancellationToken)` and `IStateStore.LoadAsync(CancellationToken)` / `SaveAsync(StateDocument, CancellationToken)`.
- `PENDING` is derived when no record exists and the window remains open. Store terminal records; never transition a terminal status back to `PENDING`.

- [ ] **Step 1: Write persistence fault tests first.** Assert writes use a unique temporary sibling, flush before replacement, atomically move on first creation, preserve valid prior bytes on serialization/flush/replace failure, clean only the temp file created by that write, and never accept a partial document. Use injected deterministic filesystem failures; do not simulate real power loss.

- [ ] **Step 2: Write state-machine tests.** Assert allowed transitions: `PENDING -> CLAIMED -> CONFIRMED_SUCCESS`, `PENDING -> CLAIMED -> ATTEMPTED_UNCONFIRMED`, `PENDING -> CLAIMED -> PRELAUNCH_FAILED`, `PENDING -> MISSED`, and stale `CLAIMED -> ATTEMPTED_UNCONFIRMED`. Reject terminal-to-pending and finalizing an unclaimed slot. Malformed/unsupported config/state fails closed without replacement.

- [ ] **Step 3: Write next-slot tests.** Assert the earliest unclaimed future identity is returned; a slot remains next while its admission window is open; terminal identities and same-day claimed positions are skipped; and the next-day selection crosses midnight correctly.

- [ ] **Step 4: Run focused tests and verify failure.** Run the persistence, state-transition, and next-slot test classes individually. Expected: FAIL before implementation.

- [ ] **Step 5: Implement atomic JSON storage, transitions, and state-aware next-slot selection.** Keep config/state/logs below `%LOCALAPPDATA%\CodexSlotPing`. Use schema version 1 and `FileStream.Flush(true)` before replace. Never create an empty replacement state after parse errors. Return the earliest unclaimed future `SlotScheduleEntry`, preserving an identity during its open admission window.

- [ ] **Step 6: Rerun focused tests.** Expected: PASS, including `AtomicReplaceFailure_PreservesPriorStateAndBlocksDispatch` and all next-slot assertions.

- [ ] **Step 7: Commit this task.** `git add src/CodexSlotPing.Core/Persistence src/CodexSlotPing.Core/Configuration src/CodexSlotPing.Core/State src/CodexSlotPing.Core/Slots/NextSlotCalculator.cs tests/CodexSlotPing.Core.Tests`; commit as `feat: persist config and slot state atomically`.

---

### Task 5: Named Mutex, Claim Ordering, and Same-Day Position Guard

**Files:**

- Create: `src/CodexSlotPing.Core/Concurrency/IStateMutex.cs`
- Create: `src/CodexSlotPing.Core/Concurrency/NamedStateMutex.cs`
- Create: `src/CodexSlotPing.Core/State/SlotClaimResult.cs`
- Create: `src/CodexSlotPing.Core/State/FormalSlotClaimService.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Concurrency/NamedStateMutexTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/State/FormalSlotClaimServiceTests.cs`

**Interfaces:**

- Produces: `IStateMutex.TryAcquire(TimeSpan timeout) -> StateMutexLease?`; null means dispatch is blocked.
- Produces: `FormalSlotClaimService.TryClaimCurrentSlotAsync(CancellationToken) -> SlotClaimResult`. Inject `IClock`, `ITimeZoneProvider`, config/state stores, `IStateMutex`, and `SlotAdmissionEvaluator`; no Codex process dependency.
- Mutex name: `Local\CodexSlotPing.State.<current-user-SID>`, shared by App Test Now and Runner.

- [ ] **Step 1: Write claim tests first.** Assert a claim is atomically persisted as `CLAIMED` before an eligible result returns; a second claim for the identity is rejected; claiming one position prevents a second same-day dispatch if that slot time is edited; editing an unclaimed future slot creates a new identity; an expired identity remains missed.

- [ ] **Step 2: Write failure tests.** Assert unavailable mutex, invalid state, timezone mismatch, rollback, failed claim persistence, and disabled config each block dispatch.

- [ ] **Step 3: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~FormalSlotClaimServiceTests`. Expected: FAIL before implementation.

- [ ] **Step 4: Implement the named mutex and claim service.** Acquire the mutex, load one config/state snapshot, read UTC once, update the high-water mark, re-evaluate under the lock, persist `CLAIMED`, then release the lock before any child process can start.

- [ ] **Step 5: Rerun focused tests.** Expected: PASS; the claim service has no process-launch dependency.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.Core/Concurrency src/CodexSlotPing.Core/State tests/CodexSlotPing.Core.Tests`; commit as `feat: claim formal slots at most once`.

---

### Task 6: Direct Codex CLI Detection and Safe Process Launch

**Files:**

- Create: `src/CodexSlotPing.Core/Codex/CodexCliCapabilities.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexExecutableResolver.cs`
- Create: `src/CodexSlotPing.Core/Codex/ICodexEnvironmentDetector.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexEnvironmentDetector.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexInvocationRequest.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexInvocationBuilder.cs`
- Create: `src/CodexSlotPing.Core/Codex/ICodexProcessLauncher.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexProcessLauncher.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexResultClassification.cs`
- Create: `src/CodexSlotPing.Core/Codex/CodexInvocationResult.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Codex/CodexExecutableResolverTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Codex/CodexInvocationBuilderTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Codex/CodexProcessLauncherTests.cs`

**Interfaces:**

- Produces: `ICodexEnvironmentDetector.DetectAsync(CancellationToken) -> Task<EnvironmentStatus>`.
- Produces: `ICodexProcessLauncher.RunAsync(CodexInvocationRequest request, CancellationToken cancellationToken) -> Task<CodexInvocationResult>`.
- `CodexInvocationRequest` carries executable path, model, reasoning effort, prompt, app-owned empty working directory, and formal/test invocation ID.
- `CodexResultClassification` is `CONFIRMED_SUCCESS`, `USAGE_LIMIT`, `ATTEMPTED_UNCONFIRMED`, or `PRELAUNCH_FAILED`.

- [ ] **Step 1: Write executable-resolution tests first.** Assert an absolute native `codex.exe` is selected; the known npm shim may locate only its matching native package binary, as the verified prototype does. No `.ps1`, `.cmd`, `cmd.exe`, `powershell.exe`, or `pwsh.exe` is launched. If no direct native binary exists, report not directly launchable. Multiple installs must not trigger a silent version fallback.

- [ ] **Step 2: Write argument/environment tests.** Pin the vector to Task 1 evidence and frozen contract: explicit model; verified `model_reasoning_effort` config override; read-only sandbox; approval `never`; `--ignore-user-config`, `--skip-git-repo-check`, `--ephemeral`, `--json`, and stdin sentinel `-`. Prompt text goes only to stdin. Assert explicit `FileName`, `UseShellExecute=false`, and empty app-owned working directory. Strip `OPENAI_API_KEY`, `CODEX_API_KEY`, `CODEX_ACCESS_TOKEN`, `OPENAI_BASE_URL`, `CODEX_OSS_BASE_URL`, `CODEX_APP_SERVER_CHATGPT_BASE_URL`, `CODEX_AUTHAPI_BASE_URL`, `CODEX_AGENT_IDENTITY_AUTHAPI_BASE_URL`, and `CODEX_AGENT_IDENTITY_JWKS_BASE_URL` by name without recording values; preserve `CODEX_HOME`.

- [ ] **Step 3: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~Codex`. Expected: FAIL before implementation.

- [ ] **Step 4: Implement detection and launch.** Verify `--version`, `login status`, and required option support without sending a prompt. Mark login true only when explicitly reported by Codex. Use `ProcessStartInfo.ArgumentList`, redirect/drain UTF-8 stdout/stderr concurrently, enforce 100 seconds, and kill the child process tree on timeout. Reasoning choices come only from the captured CLI compatibility set; selected model is not account-availability evidence.

- [ ] **Step 5: Rerun focused tests.** Expected: PASS with FakeCodex; no network request or account usage.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.Core/Codex tests/CodexSlotPing.Core.Tests/Codex`; commit as `feat: invoke the detected Codex CLI directly`.

---

### Task 7: JSONL Classification and Privacy-Safe Logging

**Files:**

- Create: `src/CodexSlotPing.Core/Codex/CodexOutputClassifier.cs`
- Create: `src/CodexSlotPing.Core/Logging/StructuredLogEntry.cs`
- Create: `src/CodexSlotPing.Core/Logging/IStructuredLogWriter.cs`
- Create: `src/CodexSlotPing.Core/Logging/StructuredLogWriter.cs`
- Create: `src/CodexSlotPing.Core/Logging/RawLogStore.cs`
- Create: `tests/CodexSlotPing.Core.Tests/Fixtures/CodexOutput/success.jsonl`
- Create: `tests/CodexSlotPing.Core.Tests/Fixtures/CodexOutput/usage-limit.jsonl`
- Create: `tests/CodexSlotPing.Core.Tests/Fixtures/CodexOutput/warnings-success.jsonl`
- Test: `tests/CodexSlotPing.Core.Tests/Codex/CodexOutputClassifierTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Logging/StructuredLogWriterTests.cs`

**Interfaces:**

- Consumes: `CodexInvocationResult` from Task 6 and formal-state mapping from Task 4.
- Produces: `CodexOutputClassifier.Classify(string stdoutJsonl, string stderr, int? exitCode, bool timedOut, bool processStarted) -> CodexInvocationResult` and `IStructuredLogWriter.AppendAsync(StructuredLogEntry, CancellationToken)`.

- [ ] **Step 1: Write fixture-backed classifier tests first.** Success requires process start, exit zero, complete JSONL, `turn.completed`, completed `agent_message`, and no later terminal error. Cover usage limit/`turn.failed`, stderr warning plus success, nonzero exit, timeout, malformed stream, partial final line, unknown event, error event, and missing terminal event. Add exact test `StderrSkillYamlWarnings_DoNotOverrideJsonlSuccess` using stderr skill/YAML warning text and a complete successful JSONL fixture. A warning alone does not invalidate success; unknown nonterminal events are ignored.

- [ ] **Step 2: Write log privacy tests.** Assert structured records contain UTC timestamp/local offset, slot ID or test ID, kind, model, reasoning, result, elapsed, exit code, diagnostic category, and usage when available. Assert prompt, response, auth values, and stderr text are absent. Raw logging is opt-in, separate, disclosed, and prunes files older than 14 days.

- [ ] **Step 3: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~CodexOutputClassifierTests` and repeat with `FullyQualifiedName~StructuredLogWriterTests`. Expected: FAIL before implementation.

- [ ] **Step 4: Implement classification and logs.** Any post-start timeout, nonzero exit, quota, failed turn, error, malformed/truncated stream, or uncertain result maps to `ATTEMPTED_UNCONFIRMED`. Process creation failure after claim maps to `PRELAUNCH_FAILED`. Stderr warnings never override valid JSONL success.

- [ ] **Step 5: Rerun focused tests.** Expected: PASS for all fixtures and privacy assertions.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.Core/Codex src/CodexSlotPing.Core/Logging tests/CodexSlotPing.Core.Tests`; commit as `feat: classify Codex output and write private logs`.

---

### Task 8: Owned Task Scheduler Registration and Readback

**Files:**

- Create: `src/CodexSlotPing.Core/Scheduler/SchedulerState.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/SchedulerStatus.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/SchedulerTaskDefinition.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/SchedulerOperationResult.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/ITaskSchedulerGateway.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/WindowsTaskSchedulerGateway.cs`
- Create: `src/CodexSlotPing.Core/Scheduler/TaskSchedulerStaWorker.cs`
- Test: `tests/CodexSlotPing.IntegrationTests/Scheduler/TaskSchedulerContractTests.cs`

**Interfaces:**

- Produces: `ReadStatusAsync(string taskPath, string currentRunnerPath, string configuredTimeZoneId, CancellationToken) -> Task<SchedulerStatus>`; `InstallOrUpdateAsync(SchedulerTaskDefinition definition, CancellationToken) -> Task<SchedulerOperationResult>`; `DisableAsync(string taskPath, CancellationToken) -> Task<SchedulerOperationResult>`; `RemoveAsync(string taskPath, CancellationToken) -> Task<SchedulerOperationResult>`.
- Production task path is `\Codex Slot Ping` with the exact owner marker. The action passes its task path to Runner as `--task-path` so integration tests can use a unique disposable task path while running the actual Runner. Production UI cannot select a task path.

- [ ] **Step 1: Write scheduler contract tests first.** Assert four enabled daily triggers with local `StartBoundary` and no fixed UTC offset; exact configured times; `StartWhenAvailable=false`; `IgnoreNew`; `PT2M`; restart count zero/no restart-on-failure; WakeToRun from config; least privilege; current interactive-user principal; direct absolute Runner action with `--task-path` argument; Runner-directory working directory; exact marker.

- [ ] **Step 2: Write ownership/lifecycle tests.** A same-name task without the marker remains byte-for-byte unchanged after Install, Disable, and Remove. An owned task updates and readback matches; Disable preserves it; Remove deletes only it.

- [ ] **Step 3: Run Windows-only integration tests against a unique disposable task.** Run: `dotnet test tests/CodexSlotPing.IntegrationTests/CodexSlotPing.IntegrationTests.csproj -c Release --filter FullyQualifiedName~TaskSchedulerContractTests` on Windows. Expected before implementation: FAIL. Never touch the user's production task.

- [ ] **Step 4: Implement the native Task Scheduler COM gateway.** Keep COM calls off the WPF UI thread on an STA worker. Register, update, disable, remove, and read back only an exactly owned task. Do not use PowerShell or store a Windows password.

- [ ] **Step 5: Rerun Windows-only integration tests.** Expected: PASS after every field is read back and compared.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.Core/Scheduler tests/CodexSlotPing.IntegrationTests/Scheduler`; commit as `feat: register and verify the owned Windows task`.

---

### Task 9: Portable Path Drift, Timezone Drift, and Repair

**Files:**

- Create: `src/CodexSlotPing.Core/Scheduler/SchedulerHealthEvaluator.cs`
- Create: `src/CodexSlotPing.Core/Configuration/TimezoneUpdateService.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Scheduler/SchedulerHealthEvaluatorTests.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Configuration/TimezoneUpdateServiceTests.cs`

**Interfaces:**

- Consumes: `SchedulerStatus`, `AppConfig`, `ITaskSchedulerGateway`, and atomic config storage.
- Produces: `SchedulerHealthEvaluator.Evaluate(SchedulerStatus status, string currentRunnerPath, string configuredTimeZoneId, string currentWindowsTimeZoneId) -> SchedulerStatus`; `TimezoneUpdateService.ApplyAsync(AppConfig config, string currentRunnerPath, string currentWindowsTimeZoneId, bool userConfirmed, CancellationToken) -> Task<TimezoneUpdateResult>`.

- [ ] **Step 1: Write the moved-path test first.** Given registered `C:\Old\CodexSlotPing.Runner.exe` and current `D:\New\CodexSlotPing.Runner.exe`, assert `NEEDS_REPAIR` and never healthy. After owned Install/Update, assert the action is the current path. Assert a foreign same-name task is untouched. Name the test `MovedRunnerPath_ShowsNeedsRepair_AndRepairUpdatesOwnedAction`.

- [ ] **Step 2: Write timezone drift tests.** An explicit configured ID differing from Windows returns `TIMEZONE_CHANGED`, performs no config rewrite, and blocks dispatch. After explicit confirmation with the current ID, assert config is atomically saved and the owned task updated. If update fails, report error and never healthy. Name the test `ChangedWindowsTimeZone_FailsClosedUntilConfirmedUpdate`.

- [ ] **Step 3: Run focused tests and verify failure.** Run both test classes in `CodexSlotPing.Core.Tests`. Expected: FAIL before implementation.

- [ ] **Step 4: Implement health evaluation and repair.** Compare registered action path/current Runner path and locked/current timezone. Repair is an explicit Install/Update against the exact owned task; no daemon, fixed install directory, registry launcher, or hidden program copy.

- [ ] **Step 5: Rerun focused tests.** Expected: PASS for both release-blocker cases.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.Core/Scheduler src/CodexSlotPing.Core/Configuration tests/CodexSlotPing.Core.Tests`; commit as `fix: detect and repair scheduler drift`.

---

### Task 10: Runner Orchestration and Cross-Process At-Most-Once Proof

**Files:**

- Create: `src/CodexSlotPing.Runner/RunnerArguments.cs`
- Create: `src/CodexSlotPing.Runner/RunnerOrchestrator.cs`
- Create: `src/CodexSlotPing.Runner/Program.cs`
- Create: `tests/CodexSlotPing.IntegrationTests/Runner/RunnerOrchestratorTests.cs`
- Create: `tests/CodexSlotPing.IntegrationTests/Runner/TwoRunnerProcessesTests.cs`
- Modify: `tests/CodexSlotPing.FakeCodex/Program.cs`

**Interfaces:**

- Produces: `RunnerArguments.Parse(string[] args) -> RunnerOptions` and `RunnerOrchestrator.RunFormalSlotAsync(CancellationToken) -> Task<RunnerExitCode>`.
- Constructor injection is limited to clock, config/state stores, mutex, timezone provider, scheduler gateway, environment detector, process launcher, and structured log writer. Construct them directly in `Program.cs`; no DI container.

- [ ] **Step 1: Write orchestration tests first with fakes.** Assert pending to claimed to success; pending to claimed to attempted; pending to missed; already-terminal no invocation; config/task disabled no invocation; timezone mismatch no invocation; clock rollback no invocation; mutex loss no invocation; malformed state no invocation; claim-write failure no invocation.

- [ ] **Step 2: Write crash/timeout tests.** Seed durable `CLAIMED` to simulate a crash before Codex launch; a subsequent Runner must not dispatch. Simulate process creation failure and assert terminal `PRELAUNCH_FAILED`. Simulate FakeCodex timeout, quota failure, and network-unknown JSONL; assert no retry. Assert `CLAIMED` exists on disk before FakeCodex records process start.

- [ ] **Step 3: Write the cross-process race test.** Use an isolated LocalAppData root, an open slot, a unique test task path, and FakeCodex as the direct CLI executable. Start two actual `CodexSlotPing.Runner.exe` processes at once. Assert one durable claim, one FakeCodex launch, and no second dispatch. No live Codex account or production task is used. Name the test `TwoRunnerProcesses_OnlyOneClaimsAndLaunches`.

- [ ] **Step 4: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.IntegrationTests/CodexSlotPing.IntegrationTests.csproj -c Release --filter FullyQualifiedName~Runner`. Expected: FAIL before implementation.

- [ ] **Step 5: Implement Runner orchestration.** Check config, owned-task state/path, timezone, and clock guard; claim; release mutex; repeat local CLI version/login/capability checks; start one request; classify it; atomically finalize state/logs. If a process starts, uncertainty consumes the slot. Return stable exit codes.

- [ ] **Step 6: Rerun focused integration tests.** Expected: PASS, especially cross-process race and claim-before-launch.

- [ ] **Step 7: Commit this task.** `git add src/CodexSlotPing.Runner tests/CodexSlotPing.IntegrationTests/Runner tests/CodexSlotPing.FakeCodex`; commit as `feat: dispatch formal slots through the Runner`.

---

### Task 11: Test Now Isolated Request Flow

**Files:**

- Create: `src/CodexSlotPing.Core/Testing/TestRunResult.cs`
- Create: `src/CodexSlotPing.Core/Testing/TestRunService.cs`
- Test: `tests/CodexSlotPing.Core.Tests/Testing/TestRunServiceTests.cs`

**Interfaces:**

- Consumes: `TestRunRecord` from Task 4 and the same CLI launcher/classifier used by Runner.
- Produces: `TestRunService.RunAsync(AppConfig config, CancellationToken) -> Task<TestRunResult>`.
- It only updates `StateDocument.LastTest` under the mutex; it never calls `FormalSlotClaimService`.

- [ ] **Step 1: Write tests first.** A test run gets a unique ID and changes only `lastTest`; formal records, today's statuses, and next slot remain identical. Cancellation before launch makes no process call. A failed Test Now is recorded without a formal claim.

- [ ] **Step 2: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.Core.Tests/CodexSlotPing.Core.Tests.csproj -c Release --filter FullyQualifiedName~TestRunServiceTests`. Expected: FAIL before implementation.

- [ ] **Step 3: Implement the isolated service.** Use the same atomic store/mutex and configured prompt/model/reasoning. Automated tests use FakeCodex only; no live request.

- [ ] **Step 4: Rerun focused tests.** Expected: PASS.

- [ ] **Step 5: Commit this task.** `git add src/CodexSlotPing.Core/Testing tests/CodexSlotPing.Core.Tests/Testing`; commit as `feat: add isolated Codex Test Now flow`.

---

### Task 12: Compact WPF Main Window and Settings

**Files:**

- Create: `src/CodexSlotPing.App/AppController.cs`
- Create: `src/CodexSlotPing.App/RunnerPathResolver.cs`
- Modify: `src/CodexSlotPing.App/MainWindow.xaml`
- Modify: `src/CodexSlotPing.App/MainWindow.xaml.cs`
- Create: `src/CodexSlotPing.App/SettingsWindow.xaml`
- Create: `src/CodexSlotPing.App/SettingsWindow.xaml.cs`
- Create: `tests/CodexSlotPing.IntegrationTests/App/AppControllerTests.cs`

**Interfaces:**

- Produces: `RefreshAsync(CancellationToken) -> Task<AppScreenState>`; `SaveSettingsAsync(AppConfig, CancellationToken) -> Task<ConfigSaveResult>`; `InstallOrUpdateSchedulerAsync(CancellationToken) -> Task<SchedulerOperationResult>`; `DisableSchedulerAsync(CancellationToken) -> Task<SchedulerOperationResult>`; `RemoveSchedulerAsync(CancellationToken) -> Task<SchedulerOperationResult>`; `RunTestNowAsync(bool userConfirmed, CancellationToken) -> Task<TestRunResult?>`.
- `RunnerPathResolver.Resolve(string appBaseDirectory) -> string` returns the portable-relative Runner executable path.

- [ ] **Step 1: Write AppController tests first.** Assert scheduler state comes from Task Scheduler readback, not config alone; saving settings updates an enabled owned task but does not re-enable a disabled task; timezone/path drift is visible; Test Now runs only after confirmation; validation always calls Core.

- [ ] **Step 2: Run focused tests and verify failure.** Run: `dotnet test tests/CodexSlotPing.IntegrationTests/CodexSlotPing.IntegrationTests.csproj -c Release --filter FullyQualifiedName~AppControllerTests`. Expected: FAIL before implementation.

- [ ] **Step 3: Implement the compact WPF layout.** Show CODEX connection/login/version, Next Slot as visual center, TODAY as four rows, model/reasoning, Scheduler/Wake state, Test Now, and Open logs. Next Slot uses the state-aware Core calculation and remains next during its open admission window, with the remaining window shown. Use one restrained information column; no gradient, card wall, glass panel, or dashboard grid.

- [ ] **Step 4: Implement settings and drift actions.** Edit four times, model, supported reasoning, prompt, timezone mode/lock, and WakeToRun. Use Core validation. Show “Timezone changed / scheduler needs update” and require explicit confirmation before saving the current timezone and updating the task. Show “Scheduler needs repair / path changed” for stale action paths; never show healthy. Missing CLI/login states include copyable official instructions that the App does not execute. Before Test Now and scheduler installation, explain that the prompt is sent to Codex and Test Now may count toward usage; require the Test Now confirmation.

- [ ] **Step 5: Build and manually inspect the Windows UI.** Run: `dotnet build src/CodexSlotPing.App/CodexSlotPing.App.csproj -c Release`. Verify four rows, hierarchy, and mismatch/error states.

- [ ] **Step 6: Commit this task.** `git add src/CodexSlotPing.App tests/CodexSlotPing.IntegrationTests/App`; commit as `feat: add compact settings and status UI`.

---

### Task 13: Tray, Single Instance, and Responsive UI Operations

**Files:**

- Create: `src/CodexSlotPing.App/TrayIconController.cs`
- Create: `src/CodexSlotPing.App/SingleInstanceCoordinator.cs`
- Create: `src/CodexSlotPing.App/UiPreferencesStore.cs`
- Modify: `src/CodexSlotPing.App/App.xaml.cs`
- Modify: `src/CodexSlotPing.App/AppController.cs`
- Test: `tests/CodexSlotPing.IntegrationTests/App/AppLifecycleTests.cs`

**Interfaces:**

- Tray actions call `AppController`; tray code owns no scheduling rules.
- A named mutex identifies the App instance; a per-user named pipe carries only an activate-window message.
- Store the “suppress Exit warning” choice in `%LOCALAPPDATA%\CodexSlotPing\ui-preferences.json`, separate from execution config/state.

- [ ] **Step 1: Write lifecycle/controller tests first.** Assert close hides to tray; Exit terminates only App and never disables/removes the scheduler; first Exit while scheduler is ON shows “Closing Codex Slot Ping does not disable scheduled slots.”; suppressing it persists across launch; second App launch activates the first; repeated Test Now clicks during an active run do not start another request.

- [ ] **Step 2: Implement tray behavior.** Use the built-in Windows tray icon and menu entries Open, Test now, Scheduler state, Wake computer, Open logs, and Exit. Scheduler/Wake reflect saved/readback state; Wake computer updates the saved setting and owned enabled task. Exit leaves Task Scheduler unchanged.

- [ ] **Step 3: Implement single-instance activation.** The second process sends the activate message and exits; the first restores/foregrounds its window. No general IPC/event framework.

- [ ] **Step 4: Keep slow work off the UI thread.** Await Codex, environment detection, files, and Task Scheduler COM asynchronously. Disable Test Now and show progress while it runs; restore the button in a finally path. Run: `dotnet test tests/CodexSlotPing.IntegrationTests/CodexSlotPing.IntegrationTests.csproj -c Release --filter FullyQualifiedName~AppLifecycleTests`. Expected: PASS.

- [ ] **Step 5: Commit this task.** `git add src/CodexSlotPing.App tests/CodexSlotPing.IntegrationTests/App`; commit as `feat: add tray lifecycle and single-instance activation`.

---

### Task 14: Portable Publishing, README, and GitHub Actions

**Files:**

- Create: `README.md`
- Create: `.github/workflows/windows.yml`
- Modify: `.gitignore` only if generated artifacts need new ignore rules.

**Interfaces:**

- Release artifact: `CodexSlotPing-v0.1.0-win-x64.zip` plus matching `.sha256`.
- Workflow runs on Windows, restores/builds/tests, publishes App and Runner self-contained for `win-x64`, stages App at portable root and Runner under `Runner/`, zips, and computes SHA-256. A v0.1.0 tag attaches the ZIP and checksum to the GitHub release. No installer or signing workflow.

- [ ] **Step 1: Add package acceptance checks before publishing.** Workflow asserts the staged ZIP contains App and Runner executables and does not require an external .NET runtime. CI uses FakeCodex and receives no Codex credentials.

- [ ] **Step 2: Implement publishing and README.** No developer-specific absolute paths in source/runtime. README front page states strict no-catch-up, at-most-once, existing Codex/ChatGPT login, no API keys, no daemon, Task Scheduler, and the OpenAI independence disclaimer.

- [ ] **Step 3: Validate build/package commands.** Run: `dotnet test CodexSlotPing.sln -c Release`; publish both executable projects with `-r win-x64 --self-contained true`. Inspect staged files, ZIP contents, and checksum match. No local Codex installation is required for CI.

- [ ] **Step 4: Commit this task.** `git add README.md .github/workflows/windows.yml .gitignore`; commit as `build: package self-contained Windows release`.

---

### Task 15: Clean-Machine-Like Release Acceptance

**Files:**

- Modify: `README.md` only for verified user-facing corrections.
- Modify: `.github/workflows/windows.yml` only for verified release-gate corrections.
- Evidence: release checklist attached to the v0.1.0 candidate; no test project is added.

**Interfaces:**

- Consumes the ZIP and automated test/build outputs from Tasks 1–14.
- Produces a release decision against every §22 criterion in the frozen spec; a successful build alone proves none of the manual acceptance items.

- [ ] **Step 1: Run the complete automated suite on Windows.** Run: `dotnet test CodexSlotPing.sln -c Release`. Expected: Core, process, Runner, App-controller, and isolated Task Scheduler integration tests pass; scheduler tests clean only their own marked test tasks.

- [ ] **Step 2: Test the extracted ZIP on a clean Windows x64 machine-like environment without preinstalled .NET or PowerShell 7.** Verify launch, defaults, Codex missing message, direct CLI detection and authenticated status, settings validation, logs, tray Exit behavior, and second-launch activation. A real Test Now is manual and user-confirmed, never unattended.

- [ ] **Step 3: Verify scheduler lifecycle and execution.** Install the owned task; read back all four triggers/settings; close/Exit App; verify task remains and directly starts Runner; verify strict admission/no missed-slot replay; reopen App and check state; exercise Disable, Enable/Update, and Remove independently.

- [ ] **Step 4: Verify both release-blocker drift cases.** Move the portable directory and assert stale path reports NeedsRepair until update. Change Windows timezone after configuring an explicit lock and assert Runner fails closed until explicit settings confirmation and scheduler update.

- [ ] **Step 5: Verify privacy and release files.** Structured logs have no prompt/response; raw logs are opt-in and expire after 14 days; no auth values are stored/copied; README matches behavior; ZIP is self-contained; SHA-256 matches.

- [ ] **Step 6: Publish candidate evidence and commit only verified corrections.** Release only if every spec §22 item passes; otherwise record the exact blocker and do not label v0.1 ready.

## Spec Coverage Self-Review

- §1–3 product promises, flows, data roots, Test Now, and Exit: Tasks 2, 4, 11–13, 15.
- §4–5 App/Core/Runner ownership and direct Runner dispatch: Tasks 1, 8, 10, 12–13.
- §6 defaults, validation, timezone, wake, model, reasoning, prompt, atomic config: Tasks 2, 4.
- §7–11 states, identity, strict window, DST, rollback, claim, mutex, atomic writes: Tasks 3–5, 10.
- §12 Task Scheduler fields, ownership, readback/lifecycle, path repair: Tasks 8–9, 15.
- §13–14 CLI discovery, direct process, auth, JSONL, timeout, stderr, classification: Tasks 1, 6–7, 10–11.
- §15–16 UI, settings, tray, Exit warning, single instance: Tasks 12–13.
- §17–18 privacy, logs, environment evidence, model/account distinction: Tasks 6–7, 12, 15.
- §19–20 .NET 10 distribution, README, CI, security boundaries: Tasks 1, 6, 14–15.
- §21–22 test strategy/release criteria: Tasks 2–15.
- §23 later candidates: excluded by Global Constraints.

Self-review: each named domain type has one definition; task interfaces consume earlier outputs; Core remains config authority; claim precedes dispatch; Test Now has no formal identity; both drift cases have concrete automated tests; no shell runtime, credential storage, daemon, or product framework was added; clean-machine acceptance requires no .NET/PowerShell 7 install; the implementation stops after release acceptance.
