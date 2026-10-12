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
> "Current state" excerpts against the live code before proceeding. Adapt paths,
> symbols and obsolete test expectations within the authorized intent and scope;
> record the adjustment. Stop only for a substantive incompatible contract,
> an unapproved safety change or work outside scope.

## Status

- **Status**: DONE (branch `docs/plan-029-agent-docs`, 1cbdc14..5c07092; Windows and Linux gates green)
- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md. Run **after** plans 021, 023, 024, 026 and 027 if they are scheduled, because each adds one doc sentence. Otherwise rebase over them.
- **Category**: docs
- **Planned at**: commit `5280f78`, 2026-10-03; revised against `729d88f`, 2026-10-11

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
- `AGENTS.md`: the authoritative agent contract. The skill is a concise workflow entry point with links to exact authoritative sections of AGENTS.md/docs/mcp.md. Avoid copying their long contracts; include the few rules needed to choose the next action.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Capabilities/workflow tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Capabilities|FullyQualifiedName~AgentWorkflow|FullyQualifiedName~DocsSync"` | all pass |
| Real help | `dotnet run --project src/SqlHarness.Cli -- --help` | lists registered commands |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `skills/sqlharness/SKILL.md`, `README.md`, `AGENTS.md`, `docs/mcp.md`, `src/SqlHarness.Core/Capabilities.cs` (command discovery and operation-specific output metadata; add sized types accepted by each engine), `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` (update pins), `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json` (re-pin), `tests/SqlHarness.Tests/DocsSyncTests.cs` (create).

**Out of scope**: changing the `-1` exit behavior (document it only), adding an MCP `--max-cell-chars` option (fix the docs instead), and any safety rule change. Where docs and code disagree on a rule, the code wins unless another plan changes it.

## Git workflow

Branch `docs/plan-029-agent-docs`. Commits: `docs(029): skill`, `docs(029): README/AGENTS/mcp`, `feat(029): capabilities lists mcp and ndjson`, `test(029): docs sync guard`. Do NOT push.

## Steps

### Step 1: Capabilities matches the CLI

In `Capabilities.cs`:
- Add `new("mcp", "Serve the Core tools to one MCP client over local stdio (mcp serve).")` after `artifact`.
- Output modes: describe support per operation, not a universal list implying every command accepts every mode. `ndjson` belongs to watch; `json-summary` belongs to measure/compare and conflicts with `json`; MCP stdio is a transport rather than a CLI output mode. Inventory current settings/validators and reflect their exact supported combinations in capabilities and docs; add optional per-command metadata if needed while retaining backward-compatible global discovery fields.
- Parameter types: after `"nvarchar(max)"` add `"nvarchar(n)"`, after `"varchar(max)"` add `"varchar(n)"`, after `"char"` add `"char(n)"`, after `"nchar"` add `"nchar(n)"`, after `"varbinary(max)"` add `"varbinary(n)"` and `"binary(n)"`. First confirm each sized form is accepted by the parser: write a quick test binding `--param x:char(10)=a` through `SqlParameterParser` (`grep -n "SizedTypePattern" src/SqlHarness.Core/SqlSafety.cs`). Add a sized form to the **postgres** list only if `PostgresParameters` accepts it (`grep -n "binary\|char" src/SqlHarness.Core/Postgres/PostgresParameters.cs`).

Update `CapabilitiesCommandTests` pins, and re-pin `discoveryThenSchema` in `byte-budgets.json` from the value the test prints (`--logger "console;verbosity=detailed"`). It must still be ≤ the 8192 budget. If it exceeds 8192, STOP.

**Verify**: capabilities/workflow tests pass.

### Step 2: A sync guard test

Create `tests/SqlHarness.Tests/DocsSyncTests.cs`:
1. `Capabilities_lists_every_registered_command`: get the registered top-level command names from Spectre. The simplest robust way is to run `SqlHarnessCli.Create(...)` with `["--help"]` into a `StringWriter` and parse the COMMANDS section, or reflect on the `Configure` registrations if the CLI exposes them; prefer `--help` output. Compare to `SqlHarnessCapabilitiesProvider.Get().Commands` names. Exclude `--help`/`--version` only.
2. `Documented_examples_match_command_contract`: locate README and skill examples via the repository-file helper. Use a small explicitly delimited set of executable examples (not arbitrary prose/backticks), substitute fixture profile/path placeholders, and feed them through the existing offline command/settings parser with stub dependencies. Assert command/option combinations parse, unsupported output combinations reject, and no DB connection is opened. Check command coverage in the authoritative README command table; allow the digest skill to link to that table rather than listing every command. Add a negative fixture containing a real command with an invalid option so a test proves semantic drift is detected. Do not execute a live SQL example.

**Verify**: these tests FAIL now for the skill/README (missing commands). That is the RED state for Steps 3–4.

### Step 3: Rewrite the skill's stale sections

