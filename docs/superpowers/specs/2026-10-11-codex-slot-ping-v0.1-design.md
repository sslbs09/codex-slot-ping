# Codex Slot Ping v0.1 — Design Specification

> **Historical design snapshot.** This document records the original fixed-slot proposal before Scheduling Semantics v2 and M3-C were implemented. Its fixed-slot trigger and admission details are retained for history and migration context; where they differ, the current release-candidate behavior is documented in [README](../../../README.md), [Architecture](../../architecture.md), and [Current Status](../../current-status.md). It is not the current scheduler contract.

**Historical status:** Original design freeze candidate for owner review; superseded by the v0.1 dynamic-scheduling implementation.
**Date:** 2026-10-11  
**Target:** Windows x64, portable ZIP  
**Repository:** codex-slot-ping

> Codex Slot Ping is an independent open-source project and is not affiliated with or endorsed by OpenAI.

This document freezes the v0.1 product and runtime contract. Implementation must preserve the promises in §1 and satisfy §22 before release.

## 1. Product goal

Codex Slot Ping is a small Windows tray utility that starts one lightweight Codex CLI prompt at each of four user-selected daily times.

The user supplies an existing Codex CLI installation and ChatGPT sign-in. The application stores its schedule and execution state, then registers one owned Windows Scheduled Task with four daily triggers. The task starts a short-lived Runner process. The tray UI may be closed or exited while Task Scheduler continues to run the slots.

Three product promises:

1. **Missed means missed.** A slot outside its admission window is never replayed.
2. **A claim means no retry.** A slot is durably claimed before Codex starts. Once claimed, that identity is never dispatched again, even if no response is confirmed.
3. **Closing the UI does not stop the scheduler.** Only explicit Disable or Remove actions change the owned task.

The utility automates timing only. It does not promise account quota availability, a reset time, or successful model access.

## 2. Non-goals

v0.1 does not provide:

- Multiple profiles or more than four daily slots.
- Usage, quota, or reset-time viewers.
- Installer, MSI, Winget package, code signing, or automatic updates.
- A background scheduler service or always-running scheduler daemon.
- Automatic retries, catch-up, or fallback to another provider.
- Browser automation, ChatGPT login UI, API key storage, or credential migration.
- Other providers, a general-purpose agent scheduler, tool-workflow editor, attachments, images, or multi-request prompts.
- Automatic Codex installation.
- A guarantee that the selected model is available to the signed-in account.

## 3. User flows

### First launch

1. The user extracts the ZIP and starts CodexSlotPing.App.exe.
2. The App creates or loads configuration and state under %LOCALAPPDATA%\CodexSlotPing.
3. It detects Codex CLI and reports the resolved executable, version, and ChatGPT login status.
4. The user edits the four times, model, reasoning effort, prompt, and optional timezone lock.
5. Scheduler is **OFF** until the user selects Install scheduler.

### Test Now

1. The UI explains: “This sends a real Codex request and may count toward usage limits.”
2. The user selects Test Now and confirms.
3. The App starts one real Codex request and reports success or a classified failure.
4. Only state.lastTest and a test log record change. Formal slot records, today’s slot status, and next slot do not change.

### Install / update scheduler

1. The App validates configuration, CLI availability, Task Scheduler access, and timezone compatibility.
2. It checks the fixed task name and owner marker.
3. If absent, it creates the owned task. If the exact marker is present, it updates and enables it. A same-name task without that marker is a conflict and is never overwritten.
4. The App reads back the task and verifies trigger count and required settings before showing Scheduler ON.
5. Saving schedule settings updates an owned enabled task. If the task is disabled, settings are saved without enabling it.

### Scheduled slot

1. Task Scheduler starts CodexSlotPing.Runner.exe directly.
2. Runner loads one validated configuration snapshot, checks timezone and slot window, then claims the slot atomically.
3. Runner starts Codex with one prompt, captures stdout and stderr separately, classifies the result, persists state and logs, then exits.
4. A stale CLAIMED record is consumed and unresolved. Runner never starts it again.

### Disable / Remove / Exit

