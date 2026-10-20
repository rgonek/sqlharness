# Plan 092: Configurable default execution timeout in config.json and Dashboard Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Execute task-by-task with scoped review and final whole-branch review.

**Goal:** Allow operators to configure a global default SQL execution timeout in `~/.sqlharness/config.json` under an `execution` section (`execution.defaultTimeoutSeconds`), manage it visually in the Dashboard Settings page, and use it as the fallback across CLI commands and MCP tools whenever an explicit `--timeout` / `timeout` argument is omitted.

**Architecture:**
- Extend `SqlHarnessConfig` with an `ExecutionConfig` record holding `DefaultTimeoutSeconds` (default: 30, valid range: 1..300 seconds).
- Update `SqlHarnessConfigLoader` strict parsing, completion checks, and validation rules; ensure existing config files without an `execution` block load cleanly with defaults.
- Propagate `config.Execution.DefaultTimeoutSeconds` to CLI commands (`query`, `mutation`, `measure`, `compare`, `watch`, `snapshot`) and MCP tools (`sqlharness_query`, `sqlharness_mutation`, `sqlharness_measure`, `sqlharness_compare`, `sqlharness_watch`, `sqlharness_snapshot`) as the fallback when the timeout option/argument is not explicitly provided.
- Expose `execution.defaultTimeoutSeconds` in the Dashboard Settings API, update frontend TypeScript `Settings` types, and add a dedicated "SQL execution" settings card with an atomic numeric input and status feedback in `SettingsPage.tsx`.

**Tech Stack:** .NET 10, C#, xUnit; React 19, TypeScript, TanStack Query, Vitest, Tailwind CSS, shadcn/ui.

