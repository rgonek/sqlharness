# Profile editing and mutation inheritance implementation plan

> **For agentic workers:** Use superpowers:executing-plans or, if explicitly selected by the operator,
> superpowers:subagent-driven-development. Execute task-by-task; checkboxes track completion.

**Goal:** Edit existing profiles in the dashboard and select inherited or explicit mutation policy.
**Architecture:** Extend strict profile configuration with optional mode, a shared effective-policy
resolver, a guarded single-profile save, and a typed form on the existing Profiles page.
**Tech stack:** C#/.NET, System.Text.Json, ASP.NET Core, React/TypeScript, TanStack Query, xUnit, Vitest.
**Spec:** [Editable profiles design](../docs/superpowers/specs/2026-10-29-profile-editing-design.md).
**Status:** DONE; locally integrated into `main` by fast-forward to `1adc4030`. Terra high task reviews and Sol low whole-branch and final scoped reviews approved. Windows `just verify` passed on unchanged product `71a42d2a`: UI 462, Core 4834/11 skipped, MCP 537/4 skipped. Profile Save is Windows-only; non-Windows refuses before file access. No push, live acceptance or Linux gate. [Integration](124-evidence/integration.md).
**Baseline:** `455adea0`, inspected 2026-10-29; working tree initially clean.
**Owner coverage:** Extends the existing profile view and mutation approval design, not plans
109/116 or a new mutation-execution transport. No existing open profile-editing owner found in index.

## Global constraints and STOP conditions

- Read AGENTS.md, this spec, plans/README.md and docs/agents/verification.md before implementation.
- At start compare HEAD and relevant diffs with baseline; reconcile drift before editing.
- Preserve existing profiles and explicit mutation modes; never configure a real user's profiles
  or test live SQL as part of implementation.
- Keep secret values out of UI/API/logs/tests. Use synthetic profiles and environment-variable names.
- Profile writes are operator dashboard actions; approval to save is not approval to execute SQL.
- Keep public evidence anonymous. No private targets, geography, tickets or workload measurements.
- Do not add create/delete/rename, a raw file editor, global writer defaults, automatic restart,
  a new auth strategy or a connection-test action.
- STOP on incompatible existing mode semantics, inability to preserve sibling configuration,
  unresolved auth/TLS validation gaps or security-guard bypass. Resolve the design before weakening it.
- The execution session authorized implementation and local commits; it did not authorize installation, restart, merge, push or publication.

## Review focus

1. Global mode changes after an inherited profile was saved: readiness and production-YOLO checks
   stay current; credentials and sameAsReader are never enabled automatically (Tasks 1, 3).
2. Concurrent editors or external file changes: stale saves do not erase sibling profiles or form
   contents; final noncooperating external-writer race is documented honestly (Tasks 2, 3).
3. A partial/invalid/missing file is never repaired by an ordinary profile Save (Task 2).
4. Auth/engine switches and inactive writer fields do not persist contradictory configuration or
   cause environment-value reads; explicit sameAsReader must be chosen by the operator (Tasks 1–3).
5. Existing MCP state and pending mutation approvals remain scoped to their original identity;
   saved configuration is not advertised as already loaded by every client (Tasks 1, 4).

## Execution order

Tasks 1 -> 2 -> 3 -> 4. Tasks share profile models and API types; implement sequentially.
Each task uses a failing focused test, minimal implementation, passing focused tests and scoped review.
Any commit requires the execution session's authorization.

### Task 1: Optional mode and one effective-policy resolver

**Files**
- Modify: `src/SqlHarness.Core/Targets/TargetProfile.cs`, `ProfileStore.cs`.
- Create: `src/SqlHarness.Core/Mutations/MutationPolicyResolver.cs`.
- Modify: `src/SqlHarness.Core/Capabilities.cs`, dashboard mutation policy consumers
  including `Mutations/MutationExecutor.cs` and submission validation, `DashboardProfiles.cs`.
- Tests: `tests/SqlHarness.Tests/Targets/ProfileStoreTests.cs`,
  `tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs`,
  `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`,
  `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`.

**Interface**
`MutationPolicyResolver.Resolve(TargetProfile profile, MutationsConfig global)` returns an
`EffectiveMutationPolicy` containing nullable ConfiguredMode, EffectiveMode, ModeSource
(global/profile), WriterConfigured and ProductionAcknowledgementRequired.
It resolves metadata, never credentials or connections. Execution enforces readiness.
Nullable `ProfileMutations.Mode` represents inheritance; old explicit strings retain meaning.

- [x] Test absent block, writer-only inherited block, explicit never/operator/yolo, all three global
  modes, missing writer, and global default changes with no profile rewrite.
- [x] Test inherited production/staging YOLO without acknowledgement cannot execute, while explicit
  acknowledged DML and click-only non-DML retain current behavior.
- [x] Run focused xUnit tests and confirm failures exercise the new contract.
- [x] Implement strict validation and resolver; route every effective-mode consumer through it.
  Search all direct Mode fallbacks, including CLI, MCP and broker submission/execution.
- [x] Return configured/effective/source/readiness data to Profiles; keep existing response fields
  compatible and derive mutationMode from effective mode.
- [x] Verify focused tests; no auth/environment values are read by policy resolution.

**Acceptance:** One semantic source for mode resolution; global inheritance and writer choice work
independently; UI/capabilities/execution agree on effective mode.