- **Disable scheduler** disables only the owned task. Task, config, state, and logs remain.
- **Remove scheduler** deletes only the owned task. Config, state, and logs remain.
- **Exit** closes the App process only. It does not disable or remove the task.
- The window close button hides the window to the tray. Tray Exit terminates the App. Both leave the task unchanged.

## 4. Architecture

The repository contains three product projects:

    src/
      CodexSlotPing.App/
      CodexSlotPing.Core/
      CodexSlotPing.Runner/

Runtime flow:

    Windows Task Scheduler
      -> CodexSlotPing.Runner.exe
      -> CodexSlotPing.Core
      -> Codex CLI process

    User
      -> CodexSlotPing.App (WPF window and tray)
      -> CodexSlotPing.Core
      -> Task Scheduler API / config / state / logs

The App is not a scheduler. Runner is a short-lived process and the only process that admits and dispatches formal slots. Core owns shared rules and I/O contracts and has no WPF dependency. App and Runner share Core and the per-user data directory.

The App is single-instance; a second launch activates its existing window. Runner invocations are independent processes. A named mutex and durable claims prevent duplicate dispatch.

## 5. App, Core, and Runner responsibilities

| Project | Owns | Must not own |
| --- | --- | --- |
| CodexSlotPing.App | WPF window, tray, settings UI, environment summary, Test Now, scheduler controls, log navigation, single-instance activation | Slot admission, formal dispatch, background scheduling, or another scheduler |
| CodexSlotPing.Core | Config validation, timezone/slot calculations, identity, state machine, mutex and atomic persistence, process launch, JSONL classification, logging, environment detection, Task Scheduler definition/readback | WPF types or UI lifetime |
| CodexSlotPing.Runner | Load config, select eligible formal slot, persist CLAIMED before dispatch, run Codex, finalize state/logs, return stable exit code | Tray/UI behavior, long-lived service, catch-up, retries |

Task Scheduler is the scheduling authority. Core independently decides whether dispatch is still permitted when Runner starts.

## 6. Configuration schema

Default values:

    {
      "schemaVersion": 1,
      "enabled": true,
      "slots": ["07:00", "12:01", "17:01", "22:01"],
      "timeZone": "SYSTEM",
      "model": "gpt-5.6-luna",
      "reasoningEffort": "low",
      "prompt": "Hi.",
      "wakeToRun": false,
      "strictSlotWindowMinutes": 3,
      "captureRawLogs": false
    }

Rules:

- Exactly four unique 24-hour HH:mm values. Display them in chronological order.
- Adjacent slots, including across midnight, must be at least six minutes apart. Reject invalid edits before saving or installing. This minimum follows from the latest admitted start at +3:59, the two-minute task limit, and IgnoreNew behavior.
- strictSlotWindowMinutes is fixed at 3 in v0.1 and is not editable.
- SYSTEM follows the current Windows timezone. An explicit value is a Windows timezone ID lock. Install and dispatch are permitted only while that ID matches the machine’s current Windows timezone ID.
- Default timezone is SYSTEM. No China timezone is hard-coded.
- enabled is a Runner policy guard. Dispatch requires enabled=true and an enabled owned task. The UI reports scheduler state from Task Scheduler readback, not this flag alone.
- wakeToRun defaults to false and changes only after an explicit user action.
- prompt is plain text, non-empty after trimming, at most 200 Unicode scalar values. Newlines are allowed; attachments and workflow settings are not.
- model is editable and passed as a selection request; it does not prove account access.
- reasoningEffort is a dropdown backed by values supported by the detected CLI compatibility set. Default is low. Never invent or silently substitute an effort.
- captureRawLogs defaults to false. Enabling it requires a privacy notice because raw process output may include prompt, response, or personal data.

Config writes use a temporary sibling, flush it, and atomically replace config.json. Invalid or unreadable config fails closed and remains available for repair.

## 7. State machine

PENDING is derived when no durable record exists and the admission window remains open. MISSED may be derived for display after the window closes; Runner persists it when processing that expired identity. A powered-off computer cannot write state while off, but the next invocation still refuses expired slots.

