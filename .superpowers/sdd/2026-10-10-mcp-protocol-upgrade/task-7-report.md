# Task 7: Real-client acceptance script

## Result

Implemented the stdio frame tap and manual acceptance driver. The final run passed against the worktree-local SQLHarness build and the installed Claude Code and Codex clients. No user client configuration was edited. Acceptance frame logs remain in the local temporary directories below because they contain tool results.

## Client resolution and login

- Claude resolves to `C:\Users\rgone\scoop\apps\nodejs-lts\current\bin\claude.ps1`; the corresponding PATH application launcher is `claude.cmd` in that directory. `claude auth status` returned `loggedIn: true`, exit 0.
- Codex first resolves to a PowerShell function wrapper, which forwards to `unsnooze.js` and the PATH launcher. The PATH application launcher used by the driver is `C:\Users\rgone\scoop\apps\nodejs-lts\current\bin\codex` (also available as `codex.cmd`). `codex login status` returned `Logged in using ChatGPT`, exit 0.
- `CODEX_HOME` was unset, so Codex used its default home. The client emitted an existing warning that `default_mode_request_user_input` in the user's config is ignored. This acceptance run did not change that config.

## Commands and verification

| Command | Result |
| --- | --- |
| `dotnet publish src\SqlHarness.Cli -c Release -p:PublishTrimmed=false -o artifacts\task7-local` | Pass; built to the worktree-local output directory. |
| `python -I -m py_compile scripts/mcp-client-acceptance-tap.py` | Pass, exit 0. |
| PowerShell `Parser.ParseFile` on `scripts\mcp-client-acceptance.ps1` | Pass; no parse errors. |
| `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness (Resolve-Path 'artifacts\task7-local\sqlharness.exe') -SkipClaude -SkipCodex` | Pass, exit 0; confirms startup, binary-path validation, and skip-mode handling. |
| `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness (Resolve-Path 'artifacts\task7-local\sqlharness.exe') -Profile local-playground` | Pass, exit 0; final real-client acceptance below. |
| `git diff --check` | Pass. |

The first live attempt exposed a Codex configuration quoting issue on Windows: its CLI received the argument array as a string. The driver now emits TOML literal strings for the one-off `-c` values and invokes the PATH application launcher directly. The driver also checks initialize-response presence only for Codex; Claude's negotiated revision is checked through `server/discover` and the tool-call metadata.

## Final real-client acceptance

| Client | Check | Expected | Actual | Pass |
| --- | --- | --- | --- | --- |
| Claude Code | client exit code | 0 | 0 | true |
| Claude Code | tools/call count | 1 | 1 | true |
| Claude Code | negotiated | 2026-07-28 | 2026-07-28 | true |
| Claude Code | capabilities.protocolVersion | 2026-07-28 | 2026-07-28 | true |
| Claude Code | clientInfo.name | non-empty | claude-code | true |
| Claude Code | capabilities response | yes | yes | true |
| Claude Code | capabilities call error | false | false | true |
| Claude Code | server/discover used | yes | yes | true |
| Codex | client exit code | 0 | 0 | true |
| Codex | tools/call count | 1 | 1 | true |
| Codex | initialize response | yes | yes | true |
| Codex | negotiated | 2025-06-18 | 2025-06-18 | true |
| Codex | capabilities.protocolVersion | 2025-06-18 | 2025-06-18 | true |
| Codex | clientInfo.name | non-empty | codex-mcp-client | true |
| Codex | capabilities response | yes | yes | true |
| Codex | capabilities call error | false | false | true |

Frame logs were written under `D:\temp\sqlharness-mcp-acceptance-ec33c80ada414cabb03710e5e941e659` for the initial attempt and `D:\temp\sqlharness-mcp-acceptance-c3610cc7aaa9447bbd07dc111dde665a` for the passing run. They contain protocol tool results and were not copied into this report.

## Limits

- The live check used the closed `local-playground` profile and called only the target-free capabilities tool; it did not run a database operation.
- This is a manual acceptance check of the installed client versions and does not replace CI or cross-platform verification.
- Codex reported the ignored user-config setting warning described above; it did not affect the acceptance result.