In `SKILL.md`:
- Frontmatter `description`: add validate, capabilities, artifact, indexes and MCP to the trigger list (keep it one sentence).
- "Readiness and scope": add the offline startup block from AGENTS.md lines 12-21 (`capabilities --json`, `doctor --json`, `validate <profile> --file … --json`), as a short example with a link to AGENTS.md's startup section.
- Add short sections, 3-6 lines each, mirroring AGENTS.md wording: `indexes` (SQL Server missing-index evidence with overlap classes), `artifact` (offline safe sections of a saved benchmark), `--output agent` / `--max-output-bytes` / `--max-cell-chars`, `watch --output ndjson`, and "MCP server" (point to `docs/mcp.md`; no persistent mutations).
- PG diagnostics: add one example `query` that ranks `pg_stat_statements` by `total_exec_time` (top 20, `queryid`, `calls`, `total_exec_time`, `mean_exec_time`, `shared_blks_hit + shared_blks_read`). This replaces the rejected `pgstop` command; note that the extension must be installed.
- PG notes: recommend explicit `verify-full` for remote profiles and link to the authoritative transport-policy section for exact combinations and legacy behavior.
- Session contract: summarize same-session setup and native temporary objects in a few sentences; link to AGENTS.md's Benchmark setup contract for syntax/proof limits, including table-variable batch visibility and PostgreSQL `search_path`. Do not copy the large rule list.
- Exit codes: add "`-1` (255 on POSIX): command-line parse error in text mode; JSON/agent modes return `2`."

**Verify**: skill examples parse offline and authoritative documentation links resolve in the repository.

### Step 4: README, AGENTS.md, docs/mcp.md

- README command table: add rows for `indexes`, `artifact`, `capabilities`, `doctor`, `validate` and `mcp serve`, each with a one-line purpose taken from `Capabilities.cs`.
- README roadmap: replace the overlap-analysis bullet with the current planned items: the compare regression verdict (`plans/051-compare-regression-verdict.md`) and the approval tray (`docs/superpowers/plans/2026-09-26-audit-08-windows-approval-tray.md`), each marked "designed, not implemented". `pgstop` is rejected; do not list it.
- README "Results to fill from real runs" table with `TBD` rows (`:257-262`): replace it with one sentence saying measured examples will be added from real runs. Do not invent numbers.
- Every exit-code list (README `:242`, AGENTS.md "Safety contract" exit codes, SKILL.md): add the `-1` line.
- Parameter type lists (README `:109`, AGENTS.md `:78`, SKILL.md): add the sized forms confirmed in Step 1.
- Inspect live MCP serve options and catalog limits before editing the cell-limit sentence. Describe the actual default and supported configuration path; do not advertise an option that does not exist or call a configurable limit fixed. Verify an offline settings test agrees with the documented value.
- AGENTS.md was rewritten on 2026-10-11 (plan-envelope jargon and the foreign password variable are gone). Only add the `-1` exit code and sized parameter types there; keep it a short contract that links to docs.

**Verify**: `DocsSyncTests` pass. `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

### Step 5: Honest framing and pins that do not force duplication (added 2026-10-11 after the rule review)

- **Framing.** In CLI mode the agent shares the shell's environment: it can read the password variable, call `az`, and use `--unsafe-direct`. State in README Safety contract and the skill: "The classifier and the mutation flags prevent accidents. For enforcement, point profiles at a least-privilege (reader) database principal." Fix the comment in `src/SqlHarness.Core/Targets/` that calls the env password "the only operator-held secret the caller cannot already obtain" (true only for MCP without a shell).
- **Wording.** `--allow-mutation --confirm-database` is a deliberate-act check supplied by the agent, recorded after the user's approval; describe it that way. In request-scope MCP the operator's `--allow-profile` list is the approval for switching profiles; say so where AGENTS/README require a new user request per profile. Describe MCP artifact/snapshot owner checks as namespacing, not access control.
- **Pins.** Replace verbatim-sentence pins with structure: `ContractsTests` checks that README and the skill link the canonical sections (as the AGENTS.md link test does) and that key commands/options appear; drop long sentences pinned in two documents (`Documentation_covers_measure_parameter_sets`, `Documentation_covers_query_store_top_consumers`). `CapabilitiesCommandTests` asserts structured capability fields instead of the PostgreSQL TEMP-proof paragraph. Remove the Polish phrase pinned in `docs/mcp.md` ("Zakres dowodów") and translate that section to English.

## Test plan

`DocsSyncTests` covers command discovery and semantic example/option validation, including one invalid-example fixture; updated capabilities pins and the re-pinned byte budget. Verify both Windows/Linux gates after edits.

## Done criteria

- [ ] `grep -c "validate" skills/sqlharness/SKILL.md` ≥ 1; `grep -n "verify-full" skills/sqlharness/SKILL.md` → match
- [ ] `grep -n "Missing-index overlap analysis against existing indexes" README.md` → no match
- [ ] Semantic documentation examples/command coverage tests pass; both Windows and Linux full gates pass
- [ ] `plans/README.md` row updated

## STOP conditions

- A discovered safety-policy conflict cannot be resolved from the approved contracts or coordinating plan within this scope. Report the conflicting evidence; routine stale documentation is corrected to the established behavior.
- The capabilities change pushes `discoveryThenSchema` over its 8192-byte budget.
- Spectre's `--help` output cannot be parsed reliably. Fall back to a hard-coded expected list in the test with a comment, and report it.

## Maintenance notes

- The documentation guard validates command coverage and runnable examples. Backtick formatting and verbatim wording are not behavior contracts.
- The skill stays a concise digest with authoritative links. Update only affected summary rules and examples when a contract changes; avoid duplicating large sections.