| State | Meaning | Terminal for dispatch? |
| --- | --- | --- |
| PENDING | No claim exists and the window remains open | No |
| MISSED | No claim exists and the admission window closed | Yes |
| CLAIMED | Durable claim written before process launch; dispatch may be active or its outcome may be unknown after a crash | Yes |
| CONFIRMED_SUCCESS | Process started, exited zero, and complete JSONL confirms a completed turn and agent message | Yes |
| ATTEMPTED_UNCONFIRMED | Process started but completion cannot be confirmed: timeout, turn.failed, malformed/truncated stream, or nonzero exit | Yes |
| PRELAUNCH_FAILED | Slot was claimed but Codex could not be started | Yes |

Transitions:

    PENDING -> MISSED
    PENDING -> CLAIMED -> CONFIRMED_SUCCESS
    PENDING -> CLAIMED -> ATTEMPTED_UNCONFIRMED
    PENDING -> CLAIMED -> PRELAUNCH_FAILED
    CLAIMED -> ATTEMPTED_UNCONFIRMED   (stale claim reconciliation)

No terminal state returns to PENDING. Malformed state, failed atomic write, or unavailable mutex blocks dispatch. Runner never repairs corrupted state by replacing it with an empty file.

## 8. Slot identity

A formal identity is the tuple:

    Windows timezone ID + local calendar date + configured local slot time

Readable form:

    2026-10-11/12:01@Taipei Standard Time

The date and time use the configured timezone. Trigger array position is diagnostic metadata, never the identity.

State also retains slot position as a secondary same-day guard: after a position is claimed, editing its time does not permit a second dispatch for that position on the same local day. Editing an unclaimed future time creates a new local-time identity; an expired time remains missed. Timezone and slot edits never erase historical state.

## 9. Strict-slot semantics

The admission interval is half-open:

    slot start <= Runner admission time < slot start + 4 minutes

For a 12:01 slot, starts from 12:01:00 through 12:03:59.999… are eligible. At 12:04:00 it is MISSED. This tolerates scheduler jitter; it is not catch-up.

Runner reads one current UTC instant and converts it using the configured Windows timezone and that date’s rules. It rechecks eligibility after acquiring the claim mutex.

- If the computer is off through the slot and starts at 14:30, the slot is MISSED.
- WakeToRun may wake a sleeping PC only when enabled and supported by its hardware/power settings. It never starts a powered-off PC.
- A local time skipped by a spring-forward transition is MISSED for that date.
- If a local time occurs twice at fall-back, only the first corresponding UTC occurrence is eligible. The repeated occurrence cannot reopen it.
- If a locked timezone differs from the current Windows timezone, Runner refuses dispatch and reports a mismatch.
- A backward wall-clock movement already observed and persisted fails closed until the UTC high-water mark is reached again. The product cannot prove elapsed real time across an unobserved clock change while powered off; it adds no external time source.

StartWhenAvailable=false and Runner admission are both required. Either alone is insufficient.

## 10. At-most-once semantics

The at-most-once unit is the formal identity in §8. A successful claim consumes it.

Runner sequence:

1. Acquire the named per-user state mutex.
2. Load and validate config and state.
3. Read the clock once, resolve timezone, and determine an eligible slot.
4. If outside its window, record/derive MISSED and exit without Codex.
5. Verify the identity and same-day position have not been claimed.
6. Atomically persist CLAIMED.
7. Release the short state lock and start the Codex child process.
8. Classify process and JSONL result.
9. Reacquire the lock, atomically persist final state and structured log, then exit.

If CLAIMED cannot be persisted, Codex does not start. If the child starts and the network fails, it times out, emits turn.failed, or yields uncertain output, the slot stays consumed. If process creation fails after claim, the terminal status is PRELAUNCH_FAILED and there is no automatic retry.

A stale CLAIMED record may be reconciled to ATTEMPTED_UNCONFIRMED, but is never relaunched. Transient errors, usage limits, and unknown results have no retry path.

## 11. Atomic persistence

All state transitions and lastTest updates use a named per-user mutex and atomic writes.

1. Serialize a complete new document to a uniquely named temporary file in the same directory.
2. Flush file contents to disk.
3. Atomically replace the existing file, or atomically move it into place on first creation.
4. Clean up only the temporary file created by this write.

Never truncate or overwrite the current config/state in place. Failure preserves the previous file and prevents formal dispatch. An unsupported schema version is read-only and fails closed.

