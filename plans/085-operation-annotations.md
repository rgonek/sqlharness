# Plan 085: Operation annotations by agents and owner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plans 077, 079 and 084 must be merged. `git diff --stat 6c37cd7..HEAD -- src/SqlHarness.Mcp src/SqlHarness.Core/Journal src/SqlHarness.Dashboard tests/SqlHarness.Mcp.Tests`. Use 077's `McpAgentEnvelope` and budget tests, 084's membership API.

**Goal:** Let agents label and star operations at call time and annotate them afterwards by `operationRef`, scoped to their agent session; let the owner star/note operations and browse named operations in the dashboard.

**Architecture:** One process-start switch (`journal.operationAnnotations`) gates every agent-facing surface: optional `label`/`keep` members, `operationRef` in results, and a new annotation tool; off ⇒ byte-identical output to 077. Scope is resolved server-side from journal data only (084 membership, `agent_session_id`), never from transcripts in the MCP process. Owner annotations live separately and are always available in the dashboard.

**Tech Stack:** .NET 10, MCP SDK, Microsoft.Data.Sqlite, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-17-operation-annotations-design.md](../docs/superpowers/specs/2026-10-17-operation-annotations-design.md)

## Status and dependencies

- IMPLEMENTED on branch `plan-085`; verified product tree `9adc27d`; all task reviews, whole-branch Sol review, and final scoped Sol re-review approved. Local integration into `main` is authorized and pending; no push authorized. Planning base `6c37cd7`. Depends on 077, 079, 084.
- Priority P2; effort M–L; risk MEDIUM (MCP contract; scope rule).

## Global Constraints

- Config `journal.operationAnnotations` (default `false`), read at process start; rejected at startup if the journal is disabled.
- Off: no `label`/`keep` members, no annotation tool, no `operationRef`, no capability member, no description mentions; golden tools/list and result bytes identical to 077.
- On: `label` (≤ 120 Unicode code points; control characters and newlines rejected) and `keep` on `query`, `measure`, `compare`, `watch`, `snapshot` only; `operationRef` envelope field on those tools' results only when a journal row exists, inside the byte budget, never clipped, also in minimal error fallbacks; `schemaVersion` 3.
- Annotation tool input `{ items: [ { operationRef, label?, keep? } ] }`, 1–20 items; omitted = unchanged; `label: ""` clears; idempotent. Per-item result `{ operationRef, tool, label, keep }` or `operation_not_available` (byte-identical for unknown, expired, foreign, out-of-scope, non-annotatable); malformed refs → validation error; batch with failed items keeps `isError=false` with per-item status. The annotation call is journaled, returns no handle, is not a target.
- Scope (matching flag re-read per call): matching off → target in caller's MCP session; caller resolved (Codex/OpenCode `agent_session_id`; Claude only when all exact links of the caller's MCP session point to one conversation) → target exactly linked to that conversation, or in the caller's MCP session with no exact link elsewhere; caller unresolved → target in caller's MCP session with no exact link to another conversation. Inferred membership never authorizes. Authorize + write in one transaction.
- Labels never influence classification, approval or policy; rendered as plain text.
- Tool count pins become "9, or 10 when annotations are on"; tools/list on ≤ 32 KiB.
- Commits `feat(085): …`; `just verify`; completion gate = deterministic byte measurements.

## Review Focus

1. Feature off: tools/list and every result byte-identical to the 077 golden → Task 1 `Off_output_is_byte_identical`.
2. Query then immediate annotate of it (Claude, no link yet) → allowed → Task 3 `Fresh_unlinked_target_in_own_session_is_allowed`.
3. Shared MCP session serving two conversations: conversation B cannot annotate A's linked operations → Task 3 `Other_conversation_target_is_not_available`.
4. Ref of a retention-deleted operation and a foreign ref return identical bytes → Task 3 `Not_available_is_uniform`.
5. `operationRef` never clipped at 4,096-byte budget with large results → Task 2 `Ref_survives_minimum_budget`.

---

### Task 0: Drift check and baseline
- [ ] Confirm 077/079/084 merged; drift check; `just verify`; record base. Capture 077 golden tools/list and representative results for the off-mode tests.