### Task 2: Strict single-profile persistence and guarded API

**Files**
- Create: `src/SqlHarness.Core/Targets/ProfileWriter.cs`.
- Modify: `src/SqlHarness.Core/Targets/ProfileStore.cs` for in-memory strict validation reuse.
- Modify: `src/SqlHarness.Dashboard/DashboardProfiles.cs`, `DashboardServer.cs`,
  `DashboardSecurity.cs`.
- Tests: existing `DashboardProfilesTests.cs`, `DashboardServerTests.cs`;
  create `tests/SqlHarness.Tests/Targets/ProfileWriterTests.cs`.

**Interfaces**
`ProfileWriter.Update(string path, string name, string expectedRevision, TargetProfile profile)`
returns the new revision or a typed validation/conflict/not-found/I/O outcome.
`DashboardProfiles.PutAsync(HttpContext context, string path, JsonSerializerOptions json)`
maps outcomes to the spec's HTTP statuses. PUT /api/profiles accepts the exact body in the spec.
GET includes revision plus the complete editable configuration; no environment values.

- [x] Test successful one-profile round trip, preserved siblings, writer-only inheritance and
  existing auth/TLS/template/variable validations.
- [x] Test stale revision, deleted profile, invalid/missing document, duplicate/unknown JSON members,
  oversized body, cancelled request, write/replace failures and temp-file cleanup.
- [x] Test endpoint rejects missing write header, bad Origin/Host and unauthenticated access;
  authorization occurs before file access. No path supplied by caller is accepted.
- [x] Run focused tests to demonstrate failures, implement the locked/rechecked atomic writer and
  API, then rerun. Preserve unrelated fields and sibling profile content.
- [x] Verify successful save returns the new persisted profile/revision; no connection or mutation
  submission occurs and error output contains no secrets or raw file content.

**Acceptance:** A stale or invalid request cannot overwrite another profile or damage targets.json.

### Task 3: Existing-profile edit form with policy provenance

**Files**
- Modify: `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx`, `ProfilesPage.test.tsx`.
- Create: `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx`.
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `client.ts`, `queries.ts`;
  extend `api/client.test.ts`.

**Interface**
Add typed `updateProfile({ name, expectedRevision, profile })` using existing guarded-write
client conventions. On success invalidate Profiles; retain form values on 400/409/500.
Mutation mode form values are inherit/never/operator/yolo; inherit serializes an absent mode,
not the effective global string. Writer is separately unconfigured/sameAsReader/separate.

- [x] Add failing component tests for Edit/Save/Cancel, immutable name, all editable field groups,
  variable editing and field-specific errors.
- [x] Test inherited mode displays current global/effective mode, preserves writer on mode change,
  shows missing-writer readiness and production-YOLO acknowledgement.
- [x] Test dirty navigation confirmation, unchanged state after Cancel, stale-save form retention,
  accessible labels/focus and inactive writer-field cleanup.
- [x] Implement the form using existing components and layout. State that YOLO skips approval only
  for DML; do not show the current overbroad no-approval description.
- [x] Run `npm test -- ProfilesPage.test.tsx client.test.ts` and `npm run typecheck` in the UI
  directory; include the new form tests if split into a separate test file.

**Acceptance:** An operator can configure inherited operator mode plus explicit sameAsReader,
save, reopen and see the same choice without editing JSON or exposing a secret.

### Task 4: Runtime boundaries, documentation and final verification

**Files**
- Update: `README.md`, `docs/mcp/tools.md`, `docs/mcp/startup.md` where profile reload and
  mutation-policy metadata are described; update affected API contract examples.
- Tests: existing mutation broker/executor and MCP mapping tests, dashboard security/API tests.
- Update this plan and `plans/README.md` with actual evidence and status.

- [x] Test a prepared request whose target changes before execution, inherited global-policy changes,
  and the startup-frozen MCP capability projection. Target mismatch fails closed before dispatch;
  full broker pending-approval lifecycle and live MCP transport remain unverified.
- [x] Document dashboard reload vs startup-frozen clients; Save success must not claim live MCP
  activation. Do not change lifecycle behavior to hide stale configuration.
- [x] Run focused tests via `dotnet test tests/SqlHarness.Tests --filter
  "FullyQualifiedName~Profile|FullyQualifiedName~Mutation|FullyQualifiedName~Capabilities"`
  and corresponding MCP mapping tests. Resolve actual failures rather than weakening assertions.
- [x] Run `just verify` and required scoped formatting checks; [evidence](124-evidence/verification.md) records the final exact product revision and earlier historical gate boundary.
- [x] Verify the UI with synthetic fixtures: inherited and explicit policy, missing writer,
  separate writer, production acknowledgement, conflict and keyboard-only Save/Cancel.
  No real profile writes, database access or restart.
- [x] Record results and unresolved limits, review complete scoped diff and privacy, then update
  plan/index status. Do not mark installed/live behavior verified from local tests alone.

**Acceptance:** Existing profiles remain compatible; synthetic end-to-end editing and guard tests
pass; new policy semantics, activation limits and operator controls are documented.



Final whole-branch review found a draft-loss refresh path and a browser assertion gap. Final fix retains the opening snapshot across refresh states; scoped Sol re-review approved both fixes without new findings. See [verification](124-evidence/verification.md) and [review archive](124-evidence/reviews-and-checks/whole-branch-review.md).