CLAIMED must reach durable storage before process start. Final status is a second atomic write. A crash between them leaves a claim that prevents duplicate dispatch.

## 12. Task Scheduler contract

Ownership:

- Fixed task name: Codex Slot Ping.
- Description contains the exact marker: Codex Slot Ping; Owner=CodexSlotPing; Schema=1.
- A same-name task with a missing/different marker is a conflict. Install, Disable, and Remove leave it untouched.
- Operations affect only this owned task.

Definition:

- Four enabled daily calendar triggers, one per configured time.
- StartBoundary uses machine-local time without a fixed UTC offset so Windows timezone and daylight-saving rules apply.
- StartWhenAvailable=false.
- MultipleInstancesPolicy=IgnoreNew.
- ExecutionTimeLimit=PT2M.
- RestartCount=0; no RestartOnFailure.
- WakeToRun equals config.wakeToRun and defaults to false.
- RunLevel=LeastPrivilege.
- Principal uses the current interactive user token. No SYSTEM task or stored Windows password.
- Action directly starts the absolute path to CodexSlotPing.Runner.exe. No powershell.exe, pwsh.exe, .ps1, cmd.exe wrapper, or shell command.
- WorkingDirectory is the Runner executable directory, never the user’s current directory.
- The task is visible and considered enabled only after registration and readback succeed.

Install updates and enables an owned task. Disable keeps it but disables it. Remove deletes only the owned task. Exit changes nothing. The task runs in the user context that owns the Codex CLI login; a signed-out session cannot provide that interactive context.

Core reads back and verifies task name, marker, trigger count/times, start-when-available, multiple-instance policy, execution limit, restart count, wake setting, principal, action, and working directory. A mismatch is not a successful install.

Use the native Task Scheduler API/COM surface, not PowerShell as a runtime bridge. Moving the portable directory invalidates the action path; the App reports it and Install scheduler updates the owned task.

## 13. Codex invocation contract

The product uses the installed Codex CLI and existing ChatGPT sign-in. It does not use browser automation, call an inference API directly, install Codex, or store/copy the CLI auth file.

Runner requirements:

- Resolve a directly launchable Codex executable and absolute path. If only a shell script shim exists, report it as not directly launchable; never send the prompt through a shell.
- Run one non-interactive codex exec request in JSONL mode, with the configured model and supported reasoning effort.
- Pass prompt through stdin using the CLI’s explicit stdin-prompt mode. Use a structured argument list, never a concatenated shell command.
- Use an empty app-owned working directory and read-only sandbox. Do not enable file writes or interactive approval.
- Use ephemeral execution so the CLI does not persist a session rollout per slot.
- Limit the Codex child to 100 seconds, kill its process tree on timeout, and reserve the rest of the two-minute task limit for final state/log writes.
- Read stdout as JSONL and stderr independently. Stderr by itself is not failure.
- Install validates the required CLI options. Runner repeats local version, login, and capability checks after claiming and before sending the prompt. A failed check becomes PRELAUNCH_FAILED and is not retried for that identity.

The App may use codex --version and codex login status for environment detection. It reports ChatGPT login only when the CLI explicitly confirms it. It does not run an authentication flow or infer account access from a model catalog.

Test Now uses the same process/parsing contract with a separate invocation ID and state.lastTest. The UI warns that each Test Now is a real request and may count toward usage limits.

## 14. Output and error classification

CONFIRMED_SUCCESS requires:

- Child process started.
- Exit code is zero.
- JSONL is syntactically complete and contains turn.completed.
- The completed turn includes a completed agent_message item.
- No later terminal error or turn.failed invalidates the result.

Timeout, nonzero exit, turn.failed, error event, missing terminal event, malformed/truncated JSONL, or uncertain parse after process start is ATTEMPTED_UNCONFIRMED. The slot remains consumed.