### Task 1: Config, schema members and descriptions
**Files:** `src/SqlHarness.Core/SqlHarnessConfig.cs` (`JournalConfig.OperationAnnotations`), `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (schema member removal via the factory's schema options and allowlist gating, not handler duplication), `McpToolArguments.cs`, `src/SqlHarness.Core/Capabilities.cs` (nullable `operationAnnotations` member omitted when off; bump `ContractVersion`), `src/SqlHarness.Mcp/McpLimits.cs`; tests `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`, catalog/startup tests.
- [ ] Failing tests: `Off_output_is_byte_identical`; on → members present only on the five tools with descriptions ≤ 1 line; unknown `label` when off → rejected; tools/list on ≤ 32 KiB; startup rejects on + journal disabled.
- [ ] FAIL → implement → PASS. Commit `feat(085): gate annotation members behind config`.

### Task 2: operationRef in results and call-time annotations
**Files:** 077's `McpAgentEnvelope`/`McpResultAdapter.cs` (optional field, budget reservation), `src/SqlHarness.Core/Journal/JournalingModule.cs` (`Journaled` flag on outcome), `JournalSchema.cs` step (`operation_annotations(operation_id PK, agent_label_initial, agent_label, agent_keep, owner_starred, owner_note, updated_at)`), `ActivityJournal.cs`; tests `McpResultAdapterTests`, `McpTokenBudgetTests`, journal tests.
- [ ] Failing tests: ref only when journaled and on; `Ref_survives_minimum_budget`; ref present in minimal error fallback; call-time `label`/`keep` stored (initial and current); label validation errors.
- [ ] FAIL → implement → PASS. Commit `feat(085): return operation refs and store call-time annotations`.

### Task 3: Annotation tool and scope resolver
**Files:** create `src/SqlHarness.Core/Journal/AnnotationScope.cs`, `src/SqlHarness.Mcp/Tools/AnnotateHandler.cs`; register in `McpToolCatalog.cs` only when on; tests `tests/SqlHarness.Tests/Journal/AnnotationScopeTests.cs`, `tests/SqlHarness.Mcp.Tests/AnnotateToolTests.cs`.
**Interfaces:** `static ScopeDecision AnnotationScope.Decide(CallerIdentity caller, TargetRow target, MembershipView links, bool matchingOn)`.
- [ ] Failing tests: `Fresh_unlinked_target_in_own_session_is_allowed`; `Other_conversation_target_is_not_available`; cross-MCP-session target in the same Codex conversation allowed; Claude caller with single-conversation session resolves, two-conversation session unresolved; inferred never authorizes; `Not_available_is_uniform` (byte comparison); malformed ref validation; batch of 20 mixed; echo `{operationRef, tool, label, keep}`; mutation/artifact refs not annotatable; matching flag change between calls takes effect.
- [ ] FAIL → implement → PASS. Commit `feat(085): annotate operations within the agent session`.

### Task 4: Dashboard owner annotations and Named operations
**Files:** `src/SqlHarness.Dashboard/DashboardServer.cs` (`PATCH /api/operations/{id}/annotation` owner star/note; `GET /api/named-operations?q=&cursor=`), `JournalReader.cs`; UI `ui/src/pages/NamedOperationsPage.tsx` (new nav item), `OperationPage.tsx` and `OperationTable.tsx` (owner star, agent keep marker distinct, label column); tests alongside.
- [ ] Failing tests: owner star/note independent of the switch; agent keep visually distinct; named view lists labelled, kept or owner-starred operations with search across sessions; reuse actions only with stored SQL, else `SQL not stored`; labels as plain text.
- [ ] FAIL → implement → PASS; build. Commit `feat(085): owner operation annotations and named operations view`.

### Task 5: Contract docs, 077 D1 amendment, verification
- [ ] Document the conditional `operationRef` in 077's contract docs (D1 amendment note), `docs/mcp.md`, README (switch, privacy: labels stored when on regardless of `storeSensitive`), capabilities docs. No skill change unless optional trials show under-labelling.
- [ ] Byte measurements recorded: tools/list on/off, representative results on/off. Optional (operator-authorized): paired agent trials and second-session reuse trial vs work-folder baseline.
- [ ] `just verify`; queue → history; commit `docs(085): record plan 085 evidence`.
