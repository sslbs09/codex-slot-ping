# Codex Slot Ping

Codex Slot Ping is an independent Windows tray utility that runs a small number of user-configured Codex CLI requests at fixed local times.

> **Engine baseline only.** v0.1 uses four fixed daily slots. It does not model rolling usage windows, weekly quota exhaustion, or manual Codex activity. Scheduling Semantics v2 is required before a public `v0.1.0` release.

## What it does

- Runs four configurable local-time slots through Windows Task Scheduler.
- Uses an existing Codex CLI installation and its existing ChatGPT login.
- Claims each formal slot before dispatch. A claimed slot is never retried, and missed slots are never replayed.
- Runs each request in a short-lived Runner process. The App can close while Task Scheduler remains active.
- Stores configuration, execution state, and logs under `%LOCALAPPDATA%\CodexSlotPing`.
- Ships as a self-contained Windows x64 ZIP; installing a separate .NET runtime is not required.

Codex Slot Ping does not install Codex, use API keys, automate a browser, or run a background daemon. Task Scheduler directly starts the bundled Runner.

## Requirements

- Windows x64.
- The official Codex CLI installed and logged in with the ChatGPT account you intend to use.
- Internet access when Codex runs a request.

Codex Slot Ping does not install or sign in to Codex for you. If needed, install the official CLI and run `codex login` yourself. Do not add API keys for this utility.

## Use the portable ZIP

1. Extract the ZIP to a folder you control.
2. Start `CodexSlotPing.App.exe`.
3. Set the four local times, model selection request, reasoning effort, and prompt in **Settings**.
4. Select **Test now** only after reading the confirmation. It sends the configured prompt as a real Codex request and may count toward usage limits.
5. Select **Install / update scheduler** only when you want the formal slots enabled. The confirmation explains that future eligible slots send the configured prompt.

The ZIP layout is:

```text
CodexSlotPing.App.exe
... self-contained App files ...
Runner/
  CodexSlotPing.Runner.exe
  ... self-contained Runner files ...
```

Keep the `Runner` folder beside the App. Moving or renaming the extracted folder changes the Runner path; open the App and update the owned scheduler task afterward. User data remains in `%LOCALAPPDATA%` and is not part of the ZIP.

## Scheduling behavior

The v0.1 engine uses four strict local-time slots. Each slot has a three-minute admission period. The task is not configured to run after a missed start, the Runner rejects expired slots, and a durable claim is written before starting Codex. If the request may have reached Codex, the slot remains consumed even if its result cannot be confirmed.

The App reports Scheduler state from Windows Task Scheduler readback. Closing the window hides it to the tray. **Exit** closes only the App; it does not disable scheduled slots. Use **Disable scheduler** or **Remove scheduler** explicitly to change the owned task.

## Data and privacy

The prompt is sent to Codex when Test Now is confirmed or an eligible formal slot is dispatched. Configuration and formal slot state stay under `%LOCALAPPDATA%\CodexSlotPing`. Structured logs omit prompt and response text. Raw output capture is optional, remains local, and is removed after 14 days.

Codex Slot Ping does not read, copy, or modify Codex authentication files. It relies on the CLI's existing login.

## Project status

The fixed four-slot scheduler is an engine baseline, not the complete public-release scheduling policy. Dynamic rolling windows, weekly quota exhaustion, manual Codex usage, reset provenance, and uncertain-state handling remain for Scheduling Semantics v2. Do not publish a public `v0.1.0` release until that design is complete and accepted.

**Independence notice:** Codex Slot Ping is an independent community utility. It is not affiliated with, endorsed by, or sponsored by OpenAI.