| Evidence | User-facing category | Formal slot state |
| --- | --- | --- |
| No directly launchable CLI executable | Codex not found | PRELAUNCH_FAILED if already claimed |
| CLI confirms no ChatGPT login | Not authenticated | PRELAUNCH_FAILED if already claimed |
| Explicit usage-limit failure | Usage limit reached | ATTEMPTED_UNCONFIRMED |
| Child process cannot be created | Process failed | PRELAUNCH_FAILED |
| Child exceeds deadline | Timeout | ATTEMPTED_UNCONFIRMED |
| turn.failed, error, nonzero exit, or incomplete stream | Process failed or Unknown | ATTEMPTED_UNCONFIRMED |
| Valid completed turn and agent message, exit zero | Test succeeded / Confirmed | CONFIRMED_SUCCESS |

Codex JSONL uses events such as thread.started, turn.started, item.*, turn.completed, turn.failed, and error. Ignore unknown nonterminal types; malformed records prevent success confirmation. A stderr warning never overrides valid success. Skill or YAML parsing warnings are shown as Warnings, not execution failures. Codex Slot Ping never edits files in the user Codex skills directory.

The App shows a short category and safe metadata, not dozens of stderr lines. If raw logging is enabled, the user can open the separate raw file.

## 15. UI structure

Use a compact Windows utility window with one information column and restrained controls. Avoid gradient branding, card grids, glass panels, and dashboard layouts.

Main window:

    Codex Slot Ping

    CODEX
    ● Connected
    Logged in using ChatGPT
    codex-cli <detected version>

    NEXT SLOT
    12:01
    01:36:42

    TODAY
    07:00  ✓ Confirmed
    12:01  ○ Pending
    17:01  ○ Pending
    22:01  ○ Pending

    Model              gpt-5.6-luna
    Reasoning          low
    Scheduler          ON
    Wake computer      OFF

    [Test now] [Open logs]

    Strict slot · missed slots are not replayed.

Next slot is the earliest unclaimed future slot. During an open admission window, it remains next and the UI shows the remaining window. Today’s four statuses are compact rows, not large cards.

Environment and scheduler drift are visible. “Selected model” is not “available” until a real Test Now response confirms it for the current account.

Test Now and raw-log opt-in show usage/privacy notices. Environment failures include an explanation and a copyable official install/login instruction; the App does not execute it.

## 16. Tray behavior

    Codex Slot Ping
    Next: 12:01 · 1h 36m
    Last: 07:00 · Confirmed
    ---
    Open
    Test now
    ---
    Scheduler ✓
    Wake computer
    ---
    Open logs
    Exit

- Open activates the main window.
- Test now follows §§12–14.
- Scheduler and Wake computer reflect actual saved/registered state and user choice.
- Open logs opens the app-owned log directory.
- Exit terminates the App only. It never disables/removes the task.
- On first Exit while the scheduler is on, show: “Closing Codex Slot Ping does not disable scheduled slots.” The user may suppress it later.

## 17. Storage, logging, and privacy

User data is under:

    %LOCALAPPDATA%\CodexSlotPing\

    config.json
    state.json
    logs\YYYY-MM-DD.jsonl
    logs\raw\<run-id>.log       (only when raw logging is explicitly enabled)
    runtime\                     (app-owned transient files only)

The portable directory contains binaries and bundled runtime only. ZIP updates may replace it but never delete user data. Moving/renaming it requires reinstalling the owned task.

Structured logs contain only UTC timestamp/local offset, slot identity or test ID, kind, model, reasoning, result, elapsed milliseconds, exit code, diagnostic category, and token usage if provided by Codex.

Structured logs never contain the full prompt or response. Raw logging is OFF by default. Opt-in warns that output may contain prompt, response, file paths, or personal data. Raw output is stored separately and removed after 14 days; the user can open or delete it. No prompt/response or CLI output is sent to a Codex Slot Ping server.

Test Now updates only state.lastTest. Formal records and lastTest may share state.json only if every update preserves the other section under the same mutex and atomic replacement.

## 18. Environment detection

The App and Runner detect without installing anything:

- Codex command presence and direct-executable resolution.
- Absolute executable path and codex --version.
- Whether codex login status explicitly reports ChatGPT authentication.
- Configured model/reasoning.
- Required exec option support.
- Match between current Windows timezone and SYSTEM or the explicit lock.
- Existence, enabled state, and definition of the owned task.

Detection separates configuration from evidence:

- Selected model is not proof of account availability.
- Version output is not proof of login.
- Login status is not proof that the selected model will answer.
- Only a completed Test Now confirms a real response at that moment.

If Codex is missing, explain how to install it and offer a copyable official command. If not logged in, instruct the user to run the official login flow in a terminal. The app never performs either action.

## 19. Distribution

- Windows x64 portable ZIP.
- C# and WPF on .NET 10.
- Self-contained publish; user does not install .NET separately.
- GitHub release assets: CodexSlotPing-v0.1.0-win-x64.zip and matching .sha256.
- GitHub Actions builds, tests, publishes self-contained, creates ZIP, and computes SHA-256.

No installer, MSI, Winget, signing, or automatic update. Users manually extract and update program files while preserving %LOCALAPPDATA% data.

The project license is MIT.

Proposed README first screen:

    # Codex Slot Ping

    A tiny Windows tray utility for running lightweight Codex CLI prompts at predictable daily time slots.

    - Strict slots — missed runs are never replayed
    - At-most-once — no automatic retry after dispatch
    - Uses your existing Codex / ChatGPT login
    - No API keys and no background scheduler daemon
    - Windows Task Scheduler does the scheduling

    Codex Slot Ping is an independent open-source project and is not affiliated with or endorsed by OpenAI.

## 20. Security boundaries

- Runs with the current interactive user’s permissions and never elevates.
- Runner is started directly; no script host or shell wrapper.
- Codex uses read-only sandbox, an app-owned empty working directory, one prompt, no attachment.
- The utility never reads, copies, displays, transmits, or persists credential/API key values.
- The child environment must not silently switch to an API key or alternate provider endpoint. Remove known credential/provider override variables by name; never log their values. Refuse dispatch if ChatGPT authentication cannot be confirmed.
- Codex CLI manages existing authentication. The app does not implement authentication, browser automation, or token refresh.
- Prompt text is sent to Codex CLI and may be transmitted to its service. Explain this before Test Now and scheduler installation.
- Ownership checks protect third-party same-name tasks. Disable/Remove affect only the exact owned task.
- Config/state parse or write errors fail closed. Never replace broken state with an empty file.
- Respect usage limits; no quota bypass/reset, account switching, or credit purchase.

## 21. Test strategy

This section defines implementation tests. This design-only task creates no tests or product code.

### Core unit tests

- Slot window boundaries at start, start + 3:59.999…, and start + 4:00.
- No early dispatch and no expired-slot catch-up.
- Identity stability across trigger ordering; distinct dates/times remain distinct.
- Same-day position guard after a time edit.
- SYSTEM timezone and fixed-lock match/mismatch.
- Spring-forward invalid and fall-back repeated times.
- Slot spacing, including midnight.
- Every state transition; no terminal-to-pending transition.
- Malformed/unsupported config/state fails closed.

### Persistence and concurrency

- Atomic replace preserves prior data if serialization, flush, or replace fails.
- Interrupted writes are never accepted as partial state.
- Concurrent Runner processes can claim one identity only once.
- CLAIMED persistence precedes process start.
- Test Now changes lastTest but not formal records, next slot, or daily status.

### Process and classification

- JSONL fixtures for success, usage limit, turn.failed, error, unknown events, malformed/truncated lines, and missing terminal event.
- Stderr warnings do not override valid success.
- Nonzero exit, timeout, launch failure, final-state write failure.
- Prompt is never concatenated into a shell command.
- Timeout kills the process tree and leaves time for finalization.

### Windows integration/manual acceptance

- Install an isolated test task and read back each scheduler contract field.
- Same-name/different-marker task collision remains untouched.
- Verify Install, Disable, Remove, and Exit independently.
- Portable directory move is detected and task path can be updated.
- App remains clear/useful with no network, no CLI, or no login.
- Live Test Now is manual and explicit. Automated tests use a fake Codex process and never consume account usage.
- UI exit leaves the task active; Runner executes with App absent.

## 22. Release acceptance criteria

Release only when all are true:

