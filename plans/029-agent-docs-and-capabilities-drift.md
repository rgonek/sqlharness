# Plan 029: The agent skill, README, AGENTS.md and `capabilities` describe the CLI that actually ships, and a test keeps them in sync

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- skills/sqlharness/SKILL.md README.md AGENTS.md docs/mcp.md src/SqlHarness.Core/Capabilities.cs src/SqlHarness.Cli/SqlHarnessCli.cs tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md. Run **after** plans 021, 023, 024, 026 and 027 if they are scheduled, because each adds one doc sentence. Otherwise rebase over them.
- **Category**: docs
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Coding agents are SQLHarness's primary users, and `skills/sqlharness/SKILL.md` is what gets loaded into their context. It was last changed at commit `5e65594` and predates the MCP server, `validate`, `capabilities`, `doctor`, `artifact`, `indexes`, `--output agent`, watch NDJSON and the plan-011 syntax extensions. Concretely:

- The skill's readiness steps are only `Get-Command` and `--help`. It never mentions the offline startup path AGENTS.md prescribes (`capabilities --json`, `doctor --json`, `validate`).
- Its PostgreSQL TLS note (line ~121) describes only the legacy `trustServerCertificate` mapping. It never mentions `sslMode: verify-full` or `rootCertificate`, so an agent following it configures remote PG **without certificate verification**.
- Its session-temp rules (line ~132) omit table variables, `SET @v`, `TRUNCATE`, `ALTER TABLE #t` and the PG `pg_temp.` / `search_path` caveat, so agents rewrite batches that are actually allowed.

Other drift:
- **README command table** (`README.md:219-234`) lists 12 of the 17 registered commands. It is missing `indexes`, `artifact`, `capabilities`, `doctor`, `validate`, and the `mcp serve` branch.
- **README roadmap** (`:275-277`) lists overlap analysis, which already ships as `indexes`.
- **Gain sentence** (`:255`): if plan 027 has landed, its docs step owns that sentence; skip it here.
- `capabilities` omits the `mcp` command and the `ndjson` output mode (`Capabilities.cs:34-52`, `:89`) although both exist.
- **Exit code -1.** Text-mode command-line parse errors exit `-1` (255 on POSIX) (`SqlHarnessCli.cs:35-36`; pinned by tests "Spectre owns unknown options and returns -1"). No exit-code list documents this.
- **MCP cell limit.** `docs/mcp.md:92` and `AGENTS.md:142` say "an operator may configure at most 4096" cell characters, but `mcp serve` has no such option (`McpServeCommand.cs:29-51`), and the catalog always uses the default.
- **Sized parameter types.** The parser accepts `nvarchar(n)`, `varchar(n)`, `char(n)`, `nchar(n)`, `varbinary(n)` and `binary(n)` (`SqlSafety.cs` `SizedTypePattern` ~`:1542`), but no list mentions them.
- **AGENTS.md jargon.** `AGENTS.md:97` ends with "This is not the plan 03 agent envelope." (unresolvable for outside agents), and `AGENTS.md:105` names `CIVICLENS_POSTGRES_PASSWORD`, a variable from another project.

## Current state

- `src/SqlHarness.Cli/SqlHarnessCli.cs:44-65`: Spectre registrations, the source of truth for command names. Read them.
- `src/SqlHarness.Core/Capabilities.cs:30-95`: the `SqlHarnessCapabilities` construction: the command list (17 entries, no `mcp`), the engine parameter-type lists, a limits dictionary and output modes `["text", "json", "json-summary", "agent"]`.
- `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` pins capabilities wording **exactly** (plan 011/T6). `tests/SqlHarness.Tests/AgentWorkflowTests.cs` + `Fixtures/AgentWorkflow/byte-budgets.json` pin `discoveryThenSchema` output bytes (7100), which includes capabilities JSON. Adding entries changes both, which is expected: update them deliberately.
- `skills/sqlharness/SKILL.md` (182 lines): frontmatter `description:` plus sections "Readiness and scope", "Safe workflow", parameter types, PG notes, the session contract, exit codes.
- `AGENTS.md`: the authoritative agent contract. Wording in the skill should **link to or mirror** AGENTS.md, never invent new rules.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Capabilities/workflow tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Capabilities|FullyQualifiedName~AgentWorkflow|FullyQualifiedName~DocsSync"` | all pass |
| Real help | `dotnet run --project src/SqlHarness.Cli -- --help` | lists registered commands |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `skills/sqlharness/SKILL.md`, `README.md`, `AGENTS.md`, `docs/mcp.md`, `src/SqlHarness.Core/Capabilities.cs` (add `mcp` command and `ndjson` output mode; add sized types to the SQL Server/PG lists), `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` (update pins), `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json` (re-pin), `tests/SqlHarness.Tests/DocsSyncTests.cs` (create).

**Out of scope**: changing the `-1` exit behavior (document it only), adding an MCP `--max-cell-chars` option (fix the docs instead), and any safety rule change. Where docs and code disagree on a rule, the code wins unless another plan changes it.

## Git workflow

Branch `docs/plan-029-agent-docs`. Commits: `docs(029): skill`, `docs(029): README/AGENTS/mcp`, `feat(029): capabilities lists mcp and ndjson`, `test(029): docs sync guard`. Do NOT push.

## Steps

### Step 1: Capabilities matches the CLI