**Spec:** [Fix contract below](#fix-contract).

## Status and dependencies

- TODO — plan only. No blocking dependencies. Priority P2. Planning-only authorization.

## Fix contract

### 1. Configuration schema and loader (`SqlHarness.Core`)
- Add `ExecutionConfig` record:
  ```csharp
  public sealed record ExecutionConfig
  {
      public const int MinimumTimeoutSeconds = 1;
      public const int MaximumTimeoutSeconds = 300;
      public const int DefaultTimeoutSeconds = 30;

      public int DefaultTimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
  }
  ```
- Add `public ExecutionConfig Execution { get; init; } = new();` to `SqlHarnessConfig`.
- In `SqlHarnessConfigLoader`:
  - `IsComplete`: require `config.Execution is not null`.
  - Deserialization of an existing `config.json` that omits the `"execution"` key initializes `Execution` to default without error or warning.
  - Validation: if `config.Execution.DefaultTimeoutSeconds is < 1 or > 300`, record `SqlHarnessConfigFieldError("execution.defaultTimeoutSeconds", "Must be between 1 and 300.")`.
  - Serializer (`SqlHarnessConfigWriter`): outputs the `"execution"` object with `"defaultTimeoutSeconds"` in standard web camelCase JSON.

### 2. Runtime fallback in CLI and MCP
- **CLI Commands:**
  - `QueryCommand`, `MeasureCommand`, `CompareCommand`, `MutationCommand`, `WatchCommand`, `SnapshotCommand`:
    - When `--timeout` is not explicitly passed on the command line (or set to default), resolve timeout from `_config.Execution.DefaultTimeoutSeconds`.
    - If `--timeout` is explicitly provided, validate it against `1..300` and use the explicit value.
- **MCP Tools:**
  - In `McpToolCatalog` and `McpOperationMapper`:
    - When `timeout` argument is omitted by the caller (or null/default marker), resolve timeout from `process.Config.Execution.DefaultTimeoutSeconds`.
    - An explicit `timeout` argument in the tool call continues to override the default, validated against `1..300`.
- **Core Safety Bounds:**
  - `ValidateBounds` in `SqlHarnessModule` continues to strictly enforce `query.TimeoutSeconds is < 1 or > 300` as a hard safety ceiling.

### 3. Dashboard API and Settings UI (`SqlHarness.Dashboard`)
- **Backend:**
  - `DashboardSettings.Read` and `DashboardSettings.PutAsync` automatically serialize/deserialize the new `execution` block as part of `SqlHarnessConfig`.
  - `PutAsync` validates field errors and returns HTTP 400 with `execution.defaultTimeoutSeconds` error when out of bounds.
- **Frontend types (`src/SqlHarness.Dashboard/ui/src/api/types.ts`):**
  - Update `Settings` type to include:
    ```ts
    execution: { defaultTimeoutSeconds: number }
    ```
- **Frontend UI (`src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`):**
  - Add `"execution"` to `CardKey`.
  - Add `setExecution = (next: Partial<Settings["execution"]>) => commit({ ...draft, execution: { ...draft.execution, ...next } }, "execution")`.
  - Render a new Card:
    - Title: **SQL execution**
    - Description: "Default command timeout for queries, mutations, benchmarks, and watches when no explicit timeout is passed. Capped at 1..300 seconds."
    - Control: `<NumberField id="execution-default-timeout" label="Default execution timeout (seconds)" value={draft.execution.defaultTimeoutSeconds} min={1} max={300} error={fieldErrors["execution.defaultTimeoutSeconds"]} onCommit={value => setExecution({ defaultTimeoutSeconds: value })} />`
    - Feedback: `<StatusLine status={status.execution} />`

## Global Constraints

- Never weaken the hard safety limit: execution timeout must remain bounded within `1..300` seconds in all surfaces.
- Preserves zero-regression compatibility for existing `~/.sqlharness/config.json` files lacking the `"execution"` section.
- Atomic file writing and strict 0700 permissions via `SqlHarnessConfigWriter` remain mandatory.
- Full verification: Windows `just verify` and branch formatting script.

## Review Focus

1. Existing `config.json` without `"execution"` section loads as `SqlHarnessConfigStatus.Valid` with `DefaultTimeoutSeconds == 30`.
2. Out-of-range value (e.g. 0 or 301) marks config `Invalid` and returns expected field error in both Core parser and Dashboard PUT response.
3. CLI commands without `--timeout` flag pick up custom `defaultTimeoutSeconds` from `config.json`.
4. MCP tool calls without `timeout` argument use custom `defaultTimeoutSeconds` from `config.json`.
5. Dashboard Settings card updates `execution.defaultTimeoutSeconds`, handles validation errors smoothly, and persists to disk.

---

### Task 1: Core configuration schema, parsing, validation, and serialization

**Files:** `src/SqlHarness.Core/SqlHarnessConfig.cs`, `SqlHarnessConfigLoader.cs`, `SqlHarnessConfigWriter.cs`; `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`.

**Interfaces:** `ExecutionConfig` record, `SqlHarnessConfig.Execution`, `SqlHarnessConfigLoader.Parse` validation.

- [ ] Add failing tests in `SqlHarnessConfigTests.cs`:
  - `Execution_timeout_defaults_when_missing_and_custom_value_round_trips`
  - `Execution_timeout_out_of_range_fails_validation` (verifying `< 1` and `> 300`)
  - `Execution_timeout_accepts_inclusive_bounds` (verifying 1 and 300)
- [ ] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"`; confirm RED failures.
- [ ] Implement `ExecutionConfig`, update `SqlHarnessConfig`, `SqlHarnessConfigLoader` (`IsComplete` and range check), and `SqlHarnessConfigWriter`.
- [ ] Rerun tests; confirm GREEN. Commit `feat(092): add execution default timeout to config schema and loader`.

### Task 2: CLI and MCP default timeout resolution

**Files:** `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`, `src/SqlHarness.Cli/Commands/MutationCommand.cs`, `src/SqlHarness.Cli/Commands/WatchCommand.cs`, `src/SqlHarness.Cli/Commands/SnapshotCommand.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `src/SqlHarness.Mcp/McpOperationMapper.cs`; `tests/SqlHarness.Tests/Cli/` tests and MCP tool tests.

**Interfaces:** CLI command execution falling back to `config.Execution.DefaultTimeoutSeconds`, MCP tool dispatch falling back to `process.Config.Execution.DefaultTimeoutSeconds`.

- [ ] Add failing tests verifying that when `--timeout` (CLI) or `timeout` (MCP) is omitted, the operation uses the configured default from `config.json`, and when explicitly passed, the explicit parameter takes precedence.
- [ ] Run CLI and MCP tests; confirm RED failures.
- [ ] Implement fallback resolution in CLI command runners and MCP tool mapper.
- [ ] Rerun tests; confirm GREEN. Commit `feat(092): resolve default execution timeout from config in CLI and MCP`.

### Task 3: Dashboard API contract, UI card, and tests

**Files:** `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`, `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx`, `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`.

**Interfaces:** `Settings.execution` in TypeScript, Dashboard Settings contract JSON, `SettingsPage` SQL execution card.

- [ ] Add failing contract test in `DashboardContractTests.cs` verifying `execution.defaultTimeoutSeconds` is present in `SettingsResponse`.
- [ ] Add failing UI test in `SettingsPage.test.tsx` verifying that the "SQL execution" card renders, displays the current value, commits on change, and shows field errors on invalid input.
- [ ] Run tests; confirm RED failures.
- [ ] Update `types.ts`, `SettingsPage.tsx` with the new card and mutation logic.
- [ ] Run `npm --prefix src/SqlHarness.Dashboard/ui run check` and vitest; confirm GREEN.
- [ ] Rerun C# dashboard contract tests; confirm GREEN. Commit `feat(092): add SQL execution timeout card to dashboard settings`.

### Task 4: Documentation, verification evidence, and index updates

**Files:** `docs/mcp/runtime.md`, `README.md`, `plans/README.md`, `plans/092-verification-evidence.md`.

- [ ] Update documentation describing `execution.defaultTimeoutSeconds` in `config.json` and its role in CLI/MCP fallback.
- [ ] Run branch formatting (`pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main`) and `just verify` on Windows.
- [ ] Record test revision, test counts, and gate logs in `plans/092-verification-evidence.md`.
- [ ] Update `plans/README.md` status. Commit `docs(092): record default execution timeout verification and update docs`.

## Plan self-review

- Covers the complete end-to-end flow: Core config schema -> strict validation -> CLI & MCP resolution -> Dashboard API -> Dashboard UI.
- All five review-focus items map directly to Tasks 1–3.
- Backward compatibility is preserved for existing `config.json` files without `"execution"`.
- Strict fail-closed validation and hard safety bounds (1..300 s) remain uncompromised.
