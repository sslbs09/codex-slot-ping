# Codex CLI Contract Evidence

Captured on 2026-10-11 for the implementation environment. This file records compatibility evidence, not account model availability.

## Local installation

- `where.exe codex` resolves the npm command shim and a separate app-installed executable.
- PowerShell resolves the npm command; its package-matched native Windows executable reports `codex-cli 0.153.4`.
- The separate app-installed executable reports `codex-cli 0.162.0-alpha.17.2`. It is not selected as a silent fallback.
- No machine-specific executable paths are recorded.
- Read-only `codex login status` returned `Logged in using ChatGPT`. No credential values were read or recorded.

## Local help evidence

`codex --help` confirms `--model`, `--sandbox` with `read-only`, `--ask-for-approval` with `never`, and `--config key=value`.

`codex exec --help` confirms:

- `-` explicitly reads the initial prompt from stdin.
- `--json` prints JSONL events to stdout.
- `--ephemeral` avoids persisting session files.
- `--ignore-user-config` avoids loading the Codex config file while retaining authentication through `CODEX_HOME`.
- `--skip-git-repo-check` allows an app-owned empty working directory outside a Git repository.

The current official references agree with the local help: [non-interactive mode](https://developers.openai.com/codex/non-interactive-mode), [CLI command reference](https://learn.chatgpt.com/docs/developer-commands?surface=cli), and [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

The documented invocation shape is:

```text
<resolved-native-codex.exe>
  --ask-for-approval never
  --sandbox read-only
  --model <configured-model>
  --config model_reasoning_effort="<configured-effort>"
  exec
  --ignore-user-config
  --skip-git-repo-check
  --ephemeral
  --json
  -
```

The application will pass arguments through `ProcessStartInfo.ArgumentList`, use an empty app-owned working directory, write the prompt only to stdin, and enforce the frozen 100-second child deadline. The model remains a selection request. A successful login check or documented reasoning value does not prove model access for this account.

The configuration reference lists `low`, `medium`, `high`, `xhigh`, `max`, and `ultra` as possible `model_reasoning_effort` values and says availability depends on the model and client. The CLI's help does not enumerate model-specific effort support. The implementation therefore treats this documented set as CLI compatibility evidence only; it must not claim account/model availability or silently substitute an effort.

## Read-only prototype comparison

The existing prototype's `setup.ps1` resolves a native binary matching the npm package, checks version/login and required options, and its `common.ps1` uses `ProcessStartInfo.ArgumentList` with separate stdout/stderr streams. Its formal invocation passes the prompt as a positional argument, sets `--ask-for-approval on-request`, uses `--cd` for its private workspace, and its setup default is a 110-second timeout. Its scheduled action is a PowerShell script. The frozen Codex Slot Ping contract takes precedence: direct Runner task action, stdin prompt sentinel, approval `never`, an empty app-owned working directory, and a 100-second child deadline. The prototype was not modified.

## Existing observed output evidence

Existing prototype logs were inspected programmatically in place. Only event types, usage field names/counts, result categories, and booleans for skill/YAML warning presence were emitted; prompt, response, full stderr, identifiers, and credential values were not displayed. The records confirm a successful `thread.started` → `turn.started` → `item.completed` (`agent_message`) → `turn.completed` stream with usage, successful completion despite skill/YAML stderr warnings, and a usage-limit phrase in a failed turn. Later committed fixtures will redact run identifiers and response text and use deterministic test data where needed.

No live Codex prompt was sent during this evidence capture.