In `Capabilities.cs`:
- Add `new("mcp", "Serve the Core tools to one MCP client over local stdio (mcp serve).")` after `artifact`.
- Output modes: add `"ndjson"` (used by `watch --output ndjson`).
- Parameter types: after `"nvarchar(max)"` add `"nvarchar(n)"`, after `"varchar(max)"` add `"varchar(n)"`, after `"char"` add `"char(n)"`, after `"nchar"` add `"nchar(n)"`, after `"varbinary(max)"` add `"varbinary(n)"` and `"binary(n)"`. First confirm each sized form is accepted by the parser: write a quick test binding `--param x:char(10)=a` through `SqlParameterParser` (`grep -n "SizedTypePattern" src/SqlHarness.Core/SqlSafety.cs`). Add a sized form to the **postgres** list only if `PostgresParameters` accepts it (`grep -n "binary\|char" src/SqlHarness.Core/Postgres/PostgresParameters.cs`).

Update `CapabilitiesCommandTests` pins, and re-pin `discoveryThenSchema` in `byte-budgets.json` from the value the test prints (`--logger "console;verbosity=detailed"`). It must still be ≤ the 8192 budget. If it exceeds 8192, STOP.

**Verify**: capabilities/workflow tests pass.

### Step 2: A sync guard test

Create `tests/SqlHarness.Tests/DocsSyncTests.cs`:
1. `Capabilities_lists_every_registered_command`: get the registered top-level command names from Spectre. The simplest robust way is to run `SqlHarnessCli.Create(...)` with `["--help"]` into a `StringWriter` and parse the COMMANDS section, or reflect on the `Configure` registrations if the CLI exposes them; prefer `--help` output. Compare to `SqlHarnessCapabilitiesProvider.Get().Commands` names. Exclude `--help`/`--version` only.
2. `Skill_and_readme_mention_every_command`: locate `skills/sqlharness/SKILL.md` and `README.md` by walking up from `AppContext.BaseDirectory` (same helper as `ReleaseWorkflowTests.FindRepositoryFile`). Assert each command name appears as `` `name` `` (backticked) in both files.

**Verify**: these tests FAIL now for the skill/README (missing commands). That is the RED state for Steps 3–4.

### Step 3: Rewrite the skill's stale sections

In `SKILL.md`:
- Frontmatter `description`: add validate, capabilities, artifact, indexes and MCP to the trigger list (keep it one sentence).
- "Readiness and scope": add the offline startup block from AGENTS.md lines 12-21 (`capabilities --json`, `doctor --json`, `validate <profile> --file … --json`), copied **verbatim** from AGENTS.md.
- Add short sections, 3-6 lines each, mirroring AGENTS.md wording: `indexes` (SQL Server missing-index evidence with overlap classes), `artifact` (offline safe sections of a saved benchmark), `--output agent` / `--max-output-bytes` / `--max-cell-chars`, `watch --output ndjson`, and "MCP server" (point to `docs/mcp.md`; the 11 tools; no persistent mutations).
- PG notes: replace the legacy-only TLS paragraph with AGENTS.md's PostgreSQL transport bullet (the `sslMode` / `rootCertificate` / `verify-full` text).
- Session contract: replace the `#temp`-only list with AGENTS.md's "Benchmark setup contract" SQL Server and Postgres bullets, verbatim.
- Exit codes: add "`-1` (255 on POSIX): command-line parse error in text mode; JSON/agent modes return `2`."

**Verify**: `DocsSyncTests` skill assertions pass.

### Step 4: README, AGENTS.md, docs/mcp.md

- README command table: add rows for `indexes`, `artifact`, `capabilities`, `doctor`, `validate` and `mcp serve`, each with a one-line purpose taken from `Capabilities.cs`.
- README roadmap: replace the overlap-analysis bullet with the current planned items, linking `plans/013-regression-implementation.md` (regress), `plans/014-pg-statements-implementation.md` (pgstop) and `docs/superpowers/plans/2026-09-26-audit-08-windows-approval-tray.md` (approval tray), each marked "designed, not implemented".
- README "Results to fill from real runs" table with `TBD` rows (`:257-262`): replace it with one sentence saying measured examples will be added from real runs. Do not invent numbers.
- Every exit-code list (README `:242`, AGENTS.md "Safety contract" exit codes, SKILL.md): add the `-1` line.
- Parameter type lists (README `:109`, AGENTS.md `:78`, SKILL.md): add the sized forms confirmed in Step 1.
- `docs/mcp.md:92` and `AGENTS.md:142`: change the cell sentence to "Cells are clipped at 512 characters; this is fixed in v1."
- `AGENTS.md:97`: replace "This is not the plan 03 agent envelope." with "The agent output envelope is described in docs/superpowers/specs/2026-09-26-agent-output-contract.md." `AGENTS.md:105`: remove the `CIVICLENS_POSTGRES_PASSWORD` mention, keeping the sentence's point that the TLS proof reads only `SQLHARNESS_PG_TLS_PROOF`.

**Verify**: `DocsSyncTests` pass. `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

`DocsSyncTests` (2 tests) plus updated capabilities pins and the re-pinned byte budget.

## Done criteria

- [ ] `grep -c "validate" skills/sqlharness/SKILL.md` ≥ 1; `grep -n "verify-full" skills/sqlharness/SKILL.md` → match
- [ ] `grep -n "Missing-index overlap analysis against existing indexes" README.md` → no match
- [ ] `grep -n "CIVICLENS" AGENTS.md` → no match
- [ ] `DocsSyncTests` and the full gate pass
- [ ] `plans/README.md` row updated

## STOP conditions

- AGENTS.md and the code disagree on a **safety** rule while you copy text. Report it; do not pick one.
- The capabilities change pushes `discoveryThenSchema` over its 8192-byte budget.
- Spectre's `--help` output cannot be parsed reliably. Fall back to a hard-coded expected list in the test with a comment, and report it.

## Maintenance notes

- `DocsSyncTests` makes adding a command without documenting it a test failure. That is intended.
- The skill should stay a **digest** of AGENTS.md. When AGENTS.md changes a rule, update the skill in the same commit.