1. Windows x64 ZIP runs on a supported clean Windows machine without separately installed .NET.
2. Exactly App, Core, and Runner product projects exist; no PowerShell runtime or daemon is required.
3. App is a single-instance WPF tray app; Task Scheduler directly starts Runner.
4. Exactly four daily triggers are installed and required settings pass API readback.
5. A same-name task without the exact owner marker is never changed or removed.
6. Missed slots are never replayed, including sleep/offline recovery and DST transitions.
7. Every formal identity is claimed atomically before process start and is never dispatched twice.
8. Launch, network, timeout, quota, parsing, or state-finalization uncertainty never causes retry.
9. Test Now is a real request after user confirmation and changes no formal state.
10. Defaults for model, effort, times, prompt, timezone mode, and WakeToRun match §6.
11. WakeToRun defaults off; UI explains sleep and powered-off behavior.
12. UI Exit does not disable the task.
13. Structured logs contain no prompt/response; raw logs are off until opt-in and disclose sensitivity.
14. Authentication stays in the existing CLI user context; the app stores no credential/API key.
15. Stderr warning alone does not make a completed request fail.
16. README front page states strict no-catch-up, at-most-once, existing login/no API key, no daemon, Task Scheduler, and OpenAI independence disclaimer.
17. No source/runtime dependency uses E:\ or another developer-specific path.
18. Release workflow creates ZIP and matching SHA-256.

## 23. Out of scope for later versions

### v0.2 candidates

- Codex usage/reset viewer, only if its data source is reliable and observed facts are separated from estimates.

### v0.3 candidates

- Multiple profiles.
- Arbitrary slot counts.
- Different model per slot.
- Additional notifications.
- Other providers.

These are outside v0.1 architecture and implementation.

## Spec self-review

These are design-document checks, not implementation verification:

- UI is not scheduler: PASS — Task Scheduler starts Runner.
- No PowerShell runtime: PASS — task action and Codex launch are direct executables.
- No catch-up: PASS — OS setting and Runner admission both reject it.
- No automatic retry: PASS — a claim is terminal for that identity.
- Claim precedes dispatch: PASS — CLAIMED is durable before Process.Start.
- State writes are atomic: PASS — sibling temp, flush, replace.
- No hard-coded E:\ path: PASS — LocalAppData and portable-relative executable path only.
- No hard-coded China timezone: PASS — SYSTEM or user-selected checked ID.
- WakeToRun defaults false: PASS.
- No credential storage: PASS — auth remains with Codex CLI.
- Stderr warning alone is not failure: PASS.
- No quota reset/bypass guarantee: PASS.
- Exactly four slots and one profile: PASS.
- Test Now does not change formal state: PASS.
- Exiting UI does not stop task: PASS.
- Ownership marker protects third-party same-name task: PASS.
- Required sections/release checks present: PASS.
- Placeholder scan: PASS — no unresolved placeholders.

## Necessary design constraints recorded during self-review

1. **Timezone lock.** Task Scheduler uses machine-local timezone/DST when StartBoundary has no offset; an explicit offset fixes the time/offset regardless of machine timezone. To preserve daily wall-clock slots and the SYSTEM default, an explicit lock must match the current machine timezone ID. Mismatch blocks install/dispatch. Scheduling against a different timezone is outside v0.1.
2. **Minimum spacing.** The 3-minute admission window, 2-minute task limit, and IgnoreNew can suppress closely spaced triggers. Four times must be at least six minutes apart, including across midnight.
3. **Prelaunch failure consumes claim.** To avoid ambiguity after a crash around process creation, any claimed slot is never retried, even when process creation fails. This is a strict interpretation of at-most-once.
4. **Clock boundary.** No external trusted time source is introduced. The app fails closed for backward clock changes it has already observed, but cannot prove elapsed real time across an unobserved clock change while powered off. Admission is defined against Windows wall-clock time.

## Reference material

- [Codex CLI non-interactive mode and JSONL output](https://developers.openai.com/codex/non-interactive-mode)
- [Codex CLI source repository and Windows installation options](https://github.com/openai/codex)
- [Task Scheduler StartBoundary timezone semantics](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itrigger-put_startboundary)
- [Task Scheduler daily trigger](https://learn.microsoft.com/en-us/windows/win32/taskschd/dailytrigger)
- [Task Scheduler settings schema](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-settings-tasktype-element)
- [.NET self-contained application publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
